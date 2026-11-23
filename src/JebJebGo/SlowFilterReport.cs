using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace JebJebGo
{
    /// <summary>
    /// Applies the parts list's exclude filters exactly like the stock
    /// EditorPartListFilterList.GetFilteredList (copy, then each filter in order),
    /// timing each one. Exclude filters are added by the game and by other mods
    /// and run on every tab switch and search; when they take longer than
    /// <see cref="SlowMs"/> together, the slowest ones are logged once per set of
    /// filters per editor visit, naming the mod that added each, so a freeze
    /// caused by another mod's filter is not mistaken for JebJebGo's (or the
    /// stock list's) own cost.
    /// </summary>
    internal static class SlowFilterReport
    {
        private const double SlowMs = 250.0;
        private const int MaxFilters = 64;

        private static readonly double[] FilterMs = new double[MaxFilters];
        private static readonly HashSet<string> Reported = new HashSet<string>();
        private static int editorVisit = -1;

        /// <summary>Main-thread milliseconds of the last exclude-filter pass.</summary>
        public static double LastMs { get; private set; }

        public static List<AvailablePart> ApplyExcludeFilters(EditorPartListFilterList<AvailablePart> filters, List<AvailablePart> source)
        {
            List<AvailablePart> parts = new List<AvailablePart>(source);
            int count = filters.Count;
            long start = Stopwatch.GetTimestamp();
            long previous = start;
            for (int i = 0; i < count; i++)
            {
                EditorPartListFilter<AvailablePart>.FilterList(parts, filters[i].FilterCriteria);
                long now = Stopwatch.GetTimestamp();
                if (i < MaxFilters)
                {
                    FilterMs[i] = (now - previous) * 1000.0 / Stopwatch.Frequency;
                }
                previous = now;
            }
            LastMs = (previous - start) * 1000.0 / Stopwatch.Frequency;
            JebJebGoStatus.LastExcludeFiltersMs = LastMs;
            if (LastMs > SlowMs)
            {
                try
                {
                    Report(filters, count);
                }
                catch (System.Exception error)
                {
                    Log.Warning("could not report slow filters: " + error.Message);
                }
            }
            return parts;
        }

        private static void Report(EditorPartListFilterList<AvailablePart> filters, int count)
        {
            if (editorVisit != JebJebGoStatus.EditorVisits)
            {
                editorVisit = JebJebGoStatus.EditorVisits;
                Reported.Clear();
            }
            StringBuilder key = new StringBuilder();
            for (int i = 0; i < count; i++)
            {
                key.Append(filters[i].ID).Append('|');
            }
            if (!Reported.Add(key.ToString()))
            {
                return;
            }
            StringBuilder text = new StringBuilder("slow parts list filters (run by the game on every tab switch and search, ")
                .Append(LastMs.ToString("0")).Append(" ms in all):");
            for (int shown = 0; shown < 8; shown++)
            {
                int slowest = -1;
                for (int i = 0; i < count && i < MaxFilters; i++)
                {
                    if (FilterMs[i] >= 0 && (slowest < 0 || FilterMs[i] > FilterMs[slowest]))
                    {
                        slowest = i;
                    }
                }
                if (slowest < 0 || FilterMs[slowest] < 1.0)
                {
                    break;
                }
                System.Func<AvailablePart, bool> criteria = filters[slowest].FilterCriteria;
                if (criteria?.Target is JanitorsClosetFilterFix.CachedFilter cached)
                {
                    criteria = cached.Original; // name the mod that owns the filter, not the fix
                }
                System.Reflection.MethodInfo method = criteria?.Method;
                string owner = method?.DeclaringType != null ? method.DeclaringType.Assembly.GetName().Name : "?";
                text.Append(' ').Append(owner).Append(" \"").Append(filters[slowest].ID).Append("\" ")
                    .Append(FilterMs[slowest].ToString("0")).Append(" ms;");
                FilterMs[slowest] = -1;
            }
            Log.Warning(text.ToString());
        }
    }
}
