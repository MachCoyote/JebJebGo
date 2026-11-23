using System.Collections.Generic;
using System.Linq;
using JebJebGo.Core;
using Xunit;

namespace JebJebGo.Core.Tests
{
    /// <summary>
    /// The smart engine: a part matches when any query word matches (like KSP
    /// Community Fixes), tolerating typos, prefixes, and dashless designations;
    /// results come back ranked, best first.
    /// </summary>
    public class SmartSearchEngineTests
    {
        [Fact]
        public void TokenizerSplitsWordsAndAddsCompactDesignations()
        {
            List<string> tokens = SearchText.Tokens("LV-T30 \"Reliant\" Liquid Fuel Engine");
            Assert.Contains("lv", tokens);
            Assert.Contains("t30", tokens);
            Assert.Contains("lvt30", tokens);
            Assert.Contains("reliant", tokens);
            Assert.Contains("engine", tokens);
        }

        [Fact]
        public void TokenizerKeepsDecimalSizesAndDropsTheMetreUnit()
        {
            List<string> tokens = SearchText.Tokens("Rockomax 1.25m Adapter");
            Assert.Contains("1.25", tokens);
            Assert.Contains("1.25m", tokens);
        }

        [Fact]
        public void TokenizerStripsDiacriticsAndStockTagPrefixes()
        {
            List<string> tokens = SearchText.Tokens("Mündung (crew ?lfo");
            Assert.Contains("mundung", tokens);
            Assert.Contains("crew", tokens);
            Assert.Contains("lfo", tokens);
        }

        [Fact]
        public void AnyQueryWordMatchesButPartsMatchingEveryWordRankFirst()
        {
            List<string> titles = Titles("fuel tank",
                Part("Liquid Fuel Engine"),
                Part("Fuel Tank Long"),
                Part("Ore Tank"),
                Part("Ladder"));
            Assert.Equal("Fuel Tank Long", titles[0]);
            Assert.Contains("Liquid Fuel Engine", titles);
            Assert.Contains("Ore Tank", titles);
            Assert.DoesNotContain("Ladder", titles);
        }

        [Fact]
        public void ExactWordRanksAbovePrefix()
        {
            List<string> titles = Titles("reliant", Part("Reliantium Tank"), Part("LV-T30 \"Reliant\" Liquid Fuel Engine"));
            Assert.Equal("LV-T30 \"Reliant\" Liquid Fuel Engine", titles[0]);
        }

        [Fact]
        public void PrefixFindsWordsBeingTyped()
        {
            Assert.Equal(new[] { "LV-T30 \"Reliant\" Liquid Fuel Engine" }, Titles("reli", Part("LV-T30 \"Reliant\" Liquid Fuel Engine"), Part("Ore Tank")));
        }

        [Theory]
        [InlineData("realiant", "LV-T30 \"Reliant\" Liquid Fuel Engine")]
        [InlineData("terier", "LV-909 \"Terrier\" Liquid Fuel Engine")]
        [InlineData("parachtue", "Mk16 Parachute")]
        [InlineData("decopler", "TD-12 Decoupler")]
        [InlineData("dockign port", "Clamp-O-Tron Docking Port")]
        [InlineData("solar pannel", "OX-STAT Photovoltaic Panels")]
        public void TyposStillFindThePart(string query, string expected)
        {
            List<string> titles = Titles(query, Corpus());
            Assert.Contains(expected, titles);
            Assert.True(titles.IndexOf(expected) < 3, query + " ranked " + expected + " at " + titles.IndexOf(expected));
        }

        [Theory]
        [InlineData("lvt30", "LV-T30 \"Reliant\" Liquid Fuel Engine")]
        [InlineData("lv909", "LV-909 \"Terrier\" Liquid Fuel Engine")]
        [InlineData("flt400", "FL-T400 Fuel Tank")]
        [InlineData("td12", "TD-12 Decoupler")]
        [InlineData("clampotron", "Clamp-O-Tron Docking Port")]
        public void DesignationsMatchWithoutTheirDashes(string query, string expected)
        {
            Assert.Equal(expected, Titles(query, Corpus())[0]);
        }

        [Fact]
        public void ShortWordsAreNotFuzzyMatched()
        {
            Assert.Empty(Titles("rcs", Part("Arc Light"), Part("Ros Tank")));
        }

        [Theory]
        [InlineData("reli", "ore", 0f)]
        [InlineData("terier", "terrain", 0f)]
        [InlineData("dockign", "docking", 0.62f)]
        [InlineData("parachtue", "parachute", 0.62f)]
        [InlineData("1.25", "1.5", 0f)]
        [InlineData("td12", "td13", 0f)]
        public void FuzzyMatchesOnlyWithinTheEditBudget(string term, string word, float expected)
        {
            // Regression: a distance one past the budget was read as a two-edit match.
            Assert.Equal(expected, SmartSearchEngine.Quality(term, word), 3);
        }

        [Fact]
        public void ManufacturerMatches()
        {
            Assert.Equal(new[] { "X200-32 Fuel Tank" }, Titles("rockomax", Part("X200-32 Fuel Tank", manufacturer: "Rockomax Conglomerate"), Part("Ore Tank", manufacturer: "Kerbodyne")));
        }

        [Fact]
        public void TitleMatchesRankAboveTagOnlyMatches()
        {
            List<string> titles = Titles("mainsail", Part("Rockomax Adapter", tags: "mainsail mount"), Part("RE-M3 \"Mainsail\" Liquid Fuel Engine"));
            Assert.Equal("RE-M3 \"Mainsail\" Liquid Fuel Engine", titles[0]);
        }

        [Fact]
        public void SizeQueriesMatchStockSizeTags()
        {
            Assert.Contains("FL-T400 Fuel Tank", Titles("1.25m fuel tank", Part("FL-T400 Fuel Tank", tags: "1.25 fl-t regular"), Part("Ladder")));
        }

        [Fact]
        public void CategoryNoneNeverMatches()
        {
            Assert.Empty(Titles("tank", Part("Hidden Tank", categoryNone: true)));
        }

        [Fact]
        public void SeparatorOnlyQueryMatchesEverySearchablePartLikeStock()
        {
            Assert.Equal(2, Titles(" - ", Part("A"), Part("B"), Part("C", categoryNone: true)).Count);
        }

        [Fact]
        public void EqualScoresAreOrderedByTitle()
        {
            Assert.Equal(new[] { "Alpha Tank", "Beta Tank" }, Titles("tank", Part("Beta Tank"), Part("Alpha Tank")));
        }

        private static PartInput[] Corpus()
        {
            return new[]
            {
                Part("LV-T30 \"Reliant\" Liquid Fuel Engine", tags: "(reliant liquid fuel engine ?lfo"),
                Part("LV-909 \"Terrier\" Liquid Fuel Engine", tags: "(terrier liquid fuel engine"),
                Part("Mk16 Parachute", tags: "(parachute chute"),
                Part("TD-12 Decoupler", tags: "decouple (decoupler ?12 ?td"),
                Part("Clamp-O-Tron Docking Port", tags: "dock port"),
                Part("OX-STAT Photovoltaic Panels", tags: "solar sun panel"),
                Part("FL-T400 Fuel Tank", tags: "fueltank liquid ?lfo 1.25"),
                Part("Ore Tank"),
                Part("Reliable Strut"),
                Part("Terrain Scanner"),
            };
        }

        private static PartInput Part(string title, string tags = "", string manufacturer = "", bool categoryNone = false)
        {
            return new PartInput(title.ToLowerInvariant().Replace(' ', '.'), tags, categoryNone, title, manufacturer);
        }

        private static List<string> Titles(string query, params PartInput[] parts)
        {
            PartSnapshot snapshot = PartSnapshot.Build(parts);
            List<int> results = new List<int>();
            new SmartSearchEngine().Search(snapshot, query, results);
            return results.Select(i => parts[i].Title).ToList();
        }
    }
}
