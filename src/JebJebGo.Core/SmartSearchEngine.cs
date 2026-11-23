using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace JebJebGo.Core
{
    /// <summary>
    /// Text normalization and tokenization for the smart engine: invariant
    /// lowercase, diacritics removed, words are runs of letters and digits (a
    /// dot between digits stays inside, so "1.25" is one word), stock tag prefix
    /// characters are ignored, a size with a metre unit also yields the bare
    /// number ("1.25m" adds "1.25"), and a whitespace-separated chunk made of
    /// several words also yields its compact form ("LV-T30" adds "lvt30").
    /// </summary>
    internal static class SearchText
    {
        public static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }
            string lower = text.ToLowerInvariant();
            bool ascii = true;
            for (int i = 0; i < lower.Length; i++)
            {
                if (lower[i] > 127)
                {
                    ascii = false;
                    break;
                }
            }
            if (ascii)
            {
                return lower;
            }
            string decomposed = lower.Normalize(NormalizationForm.FormD);
            StringBuilder builder = new StringBuilder(decomposed.Length);
            foreach (char c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(c);
                }
            }
            return builder.ToString();
        }

        public static List<string> Tokens(string text)
        {
            List<string> tokens = new List<string>();
            AddTokens(text, tokens, splitCamelCase: false);
            return tokens;
        }

        /// <summary>Add the words of <paramref name="text"/> (see the class summary) to <paramref name="output"/>.</summary>
        public static void AddTokens(string text, List<string> output, bool splitCamelCase)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            if (splitCamelCase)
            {
                text = SplitCamelCase(text);
            }
            string normalized = Normalize(text);
            List<string> words = new List<string>(4);
            int index = 0;
            while (index < normalized.Length)
            {
                while (index < normalized.Length && IsChunkSeparator(normalized[index]))
                {
                    index++;
                }
                int start = index;
                while (index < normalized.Length && !IsChunkSeparator(normalized[index]))
                {
                    index++;
                }
                if (index > start)
                {
                    AddChunk(normalized, start, index, output, words);
                }
            }
        }

        /// <summary>
        /// Query terms: one per chunk; a chunk of several words ("lv-t30") is one
        /// compact term ("lvt30"); each term carries alternatives ("1.25m" and "1.25").
        /// </summary>
        public static List<string[]> QueryTerms(string query)
        {
            List<string[]> terms = new List<string[]>();
            string normalized = Normalize(query);
            List<string> words = new List<string>(4);
            List<string> chunkTokens = new List<string>(4);
            int index = 0;
            while (index < normalized.Length)
            {
                while (index < normalized.Length && (IsChunkSeparator(normalized[index]) || IsTermSeparator(normalized[index])))
                {
                    index++;
                }
                int start = index;
                while (index < normalized.Length && !IsChunkSeparator(normalized[index]) && !IsTermSeparator(normalized[index]))
                {
                    index++;
                }
                if (index <= start)
                {
                    continue;
                }
                chunkTokens.Clear();
                words.Clear();
                AddChunk(normalized, start, index, chunkTokens, words);
                if (words.Count == 0)
                {
                    continue;
                }
                List<string> alternatives = new List<string>(2);
                if (words.Count == 1)
                {
                    alternatives.Add(words[0]);
                    string bare = StripMetre(words[0]);
                    if (bare != null)
                    {
                        alternatives.Add(bare);
                    }
                }
                else
                {
                    alternatives.Add(string.Concat(words));
                }
                if (!ContainsTerm(terms, alternatives[0]))
                {
                    terms.Add(alternatives.ToArray());
                }
            }
            return terms;
        }

        private static bool ContainsTerm(List<string[]> terms, string first)
        {
            foreach (string[] term in terms)
            {
                if (term[0] == first)
                {
                    return true;
                }
            }
            return false;
        }

        private static void AddChunk(string text, int start, int end, List<string> output, List<string> words)
        {
            words.Clear();
            int i = start;
            while (i < end)
            {
                while (i < end && !char.IsLetterOrDigit(text[i]))
                {
                    i++;
                }
                int wordStart = i;
                while (i < end && (char.IsLetterOrDigit(text[i]) || (text[i] == '.' && i > wordStart && char.IsDigit(text[i - 1]) && i + 1 < end && char.IsDigit(text[i + 1]))))
                {
                    i++;
                }
                if (i > wordStart)
                {
                    words.Add(text.Substring(wordStart, i - wordStart));
                }
            }
            foreach (string word in words)
            {
                output.Add(word);
                string bare = StripMetre(word);
                if (bare != null)
                {
                    output.Add(bare);
                }
            }
            if (words.Count > 1)
            {
                string compact = string.Concat(words);
                if (HasLetter(compact))
                {
                    output.Add(compact);
                }
            }
        }

        private static string StripMetre(string word)
        {
            if (word.Length > 1 && word[word.Length - 1] == 'm' && char.IsDigit(word[word.Length - 2]))
            {
                for (int i = 0; i < word.Length - 1; i++)
                {
                    if (!char.IsDigit(word[i]) && word[i] != '.')
                    {
                        return null;
                    }
                }
                return word.Substring(0, word.Length - 1);
            }
            return null;
        }

        private static bool HasLetter(string text)
        {
            foreach (char c in text)
            {
                if (char.IsLetter(c))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsChunkSeparator(char c)
        {
            return char.IsWhiteSpace(c);
        }

        private static bool IsTermSeparator(char c)
        {
            return c == ',' || c == '|' || c == ';';
        }

        private static string SplitCamelCase(string text)
        {
            StringBuilder builder = new StringBuilder(text.Length + 8);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (i > 0 && char.IsUpper(c) && char.IsLower(text[i - 1]))
                {
                    builder.Append(' ');
                }
                builder.Append(c == '.' || c == '_' ? ' ' : c);
            }
            return builder.ToString();
        }
    }

    /// <summary>
    /// Inverted index over a snapshot: unique words, and for each word the
    /// parts and fields it occurs in. Built once per snapshot (on a worker).
    /// </summary>
    internal sealed class SmartIndex
    {
        public const int FieldTitle = 0;
        public const int FieldName = 1;
        public const int FieldManufacturer = 2;
        public const int FieldTags = 3;

        public readonly PartSnapshot Snapshot;
        public readonly string[] Words;
        /// <summary>Per word: postings encoded as part * 4 + field, one per part and field.</summary>
        public readonly int[][] Postings;
        public readonly string[] NormalizedTitles;
        public readonly int[] TitleWordCounts;
        public readonly int SearchableCount;

        private SmartIndex(PartSnapshot snapshot, string[] words, int[][] postings, string[] titles, int[] titleWordCounts, int searchable)
        {
            Snapshot = snapshot;
            Words = words;
            Postings = postings;
            NormalizedTitles = titles;
            TitleWordCounts = titleWordCounts;
            SearchableCount = searchable;
        }

        public static SmartIndex Build(PartSnapshot snapshot)
        {
            Dictionary<string, int> ids = new Dictionary<string, int>(4096, StringComparer.Ordinal);
            List<string> words = new List<string>(4096);
            List<List<int>> postings = new List<List<int>>(4096);
            string[] titles = new string[snapshot.Count];
            int[] titleWordCounts = new int[snapshot.Count];
            List<string> tokens = new List<string>(64);
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            int searchable = 0;
            for (int p = 0; p < snapshot.Count; p++)
            {
                PartRecord part = snapshot[p];
                titles[p] = SearchText.Normalize(part.Title.Length > 0 ? part.Title : part.Name);
                if (part.CategoryNone)
                {
                    continue;
                }
                searchable++;
                for (int field = 0; field < 4; field++)
                {
                    tokens.Clear();
                    seen.Clear();
                    switch (field)
                    {
                        case FieldTitle:
                            SearchText.AddTokens(part.Title, tokens, splitCamelCase: false);
                            titleWordCounts[p] = tokens.Count;
                            break;
                        case FieldName:
                            SearchText.AddTokens(part.Name, tokens, splitCamelCase: true);
                            break;
                        case FieldManufacturer:
                            SearchText.AddTokens(part.Manufacturer, tokens, splitCamelCase: false);
                            break;
                        default:
                            SearchText.AddTokens(part.RawTags, tokens, splitCamelCase: false);
                            break;
                    }
                    foreach (string token in tokens)
                    {
                        if (!seen.Add(token))
                        {
                            continue;
                        }
                        if (!ids.TryGetValue(token, out int id))
                        {
                            id = words.Count;
                            ids.Add(token, id);
                            words.Add(token);
                            postings.Add(new List<int>(4));
                        }
                        postings[id].Add(p * 4 + field);
                    }
                }
            }
            int[][] packed = new int[postings.Count][];
            for (int i = 0; i < packed.Length; i++)
            {
                packed[i] = postings[i].ToArray();
            }
            return new SmartIndex(snapshot, words.ToArray(), packed, titles, titleWordCounts, searchable);
        }
    }

    /// <summary>
    /// The JebJebGo engine. A part matches when any query term matches one of
    /// its words (as KSP Community Fixes does, unlike stock's every-term rule).
    /// A term matches a word exactly, as a prefix, as a substring (3+ letters),
    /// or within a small edit distance (adjacent swaps count once; 1 edit for
    /// 4–7 letters, 2 for longer; also against a word's beginning while it is
    /// being typed). Each field has a weight (title highest), a term scores its
    /// best field match, and the part score adds up its terms, with bonuses for
    /// matching every term and for a title that starts with the first term.
    /// Results are ordered best first; ties by title.
    /// </summary>
    internal sealed class SmartSearchEngine : ISearchEngine
    {
        private static readonly float[] FieldWeights = { 1.0f, 0.55f, 0.6f, 0.5f };

        private const float Exact = 1.0f;
        private const float Prefix = 0.8f;
        private const float Substring = 0.45f;
        private const float FuzzyOne = 0.62f;
        private const float FuzzyTwo = 0.42f;
        private const float FuzzyPrefix = 0.5f;
        private const float AllTermsBonus = 0.35f;
        private const float TitleStartsWithTermBonus = 0.2f;
        private const float TitleStartsWithPrefixBonus = 0.1f;

        private readonly object gate = new object();
        private SmartIndex index;

        // Per-thread score buffers, reused across queries (searches run on pool threads).
        [ThreadStatic] private static float[] totalBuffer;
        [ThreadStatic] private static int[] matchedBuffer;
        [ThreadStatic] private static float[] bestBuffer;
        [ThreadStatic] private static float[] titleBonusBuffer;

        /// <summary>Build the index for <paramref name="snapshot"/> now (call from a worker).</summary>
        public SmartIndex Prepare(PartSnapshot snapshot)
        {
            lock (gate)
            {
                if (index == null || !ReferenceEquals(index.Snapshot, snapshot))
                {
                    index = SmartIndex.Build(snapshot);
                }
                return index;
            }
        }

        public void Search(PartSnapshot snapshot, string query, List<int> results)
        {
            results.Clear();
            SmartIndex idx = Prepare(snapshot);
            List<string[]> terms = SearchText.QueryTerms(query);
            int parts = snapshot.Count;
            if (terms.Count == 0)
            {
                // Like stock: a query of separators only shows every searchable part.
                for (int p = 0; p < parts; p++)
                {
                    if (!snapshot[p].CategoryNone)
                    {
                        results.Add(p);
                    }
                }
                SortByTitle(idx, results);
                return;
            }

            if (totalBuffer == null || totalBuffer.Length < parts)
            {
                totalBuffer = new float[parts];
                matchedBuffer = new int[parts];
                bestBuffer = new float[parts];
                titleBonusBuffer = new float[parts];
            }
            float[] total = totalBuffer;
            int[] matchedTerms = matchedBuffer;
            float[] best = bestBuffer;
            float[] titleBonus = titleBonusBuffer;
            Array.Clear(total, 0, parts);
            Array.Clear(matchedTerms, 0, parts);
            Array.Clear(titleBonus, 0, parts);
            for (int t = 0; t < terms.Count; t++)
            {
                Array.Clear(best, 0, parts);
                string[] alternatives = terms[t];
                for (int w = 0; w < idx.Words.Length; w++)
                {
                    string word = idx.Words[w];
                    float quality = 0f;
                    for (int a = 0; a < alternatives.Length; a++)
                    {
                        float q = Quality(alternatives[a], word);
                        if (q > quality)
                        {
                            quality = q;
                        }
                    }
                    if (quality <= 0f)
                    {
                        continue;
                    }
                    int[] postings = idx.Postings[w];
                    for (int i = 0; i < postings.Length; i++)
                    {
                        int part = postings[i] >> 2;
                        int field = postings[i] & 3;
                        float score = quality * FieldWeights[field];
                        if (score > best[part])
                        {
                            best[part] = score;
                        }
                    }
                }
                string first = alternatives[0];
                for (int p = 0; p < parts; p++)
                {
                    if (best[p] > 0f)
                    {
                        total[p] += best[p];
                        matchedTerms[p]++;
                        if (t == 0)
                        {
                            titleBonus[p] = TitleStartBonus(idx.NormalizedTitles[p], first);
                        }
                    }
                }
            }

            for (int p = 0; p < parts; p++)
            {
                if (matchedTerms[p] == 0)
                {
                    continue;
                }
                if (matchedTerms[p] == terms.Count && terms.Count > 1)
                {
                    total[p] += AllTermsBonus * terms.Count;
                }
                total[p] += titleBonus[p];
                // Prefer concise titles among otherwise equal matches.
                total[p] -= 0.002f * idx.TitleWordCounts[p];
                results.Add(p);
            }
            results.Sort((a, b) =>
            {
                int byScore = total[b].CompareTo(total[a]);
                if (byScore != 0)
                {
                    return byScore;
                }
                int byTitle = string.CompareOrdinal(idx.NormalizedTitles[a], idx.NormalizedTitles[b]);
                return byTitle != 0 ? byTitle : a.CompareTo(b);
            });
        }

        /// <summary>A small tiebreaker: the title's first word is the term (0.2) or starts with it (0.1).</summary>
        private static float TitleStartBonus(string title, string term)
        {
            if (!title.StartsWith(term, StringComparison.Ordinal))
            {
                return 0f;
            }
            return title.Length == term.Length || !char.IsLetterOrDigit(title[term.Length]) ? TitleStartsWithTermBonus : TitleStartsWithPrefixBonus;
        }

        internal static float Quality(string term, string word)
        {
            if (word.Length >= term.Length)
            {
                if (string.CompareOrdinal(word, 0, term, 0, term.Length) == 0)
                {
                    return word.Length == term.Length ? Exact : Prefix + 0.1f * term.Length / word.Length;
                }
                if (term.Length >= 3 && word.IndexOf(term, StringComparison.Ordinal) > 0)
                {
                    return Substring;
                }
            }
            if (term.Length < 4 || HasDigit(term) || HasDigit(word))
            {
                // No typo tolerance for short words or for numbers and designations:
                // "1.25" is one edit from "1.5", "td12" from "td13".
                return 0f;
            }
            int maxEdits = term.Length >= 8 ? 2 : 1;
            if (Math.Abs(word.Length - term.Length) <= maxEdits)
            {
                int distance = Distance(term, word, maxEdits);
                if (distance == 1)
                {
                    return FuzzyOne;
                }
                if (distance == 2 && maxEdits >= 2)
                {
                    return FuzzyTwo;
                }
            }
            if (term.Length >= 5 && word.Length > term.Length)
            {
                // A typo in a word still being typed: compare with the word's beginning.
                for (int length = term.Length - 1; length <= term.Length + 1 && length <= word.Length; length++)
                {
                    if (Distance(term, word, length, 1) == 1)
                    {
                        return FuzzyPrefix;
                    }
                }
            }
            return 0f;
        }

        private static bool HasDigit(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] >= '0' && text[i] <= '9')
                {
                    return true;
                }
            }
            return false;
        }

        // Distance rows, reused per thread: typo tolerance compares the query with
        // thousands of index words per search, and fresh rows per call were most
        // of a query's allocations (up to ~4 MB).
        [ThreadStatic] private static int[] rowA;
        [ThreadStatic] private static int[] rowB;
        [ThreadStatic] private static int[] rowC;

        /// <summary>Optimal string alignment distance, or max + 1 when it exceeds <paramref name="max"/>.</summary>
        internal static int Distance(string a, string b, int max)
        {
            return Distance(a, b, b.Length, max);
        }

        /// <summary>Distance between <paramref name="a"/> and the first <paramref name="bLength"/> characters of <paramref name="b"/>.</summary>
        internal static int Distance(string a, string b, int bLength, int max)
        {
            int n = a.Length;
            int m = bLength;
            if (Math.Abs(n - m) > max)
            {
                return max + 1;
            }
            if (rowA == null || rowA.Length < m + 1)
            {
                int size = Math.Max(m + 1, 32);
                rowA = new int[size];
                rowB = new int[size];
                rowC = new int[size];
            }
            int[] previous2 = rowA;
            int[] previous = rowB;
            int[] current = rowC;
            Array.Clear(previous2, 0, m + 1);
            for (int j = 0; j <= m; j++)
            {
                previous[j] = j;
            }
            for (int i = 1; i <= n; i++)
            {
                current[0] = i;
                int rowMin = current[0];
                for (int j = 1; j <= m; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    int value = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
                    if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    {
                        value = Math.Min(value, previous2[j - 2] + 1);
                    }
                    current[j] = value;
                    if (value < rowMin)
                    {
                        rowMin = value;
                    }
                }
                if (rowMin > max)
                {
                    return max + 1;
                }
                int[] swap = previous2;
                previous2 = previous;
                previous = current;
                current = swap;
            }
            return previous[m] <= max ? previous[m] : max + 1;
        }

        private static void SortByTitle(SmartIndex idx, List<int> results)
        {
            results.Sort((a, b) =>
            {
                int byTitle = string.CompareOrdinal(idx.NormalizedTitles[a], idx.NormalizedTitles[b]);
                return byTitle != 0 ? byTitle : a.CompareTo(b);
            });
        }
    }
}
