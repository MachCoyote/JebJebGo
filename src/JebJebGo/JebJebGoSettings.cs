namespace JebJebGo
{
    /// <summary>
    /// JebJebGo's tab in KSP's shared mod settings (the game's difficulty
    /// options, reachable from a new game's setup and from Settings while
    /// playing). Stored per save by the game. Public because KSP discovers
    /// settings tabs by type.
    /// </summary>
    public sealed class JebJebGoSettings : GameParameters.CustomParameterNode
    {
        [GameParameters.CustomParameterUI("Search results: best matches first",
            toolTip = "On: search results are ordered by how well they match what you typed. "
                + "Off: search results keep the parts list's own sort order (the Name, Mass, Cost, and Size buttons).")]
        public bool bestMatchesFirst = true;

        [GameParameters.CustomParameterUI("Janitor's Closet Filter Fix",
            toolTip = "Janitor's Closet's mod filter window adds part filters that re-check every part on every tab switch and search "
                + "(seconds on large installs). With this on, JebJebGo remembers each of those filters' answers and asks again only when "
                + "a filter setting in Janitor's Closet changes. The parts shown stay exactly the same.")]
        public bool janitorsClosetFilterFix = true;

        public override string Title => "JebJebGo";

        public override string DisplaySection => "JebJebGo";

        public override string Section => "JebJebGo";

        public override int SectionOrder => 1;

        public override GameParameters.GameMode GameMode => GameParameters.GameMode.ANY;

        public override bool HasPresets => false;

        /// <summary>The current save's settings, or null outside a loaded game.</summary>
        internal static JebJebGoSettings Current
        {
            get
            {
                GameParameters parameters = HighLogic.CurrentGame?.Parameters;
                return parameters?.CustomParams<JebJebGoSettings>();
            }
        }
    }
}
