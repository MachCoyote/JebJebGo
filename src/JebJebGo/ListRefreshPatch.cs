using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using KSP.UI.Screens;

namespace JebJebGo
{
    /// <summary>
    /// Harmony prefixes replacing EditorPartList.RefreshSearchList,
    /// RefreshPartList, and RefreshCustomPartList (identical on Retooled and
    /// stock 1.12.5 apart from the sort call) when JebJebGo's list is enabled.
    /// The filters and their order are stock; only sorting differs: results are
    /// ordered by <see cref="PartOrder"/> ranks instead of re-sorting them with
    /// the comparer on every refresh. Then the (virtual) UpdatePartIcons runs.
    /// </summary>
    internal static class ListRefreshPatch
    {
        private static AccessTools.FieldRef<EditorPartList, List<AvailablePart>> categoryList;
        private static Action<EditorPartList, bool> updatePartIcons;

        private enum Kind
        {
            Search,
            Category,
            CustomCategory,
        }

        public static void Install(Harmony harmony)
        {
            categoryList = AccessTools.FieldRefAccess<EditorPartList, List<AvailablePart>>("categoryList");
            MethodInfo update = AccessTools.Method(typeof(EditorPartList), "UpdatePartIcons", new[] { typeof(bool) })
                ?? throw new MissingMethodException("EditorPartList.UpdatePartIcons(bool) not found");
            updatePartIcons = (list, custom) => update.Invoke(list, new object[] { custom });
            PartOrder.Initialize();
            Patch(harmony, "RefreshSearchList", nameof(RefreshSearchListPrefix));
            Patch(harmony, "RefreshPartList", nameof(RefreshPartListPrefix));
            Patch(harmony, "RefreshCustomPartList", nameof(RefreshCustomPartListPrefix));
        }

        public static bool RefreshSearchListPrefix(EditorPartList __instance)
        {
            return Run(__instance, Kind.Search);
        }

        public static bool RefreshPartListPrefix(EditorPartList __instance)
        {
            return Run(__instance, Kind.Category);
        }

        public static bool RefreshCustomPartListPrefix(EditorPartList __instance)
        {
            return Run(__instance, Kind.CustomCategory);
        }

        private static void Patch(Harmony harmony, string method, string prefix)
        {
            // Last, so other mods' prefixes on the same refresh (VAB Organizer clears
            // its subcategories and switches its sorters there) still run: a prefix
            // that returns false skips the prefixes ordered after it.
            harmony.Patch(AccessTools.Method(typeof(EditorPartList), method)
                    ?? throw new MissingMethodException("EditorPartList." + method + " not found"),
                prefix: new HarmonyMethod(typeof(ListRefreshPatch), prefix) { priority = Priority.Last });
        }

        private static bool Run(EditorPartList list, Kind kind)
        {
            if (!JebJebGoStatus.Enabled || !JebJebGoStatus.VirtualList)
            {
                return true;
            }
            List<AvailablePart> parts;
            try
            {
                Stopwatch clock = Stopwatch.StartNew();
                parts = SlowFilterReport.ApplyExcludeFilters(list.ExcludeFilters, PartLoader.LoadedPartsList);
                EditorPartListFilter<AvailablePart>.FilterList(parts, list.AmountAvailableFilter.FilterCriteria);
                if (kind == Kind.Search)
                {
                    EditorPartListFilter<AvailablePart>.FilterList(parts, list.SearchFilterParts.FilterCriteria);
                }
                else
                {
                    list.CategorizerFilters.FilterList(parts);
                }
                Dictionary<AvailablePart, int> relevance = kind == Kind.Search && JebJebGoStatus.RelevanceOrder
                    ? SearchRanking.For(list.SearchFilterParts)
                    : null;
                if (relevance != null)
                {
                    PartOrder.SortByRank(parts, relevance);
                }
                else
                {
                    PartOrder.Sort(list, parts);
                }
                JebJebGoStatus.LastRefreshFilterSortMs = clock.Elapsed.TotalMilliseconds;
            }
            catch (Exception error)
            {
                JebJebGoStatus.VirtualList = false;
                JebJebGoStatus.LastError = error.ToString();
                Log.Error("list refresh failed; using the stock list for this session: " + error);
                return true;
            }
            categoryList(list) = parts;
            Stopwatch total = Stopwatch.StartNew();
            updatePartIcons(list, kind == Kind.CustomCategory);
            MainThreadCost.Record("ListRefresh " + kind, total);
            return false;
        }
    }

    /// <summary>
    /// Sorts list contents like the game's active comparer
    /// (EditorPartList.currentPartSorting) without comparing on every refresh:
    /// all loaded parts are sorted once per comparer with the game's own sort,
    /// and each refresh orders its parts by those ranks (an integer key sort).
    /// The result is ordered under the comparer; among parts the comparer ranks
    /// equal the order may differ from a fresh stock sort, which is unstable.
    /// Small lists use the stock sort directly.
    /// </summary>
    internal static class PartOrder
    {
        private const int StockSortBelow = 128;

        private static AccessTools.FieldRef<EditorPartList, IComparer<AvailablePart>> currentSorting;
        private static Type retooledSortingType;
        private static MethodInfo retooledSort;

        private static object rankedComparer;
        private static List<AvailablePart> rankedSource;
        private static int rankedCount;
        private static readonly Dictionary<AvailablePart, int> Rank = new Dictionary<AvailablePart, int>();
        private static int[] keys = new int[0];
        private static AvailablePart[] items = new AvailablePart[0];

        public static void Initialize()
        {
            currentSorting = AccessTools.FieldRefAccess<EditorPartList, IComparer<AvailablePart>>("currentPartSorting");
            // Retooled sorts through PartListSorting.Sort(List<AvailablePart>); stock 1.12.5 has no such type.
            retooledSortingType = AccessTools.TypeByName("KSP.UI.Screens.PartListSorting") ?? AccessTools.TypeByName("PartListSorting");
            if (retooledSortingType != null)
            {
                retooledSort = AccessTools.Method(retooledSortingType, "Sort", new[] { typeof(List<AvailablePart>) });
            }
            GameEvents.onGameSceneLoadRequested.Add(_ => Invalidate());
        }

        public static void Sort(EditorPartList list, List<AvailablePart> parts)
        {
            IComparer<AvailablePart> comparer = currentSorting(list);
            if (parts.Count < StockSortBelow)
            {
                StockSort(comparer, parts);
                return;
            }
            EnsureRanks(comparer);
            int count = parts.Count;
            if (keys.Length < count)
            {
                keys = new int[count];
                items = new AvailablePart[count];
            }
            for (int i = 0; i < count; i++)
            {
                AvailablePart part = parts[i];
                if (!Rank.TryGetValue(part, out int rank))
                {
                    // A part the ranking does not know (loaded later): rebuild and fall back once.
                    Invalidate();
                    StockSort(comparer, parts);
                    return;
                }
                keys[i] = rank;
                items[i] = part;
            }
            Array.Sort(keys, items, 0, count);
            for (int i = 0; i < count; i++)
            {
                parts[i] = items[i];
                items[i] = null;
            }
        }

        /// <summary>Order parts by a precomputed rank (relevance); unranked parts go last, in their current order.</summary>
        public static void SortByRank(List<AvailablePart> parts, Dictionary<AvailablePart, int> ranks)
        {
            int count = parts.Count;
            if (keys.Length < count)
            {
                keys = new int[count];
                items = new AvailablePart[count];
            }
            for (int i = 0; i < count; i++)
            {
                AvailablePart part = parts[i];
                keys[i] = ranks.TryGetValue(part, out int rank) ? rank : ranks.Count + i;
                items[i] = part;
            }
            Array.Sort(keys, items, 0, count);
            for (int i = 0; i < count; i++)
            {
                parts[i] = items[i];
                items[i] = null;
            }
        }

        /// <summary>Build ranks for the list's current comparer now (for example while the editor loads).</summary>
        public static void Prewarm(EditorPartList list)
        {
            if (list != null)
            {
                EnsureRanks(currentSorting(list));
            }
        }

        private static void EnsureRanks(IComparer<AvailablePart> comparer)
        {
            List<AvailablePart> loaded = PartLoader.LoadedPartsList;
            if (ReferenceEquals(comparer, rankedComparer) && ReferenceEquals(loaded, rankedSource) && loaded.Count == rankedCount)
            {
                return;
            }
            Stopwatch clock = Stopwatch.StartNew();
            List<AvailablePart> sorted = new List<AvailablePart>(loaded);
            StockSort(comparer, sorted);
            Rank.Clear();
            for (int i = 0; i < sorted.Count; i++)
            {
                Rank[sorted[i]] = i;
            }
            rankedComparer = comparer;
            rankedSource = loaded;
            rankedCount = loaded.Count;
            JebJebGoStatus.LastRankBuildMs = clock.Elapsed.TotalMilliseconds;
            Log.Info("part order ranks for " + comparer.GetType().Name + ": " + sorted.Count + " parts in " + clock.Elapsed.TotalMilliseconds.ToString("0.0") + " ms");
        }

        private static void StockSort(IComparer<AvailablePart> comparer, List<AvailablePart> parts)
        {
            if (retooledSort != null && retooledSortingType.IsInstanceOfType(comparer))
            {
                retooledSort.Invoke(comparer, new object[] { parts });
            }
            else
            {
                parts.Sort(comparer);
            }
        }

        private static void Invalidate()
        {
            rankedComparer = null;
            rankedSource = null;
            rankedCount = 0;
            Rank.Clear();
        }
    }
}
