using System;

namespace JebJebGo.Core
{
    /// <summary>How a tag compares to a search term, chosen by the tag's first character.</summary>
    internal enum StockMatchType
    {
        EitherContainsEither,
        EqualsOnly,
        EitherStartsWithEither,
        EitherEndsWithEither,
        TermStartsWithTag,
        TermEndsWithTag,
        TagStartsWithTerm,
        TagEndsWithTerm,
        TermContainsTag,
        TagContainsTerm,
    }

    /// <summary>One parsed tag: its text without the prefix, raw length with the prefix, and match type.</summary>
    internal readonly struct StockTag
    {
        public readonly string Text;
        public readonly int RawLength;
        public readonly StockMatchType MatchType;

        public StockTag(string text, int rawLength, StockMatchType matchType)
        {
            Text = text;
            RawLength = rawLength;
            MatchType = matchType;
        }
    }

    /// <summary>
    /// The stock KSP part-search syntax (BasePartCategorizer), reproduced so
    /// JebJebGo can stay result-compatible with stock and later extend it.
    /// </summary>
    internal static class StockSearchSyntax
    {
        private static readonly char[] Separators = { ' ', '|', ';', ',', '-' };

        /// <summary>Stock SearchTagSplit: trim, lowercase (invariant), split on space | ; , -.</summary>
        public static string[] SplitTerms(string text)
        {
            return (text ?? string.Empty).Trim().ToLowerInvariant().Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>Split a part's tags like stock and parse each tag's prefix once.</summary>
        public static StockTag[] ParseTags(string tags)
        {
            string[] raw = SplitTerms(tags);
            StockTag[] parsed = new StockTag[raw.Length];
            for (int i = 0; i < raw.Length; i++)
            {
                string text = raw[i];
                StockMatchType type = ParsePrefix(ref text);
                parsed[i] = new StockTag(text, raw[i].Length, type);
            }
            return parsed;
        }

        /// <summary>
        /// Stock TermMatchesTag. Deliberately uses the same string overloads as
        /// the game (ordinal Contains; culture-default StartsWith, EndsWith,
        /// Equals) so edge cases agree with stock.
        /// </summary>
        public static bool TermMatchesTag(string term, in StockTag tag)
        {
            string text = tag.Text;
            switch (tag.MatchType)
            {
                case StockMatchType.EqualsOnly:
                    return term.Equals(text);
                case StockMatchType.TermStartsWithTag:
                    return term.StartsWith(text);
                case StockMatchType.TermEndsWithTag:
                    return term.EndsWith(text);
                case StockMatchType.TagStartsWithTerm:
                    return text.StartsWith(term);
                case StockMatchType.TagEndsWithTerm:
                    return text.EndsWith(term);
                case StockMatchType.EitherStartsWithEither:
                    return term.StartsWith(text) || text.StartsWith(term);
                case StockMatchType.EitherEndsWithEither:
                    return term.EndsWith(text) || text.EndsWith(term);
                case StockMatchType.TermContainsTag:
                    return term.Contains(text);
                case StockMatchType.TagContainsTerm:
                    return text.Contains(term);
                default:
                    return term.Contains(text) || text.Contains(term);
            }
        }

        private static StockMatchType ParsePrefix(ref string tag)
        {
            if (tag.Length == 0)
            {
                return StockMatchType.EitherContainsEither;
            }
            StockMatchType type;
            switch (tag[0])
            {
                case '<': type = StockMatchType.TermContainsTag; break;
                case '>': type = StockMatchType.TagContainsTerm; break;
                case '?': type = StockMatchType.EqualsOnly; break;
                case ')': type = StockMatchType.EitherEndsWithEither; break;
                case '(': type = StockMatchType.EitherStartsWithEither; break;
                case ']': type = StockMatchType.TermEndsWithTag; break;
                case '[': type = StockMatchType.TermStartsWithTag; break;
                case '}': type = StockMatchType.TagEndsWithTerm; break;
                case '{': type = StockMatchType.TagStartsWithTerm; break;
                default: return StockMatchType.EitherContainsEither;
            }
            tag = tag.Substring(1);
            return type;
        }
    }
}
