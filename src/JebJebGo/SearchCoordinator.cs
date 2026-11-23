using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using HarmonyLib;
using JebJebGo.Core;
using KSP.UI.Screens;
using UnityEngine;
using UnityEngine.UI;

namespace JebJebGo
{
    /// <summary>
    /// JebJebGo's replacement for the stock search coroutine. It keeps the
    /// stock contract the rest of the game (and the test harness) relies on:
    /// the same keystroke debounce, results applied through the stock
    /// SearchFilterResult (so exclude, availability, and sort stay stock
    /// semantics), the field turning green, and searchRoutine cleared at the end.
    ///
    /// Matching runs on a worker thread over an immutable snapshot of the loaded
    /// parts; the main thread only polls each frame. A result is applied only if
    /// no newer keystroke and no newer search routine arrived meanwhile;
    /// otherwise the debounce restarts from the latest keystroke. Any failure
    /// disables JebJebGo for the session and finishes with the stock matcher.
    /// </summary>
    internal static class SearchCoordinator
    {
        private static AccessTools.FieldRef<BasePartCategorizer, float> searchTimer;
        private static AccessTools.FieldRef<BasePartCategorizer, Image> searchFieldBackground;
        private static AccessTools.FieldRef<BasePartCategorizer, Coroutine> searchRoutine;
        private static Action<BasePartCategorizer, EditorPartListFilter<AvailablePart>> searchFilterResult;
        private static Func<BasePartCategorizer, AvailablePart, string[], bool> stockMatches;

        private static readonly StockCompatibleMatcher StockEngine = new StockCompatibleMatcher();
        private static readonly SmartSearchEngine SmartEngine = new SmartSearchEngine();
        private static SnapshotCache snapshot;
        private static int generation;
        private static int mainThreadId;

        public static void Initialize()
        {
            mainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            searchTimer = AccessTools.FieldRefAccess<BasePartCategorizer, float>("searchTimer");
            searchFieldBackground = AccessTools.FieldRefAccess<BasePartCategorizer, Image>("searchFieldBackground");
            searchRoutine = AccessTools.FieldRefAccess<BasePartCategorizer, Coroutine>("searchRoutine");
            System.Reflection.MethodInfo filterResult = AccessTools.Method(typeof(BasePartCategorizer), "SearchFilterResult")
                ?? throw new MissingMethodException("BasePartCategorizer.SearchFilterResult not found");
            // Invoke virtually so PartCategorizer's override runs, as in stock.
            searchFilterResult = (categorizer, filter) => filterResult.Invoke(categorizer, new object[] { filter });
            System.Reflection.MethodInfo matches = AccessTools.Method(typeof(BasePartCategorizer), "PartMatchesSearch", new[] { typeof(AvailablePart), typeof(string[]) })
                ?? throw new MissingMethodException("BasePartCategorizer.PartMatchesSearch not found");
            stockMatches = (categorizer, part, terms) => (bool)matches.Invoke(categorizer, new object[] { part, terms });
            // Build the snapshot and index once, at the main menu after parts load, and
            // keep them across scenes (rebuilt only if the loaded part list changes):
            // building allocates heavily, and the garbage collection it triggers stops
            // every thread, so it should not land in the editor.
            GameEvents.onLevelWasLoadedGUIReady.Add(scene =>
            {
                if (scene == GameScenes.MAINMENU && JebJebGoStatus.Enabled)
                {
                    CurrentSnapshot();
                }
            });
            // KSP's EventVoid.Add reads the delegate target, so a static method group throws; a lambda has one.
            GameEvents.onEditorStarted.Add(() => OnEditorStarted());
            // Settings changed in the game's settings (JebJebGo tab): show the list in the new order now.
            GameEvents.OnGameSettingsApplied.Add(() => OnSettingsApplied());
        }

        private static void OnSettingsApplied()
        {
            try
            {
                if (JebJebGoStatus.Enabled && HighLogic.LoadedSceneIsEditor && EditorPartList.Instance != null)
                {
                    EditorPartList.Instance.Refresh();
                }
            }
            catch (Exception error)
            {
                Log.Warning("could not refresh the parts list after a settings change: " + error.Message);
            }
        }

        /// <summary>Prepare the snapshot (worker thread) and sort ranks while the editor opens.</summary>
        private static void OnEditorStarted()
        {
            JebJebGoStatus.EditorVisits++;
            if (!JebJebGoStatus.Enabled)
            {
                return;
            }
            try
            {
                CurrentSnapshot();
                if (JebJebGoStatus.VirtualList)
                {
                    PartOrder.Prewarm(EditorPartList.Instance);
                }
            }
            catch (Exception error)
            {
                Log.Warning("could not prepare the search snapshot early: " + error.Message);
            }
        }

        public static IEnumerator Run(BasePartCategorizer categorizer)
        {
            IEnumerator inner = RunSteps(categorizer);
            while (true)
            {
                Stopwatch step = Stopwatch.StartNew();
                bool more;
                try
                {
                    more = inner.MoveNext();
                }
                catch (Exception error)
                {
                    // Anything thrown while applying results (including other mods' filters):
                    // stock search from now on, and leave the field in its finished state.
                    Disable(error);
                    TryFinishWithStock(categorizer);
                    yield break;
                }
                finally
                {
                    MainThreadCost.Record("SearchCoordinator step", step);
                }
                if (!more)
                {
                    yield break;
                }
                yield return inner.Current;
            }
        }

        private static IEnumerator RunSteps(BasePartCategorizer categorizer)
        {
            int myGeneration = ++generation;
            while (true)
            {
                while (searchTimer(categorizer) + categorizer.searchKeystrokeDelay > Time.realtimeSinceStartup)
                {
                    yield return null;
                }

                string query = categorizer.searchField.text;
                float startedAt = searchTimer(categorizer);
                Task<SearchOutcome> job;
                try
                {
                    job = StartJob(query);
                }
                catch (Exception error)
                {
                    Disable(error);
                    FinishWithStock(categorizer, query);
                    yield break;
                }

                while (!job.IsCompleted)
                {
                    yield return null;
                }

                if (myGeneration != generation || categorizer == null)
                {
                    yield break; // a newer search routine owns the field now
                }
                if (searchTimer(categorizer) != startedAt || categorizer.searchField.text != query)
                {
                    if (categorizer.searchField.text.Length == 0)
                    {
                        yield break; // cleared meanwhile; stock SearchStop already restored the list
                    }
                    continue; // superseded by newer keystrokes: debounce again from the latest one
                }
                if (job.IsFaulted)
                {
                    Disable(job.Exception?.InnerException ?? job.Exception);
                    FinishWithStock(categorizer, query);
                    yield break;
                }

                SearchOutcome outcome = job.Result;
                JebJebGoStatus.SearchesCompleted++;
                EditorPartListFilter<AvailablePart> filter = new EditorPartListFilter<AvailablePart>("SearchFilter_", outcome.Matches.Contains);
                SearchRanking.Set(filter, outcome.Rank);
                Stopwatch apply = Stopwatch.StartNew();
                JebJebGoStatus.ThrowIfInjected("SearchApply");
                searchFilterResult(categorizer, filter);
                JebJebGoStatus.LastApplyMs = apply.Elapsed.TotalMilliseconds;
                searchFieldBackground(categorizer).color = Color.green;
                searchRoutine(categorizer) = null;
                yield break;
            }
        }

        private static Task<SearchOutcome> StartJob(string query)
        {
            SnapshotCache cache = CurrentSnapshot();
            bool smart = JebJebGoStatus.Mode != "parity";
            return cache.Ready.ContinueWith(ready =>
            {
                Stopwatch clock = Stopwatch.StartNew();
                PartSnapshot built = ready.Result;
                List<int> results = new List<int>(256);
                if (smart)
                {
                    SmartEngine.Search(built, query, results);
                }
                else
                {
                    StockEngine.Search(built, query, results);
                }
                HashSet<AvailablePart> matches = new HashSet<AvailablePart>();
                Dictionary<AvailablePart, int> rank = smart ? new Dictionary<AvailablePart, int>(results.Count) : null;
                for (int i = 0; i < results.Count; i++)
                {
                    AvailablePart part = cache.Parts[results[i]];
                    matches.Add(part);
                    if (rank != null)
                    {
                        rank[part] = i;
                    }
                }
                JebJebGoStatus.LastMatchMs = clock.Elapsed.TotalMilliseconds;
                JebJebGoStatus.LastMatchOnMainThread = System.Threading.Thread.CurrentThread.ManagedThreadId == mainThreadId;
                return new SearchOutcome(matches, rank);
            },
            // Never ExecuteSynchronously: with an already finished snapshot that runs
            // the match inline on the calling (main) thread.
            System.Threading.CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }

        private sealed class SearchOutcome
        {
            public readonly HashSet<AvailablePart> Matches;
            public readonly Dictionary<AvailablePart, int> Rank;

            public SearchOutcome(HashSet<AvailablePart> matches, Dictionary<AvailablePart, int> rank)
            {
                Matches = matches;
                Rank = rank;
            }
        }

        private static void FinishWithStock(BasePartCategorizer categorizer, string query)
        {
            string[] terms = BasePartCategorizer.SearchTagSplit(query);
            searchFilterResult(categorizer, new EditorPartListFilter<AvailablePart>("SearchFilter_", p => stockMatches(categorizer, p, terms)));
            searchFieldBackground(categorizer).color = Color.green;
            searchRoutine(categorizer) = null;
        }

        private static void TryFinishWithStock(BasePartCategorizer categorizer)
        {
            try
            {
                if (categorizer != null)
                {
                    FinishWithStock(categorizer, categorizer.searchField.text);
                }
            }
            catch (Exception error)
            {
                Log.Error("stock search fallback failed too: " + error.Message);
                if (categorizer != null)
                {
                    searchRoutine(categorizer) = null;
                }
            }
        }

        private static SnapshotCache CurrentSnapshot()
        {
            List<AvailablePart> loaded = PartLoader.LoadedPartsList;
            if (snapshot == null || !snapshot.Matches(loaded) || snapshot.Ready.IsFaulted)
            {
                snapshot = SnapshotCache.Start(loaded);
            }
            return snapshot;
        }

        private static void Disable(Exception error)
        {
            JebJebGoStatus.Enabled = false;
            JebJebGoStatus.LastError = error?.ToString();
            Log.Error("search failed; falling back to the stock search for this session: " + error);
        }

        /// <summary>
        /// The parts as plain data, copied on the main thread (cheap), with the
        /// expensive part (parsing every part's tags) done on a worker thread.
        /// </summary>
        private sealed class SnapshotCache
        {
            public readonly List<AvailablePart> Source;
            public readonly int SourceCount;
            public readonly AvailablePart[] Parts;
            public readonly Task<PartSnapshot> Ready;

            private SnapshotCache(List<AvailablePart> source, AvailablePart[] parts, Task<PartSnapshot> ready)
            {
                Source = source;
                SourceCount = source.Count;
                Parts = parts;
                Ready = ready;
            }

            /// <summary>
            /// Whether the loaded parts are still exactly the snapshot's. A database reload
            /// (debug toolbar Database > Reload) refills the same list, with the same count,
            /// with new part objects, so every reference is compared (a few microseconds).
            /// </summary>
            public bool Matches(List<AvailablePart> loaded)
            {
                if (!ReferenceEquals(loaded, Source) || loaded.Count != SourceCount)
                {
                    return false;
                }
                for (int i = 0; i < Parts.Length; i++)
                {
                    if (!ReferenceEquals(loaded[i], Parts[i]))
                    {
                        return false;
                    }
                }
                return true;
            }

            public static SnapshotCache Start(List<AvailablePart> loaded)
            {
                Stopwatch copy = Stopwatch.StartNew();
                AvailablePart[] parts = loaded.ToArray();
                PartInput[] inputs = new PartInput[parts.Length];
                for (int i = 0; i < parts.Length; i++)
                {
                    AvailablePart part = parts[i];
                    inputs[i] = new PartInput(part.name, part.tags, part.category == PartCategories.none, part.title, part.manufacturer);
                }
                double copyMs = copy.Elapsed.TotalMilliseconds;
                Task<PartSnapshot> ready = Task.Run(() =>
                {
                    Stopwatch build = Stopwatch.StartNew();
                    PartSnapshot built = PartSnapshot.Build(inputs);
                    SmartEngine.Prepare(built); // the smart index, also on the worker
                    JebJebGoStatus.SnapshotParts = parts.Length;
                    JebJebGoStatus.LastSnapshotBuildMs = build.Elapsed.TotalMilliseconds;
                    return built;
                });
                Log.Info("search snapshot: copied " + parts.Length + " parts in " + copyMs.ToString("0.0") + " ms on the main thread; building on a worker");
                return new SnapshotCache(loaded, parts, ready);
            }
        }
    }
}
