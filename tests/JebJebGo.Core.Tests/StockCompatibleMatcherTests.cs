using System.Collections.Generic;
using JebJebGo.Core;
using Xunit;

namespace JebJebGo.Core.Tests
{
    /// <summary>
    /// The stock-compatible matcher must reproduce KSP's BasePartCategorizer
    /// search exactly (same on Retooled 1.13 and stock 1.12.5): AND of terms,
    /// each term matching some tag of raw length >= min(term length, 3), with
    /// the tag's prefix choosing the match type, and category none never shown.
    /// </summary>
    public class StockCompatibleMatcherTests
    {
        [Fact]
        public void SplitTerms_LowercasesTrimsAndSplitsOnStockSeparators()
        {
            Assert.Equal(new[] { "fuel", "tank", "rcs", "x", "y" }, StockSearchSyntax.SplitTerms("  Fuel-Tank,RCS|x;y "));
        }

        [Fact]
        public void AllTermsMustMatch()
        {
            Assert.Equal(new[] { 0 }, Search("fuel tank", Part("fuel tank"), Part("fuel")));
        }

        [Fact]
        public void DefaultTagMatchesWhenEitherContainsTheOther()
        {
            Assert.Equal(new[] { 0, 1 }, Search("fuelt", Part("fuel"), Part("fueltanks"), Part("tank")));
        }

        [Fact]
        public void TagShorterThanTermMinimumIsIgnored()
        {
            // "lv" contains "l", but a one-character tag cannot match a two-character term.
            Assert.Empty(Search("lv", Part("l")));
            Assert.Equal(new[] { 0 }, Search("lv", Part("lv-t30 lv")));
        }

        [Fact]
        public void TagLengthCountsItsPrefixCharacter()
        {
            // "?td" has raw length 3 >= min(2, 3) although its text "td" is shorter than 3.
            Assert.Equal(new[] { 0 }, Search("td", Part("?td")));
            Assert.Empty(Search("tdx", Part("?td")));
        }

        [Theory]
        [InlineData("?12", "12", true)]
        [InlineData("?12", "120", false)]
        [InlineData("(reliant", "reli", true)]
        [InlineData("(reliant", "reliantx", true)]
        [InlineData("(reliant", "liant", false)]
        [InlineData(")tank", "fueltank", true)]
        [InlineData(")tank", "tan", false)]
        [InlineData("[fuel", "fuelling", true)]
        [InlineData("[fuel", "fue", false)]
        [InlineData("]tank", "bigtank", true)]
        [InlineData("{tank", "tan", true)]
        [InlineData("{tank", "tanks", false)]
        [InlineData("}tank", "ank", true)]
        [InlineData("<oxid", "oxidizer", true)]
        [InlineData("<oxidizer", "oxid", false)]
        [InlineData(">oxidizer", "xidi", true)]
        [InlineData(">oxid", "oxidizer", false)]
        public void TagPrefixSelectsMatchType(string tag, string term, bool expected)
        {
            Assert.Equal(expected, Search(term, Part(tag)).Count == 1);
        }

        [Fact]
        public void CategoryNoneNeverMatches()
        {
            Assert.Empty(Search("tank", Part("tank", hidden: true)));
        }

        [Fact]
        public void SeparatorOnlyQueryMatchesEverySearchablePart()
        {
            Assert.Equal(new[] { 0, 2 }, Search(" - ", Part("a"), Part("b", hidden: true), Part("c")));
        }

        [Fact]
        public void TagsAreLowercasedLikeTheGame()
        {
            Assert.Equal(new[] { 0 }, Search("mainsail", Part("(Mainsail Rockomax")));
        }

        [Fact]
        public void ResultsKeepSnapshotOrder()
        {
            Assert.Equal(new[] { 0, 1, 3 }, Search("t", Part("t1"), Part("t2"), Part("x"), Part("t3")));
        }

        private static PartInput Part(string tags, bool hidden = false)
        {
            return new PartInput("part", tags, hidden);
        }

        private static List<int> Search(string query, params PartInput[] parts)
        {
            PartSnapshot snapshot = PartSnapshot.Build(parts);
            List<int> results = new List<int>();
            new StockCompatibleMatcher().Search(snapshot, query, results);
            return results;
        }
    }
}
