using System;
using System.Reflection;
using HarmonyLib;
using KSP.UI.Screens;
using UnityEngine;

namespace JebJebGo
{
    /// <summary>
    /// Entry point: applies the Harmony patches once at startup. If patching
    /// fails, JebJebGo stays out of the way and the stock search runs.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class JebJebGoAddon : MonoBehaviour
    {
        internal const string HarmonyId = "jebjebgo";

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            bool retooled = Type.GetType("KSP.Retooled.MCP.McpHost, Assembly-CSharp") != null;
            Log.Info("v" + JebJebGoStatus.Version + " on KSP " + Versioning.version_major + "." + Versioning.version_minor + "." + Versioning.Revision
                + (retooled ? " Retooled" : string.Empty) + ", Unity " + Application.unityVersion
                + (JebJebGoStatus.Enabled ? string.Empty : " (disabled by -jebjebgo-disable)"));
            try
            {
                Harmony harmony = new Harmony(HarmonyId);
                MethodInfo searchRoutine = AccessTools.Method(typeof(BasePartCategorizer), "SearchRoutine")
                    ?? throw new MissingMethodException("BasePartCategorizer.SearchRoutine not found");
                harmony.Patch(searchRoutine, prefix: new HarmonyMethod(typeof(SearchRoutinePatch), nameof(SearchRoutinePatch.Prefix)));
                SearchCoordinator.Initialize();
                VirtualPartListPatch.Install(harmony);
                ListRefreshPatch.Install(harmony);
                JebJebGoStatus.Patched = true;
                JanitorsClosetFilterFix.Install(harmony);
                if (EditorHitchLog.Enabled)
                {
                    EditorHitchLog.Install(harmony, gameObject);
                }
                Log.Info("patched BasePartCategorizer.SearchRoutine and EditorPartList.UpdatePartIcons, ClearAllItems, RefreshSearchList, RefreshPartList, RefreshCustomPartList; mode "
                    + JebJebGoStatus.Mode + ", virtual list " + (JebJebGoStatus.VirtualList ? "on" : "off"));
            }
            catch (Exception error)
            {
                JebJebGoStatus.Patched = false;
                JebJebGoStatus.Enabled = false;
                JebJebGoStatus.LastError = error.ToString();
                Log.Error("could not install search patches; stock search stays active: " + error);
            }
        }
    }
}
