using System.Collections.Generic;

namespace JebJebGo.Core
{
    /// <summary>A search engine fills <paramref name="results"/> with matching snapshot indices.</summary>
    internal interface ISearchEngine
    {
        void Search(PartSnapshot snapshot, string query, List<int> results);
    }

    /// <summary>
    /// Matches exactly like the stock KSP part search. Used for parity mode
    /// (Milestone 3) and as the compatibility baseline for the improved engine.
    /// </summary>
    internal sealed class StockCompatibleMatcher : ISearchEngine
    {
        public void Search(PartSnapshot snapshot, string query, List<int> results)
        {
            results.Clear();
            string[] terms = StockSearchSyntax.SplitTerms(query);
            for (int i = 0; i < snapshot.Count; i++)
            {
                if (Matches(snapshot[i], terms))
                {
                    results.Add(i);
                }
            }
        }

        /// <summary>Stock PartMatchesSearch: every term must match some tag long enough to count.</summary>
        public static bool Matches(PartRecord part, string[] terms)
        {
            if (part.CategoryNone)
            {
                return false;
            }
            StockTag[] tags = part.StockTags;
            for (int t = terms.Length - 1; t >= 0; t--)
            {
                string term = terms[t];
                int minimumLength = term.Length < 3 ? term.Length : 3;
                bool found = false;
                for (int g = tags.Length - 1; g >= 0; g--)
                {
                    if (tags[g].RawLength >= minimumLength && StockSearchSyntax.TermMatchesTag(term, in tags[g]))
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
