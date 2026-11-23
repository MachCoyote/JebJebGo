using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using HarmonyLib;
using KSP.UI.Screens;
using UnityEngine;
using UnityEngine.Profiling;

namespace JebJebGo
{
    /// <summary>
    /// Field diagnostics for editor freezes that the dev instances do not show.
    ///
    /// Every editor frame slower than <see cref="ThresholdMs"/> logs one line:
    /// JebJebGo's own slowest main-thread hook around it, the whole stock
    /// EditorPartList.Refresh() measured from outside (every mod's patches on it
    /// included), the frame's phases (<see cref="FramePhases"/>), garbage
    /// collections and heap, icons created, the switches, the editor visit, and
    /// the flight scenes loaded so far. Each editor visit also logs the Harmony
    /// patches on the parts list and categorizer (with owners), the methods
    /// patched since the previous visit, and the size of the persistent UI
    /// canvases. Rate-limited and capped per session; -jebjebgo-no-hitch-log
    /// turns it all off.
    /// </summary>
    internal sealed class EditorHitchLog : MonoBehaviour
    {
        private const double ThresholdMs = 100.0;
        private const float MinSecondsBetweenLines = 0.5f;
        private const int MaxLinesPerSession = 300;
        private const int MaxNewPatchLines = 40;

        private static readonly Type[] WatchedTypes =
        {
            typeof(EditorPartList), typeof(EditorPartIcon), typeof(PartCategorizer), typeof(BasePartCategorizer),
            typeof(PartCategorizerButton), typeof(PartCategorizer.Category),
        };

        private static long refreshStart;
        private static int refreshDepth;
        private static double refreshFrameMaxMs;
        private static HashSet<MethodBase> patchedAtLastVisit;

        private int lastGcCount;
        private int lastIconsCreated;
        private int linesWritten;
        private float lastLineTime = -10f;
        private int flightsLoaded;
        private int editorVisits;
        private bool inEditor;
        private int frameInEditor;

        public static bool Enabled { get; } = Array.IndexOf(Environment.GetCommandLineArgs(), "-jebjebgo-no-hitch-log") < 0;

        /// <summary>Time the whole stock refresh, outermost: first prefix, last postfix.</summary>
        public static void Install(Harmony harmony, GameObject host)
        {
            MethodInfo refresh = AccessTools.Method(typeof(EditorPartList), "Refresh", Type.EmptyTypes)
                ?? throw new MissingMethodException("EditorPartList.Refresh() not found");
            harmony.Patch(refresh,
                prefix: new HarmonyMethod(typeof(EditorHitchLog), nameof(RefreshFirst)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(EditorHitchLog), nameof(RefreshLast)) { priority = Priority.Last });
            FramePhases.Install(host);
            host.AddComponent<EditorHitchLog>();
        }

        public static void RefreshFirst()
        {
            if (refreshDepth++ == 0)
            {
                refreshStart = Stopwatch.GetTimestamp();
            }
        }

        public static void RefreshLast()
        {
            if (refreshDepth > 0 && --refreshDepth == 0)
            {
                double ms = (Stopwatch.GetTimestamp() - refreshStart) * 1000.0 / Stopwatch.Frequency;
                if (ms > refreshFrameMaxMs)
                {
                    refreshFrameMaxMs = ms;
                }
            }
        }

        private void Awake()
        {
            GameEvents.onLevelWasLoadedGUIReady.Add(scene => OnSceneReady(scene));
        }

        private void OnSceneReady(GameScenes scene)
        {
            refreshDepth = 0;
            if (scene == GameScenes.FLIGHT)
            {
                flightsLoaded++;
            }
            inEditor = scene == GameScenes.EDITOR;
            if (inEditor)
            {
                editorVisits++;
                frameInEditor = 0;
                try
                {
                    LogPatches();
                    LogCanvases();
                }
                catch (Exception error)
                {
                    Log.Warning("editor diagnostics failed: " + error.Message);
                }
            }
        }

        private void Update()
        {
            try
            {
                Tick();
            }
            catch (Exception error)
            {
                enabled = false;
                Log.Warning("editor hitch log stopped after an error: " + error.Message);
            }
        }

        private void Tick()
        {
            // Duration of the previous frame; JebJebGo's cost recorded since the last Update covers it.
            double frameMs = Time.unscaledDeltaTime * 1000.0;
            double jebjebgoMs = MainThreadCost.FrameMaxMs;
            string where = MainThreadCost.FrameMaxWhere;
            MainThreadCost.ResetFrame();
            double refreshMs = refreshFrameMaxMs;
            refreshFrameMaxMs = 0;
            int gcCount = GC.CollectionCount(0);
            int gcDelta = gcCount - lastGcCount;
            lastGcCount = gcCount;
            int iconsDelta = JebJebGoStatus.IconsCreated - lastIconsCreated;
            lastIconsCreated = JebJebGoStatus.IconsCreated;

            if (!inEditor || HighLogic.LoadedScene != GameScenes.EDITOR)
            {
                return;
            }
            // The first frames after the scene loads are the scene load itself.
            if (++frameInEditor < 10 || frameMs < ThresholdMs || linesWritten >= MaxLinesPerSession
                || Time.realtimeSinceStartup - lastLineTime < MinSecondsBetweenLines)
            {
                return;
            }
            lastLineTime = Time.realtimeSinceStartup;
            linesWritten++;
            JebJebGoStatus.HitchesLogged = linesWritten;
            Log.Info("editor hitch " + frameMs.ToString("0") + " ms: JebJebGo main thread " + jebjebgoMs.ToString("0.0") + " ms"
                + (where != null ? " (" + where + ")" : string.Empty)
                + ", stock refresh incl. all mods " + refreshMs.ToString("0.0") + " ms"
                + " (last exclude filters pass " + JebJebGoStatus.LastExcludeFiltersMs.ToString("0.0") + " ms)"
                + ", phases ms [" + FramePhases.Describe() + "]"
                + ", GC +" + gcDelta + " (heap " + (Profiler.GetMonoUsedSizeLong() / 1048576) + "/" + (Profiler.GetMonoHeapSizeLong() / 1048576) + " MB)"
                + ", icons created " + iconsDelta + ", pending " + JebJebGoStatus.PendingIcons
                + ", enabled " + JebJebGoStatus.Enabled + ", virtual list " + JebJebGoStatus.VirtualList
                + ", editor visit " + editorVisits + ", flights so far " + flightsLoaded
                + (linesWritten == MaxLinesPerSession ? " (last hitch line this session)" : string.Empty));
        }

        /// <summary>Harmony patches on the parts list and categorizer, and every method patched since the last editor visit.</summary>
        private void LogPatches()
        {
            HashSet<MethodBase> patched = new HashSet<MethodBase>(Harmony.GetAllPatchedMethods());
            StringBuilder watched = new StringBuilder();
            foreach (MethodBase method in patched)
            {
                if (Array.IndexOf(WatchedTypes, method.DeclaringType) >= 0)
                {
                    watched.Append("\n  ").Append(Describe(method));
                }
            }
            Log.Info("editor visit " + editorVisits + " (flights so far " + flightsLoaded + "): " + patched.Count
                + " methods patched by Harmony; on the parts list and categorizer:" + watched);
            if (patchedAtLastVisit != null)
            {
                StringBuilder added = new StringBuilder();
                int count = 0;
                foreach (MethodBase method in patched)
                {
                    if (!patchedAtLastVisit.Contains(method) && count++ < MaxNewPatchLines)
                    {
                        added.Append("\n  ").Append(Describe(method));
                    }
                }
                Log.Info("methods patched since the previous editor visit: " + count + (count > MaxNewPatchLines ? " (first " + MaxNewPatchLines + ")" : string.Empty) + added);
            }
            patchedAtLastVisit = patched;
        }

        private static string Describe(MethodBase method)
        {
            Patches info = Harmony.GetPatchInfo(method);
            StringBuilder text = new StringBuilder(method.DeclaringType?.Name).Append('.').Append(method.Name).Append(':');
            Append(text, "prefix", info?.Prefixes);
            Append(text, "postfix", info?.Postfixes);
            Append(text, "transpiler", info?.Transpilers);
            Append(text, "finalizer", info?.Finalizers);
            return text.ToString();
        }

        private static void Append(StringBuilder text, string kind, IEnumerable<Patch> patches)
        {
            if (patches == null)
            {
                return;
            }
            foreach (Patch patch in patches)
            {
                text.Append(' ').Append(kind).Append('=').Append(patch.owner);
            }
        }

        /// <summary>Persistent UI canvases (they survive scene changes): leftovers make every UI rebuild slower.</summary>
        private void LogCanvases()
        {
            StringBuilder text = new StringBuilder("persistent UI canvases (editor visit " + editorVisits + "):");
            foreach (Canvas canvas in FindObjectsOfType<Canvas>())
            {
                if (!canvas.isRootCanvas || canvas.gameObject.scene.name != "DontDestroyOnLoad")
                {
                    continue;
                }
                int transforms = canvas.GetComponentsInChildren<Transform>(true).Length;
                if (transforms < 100)
                {
                    continue;
                }
                int active = canvas.GetComponentsInChildren<UnityEngine.UI.Graphic>(false).Length;
                text.Append(' ').Append(canvas.name).Append(" (").Append(transforms).Append(" objects, ").Append(active).Append(" active graphics)");
            }
            Log.Info(text.ToString());
        }
    }
}
