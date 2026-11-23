using System.Collections.Generic;
using KSP.UI.Screens;

namespace JebJebGo
{
    /// <summary>
    /// The relevance order of the latest smart search, tied to the filter it
    /// was applied with, so a list refresh orders results best first only for
    /// that exact search (and never for a stock or parity filter).
    /// </summary>
    internal static class SearchRanking
    {
        private static EditorPartListFilter<AvailablePart> filter;
        private static Dictionary<AvailablePart, int> rank;

        public static void Set(EditorPartListFilter<AvailablePart> searchFilter, Dictionary<AvailablePart, int> ranking)
        {
            filter = searchFilter;
            rank = ranking;
        }

        public static Dictionary<AvailablePart, int> For(EditorPartListFilter<AvailablePart> searchFilter)
        {
            return searchFilter != null && ReferenceEquals(searchFilter, filter) ? rank : null;
        }
    }
}
