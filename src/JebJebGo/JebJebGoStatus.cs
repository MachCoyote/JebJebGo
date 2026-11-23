using System;

namespace JebJebGo
{
    /// <summary>
    /// Public diagnostic and test surface of JebJebGo (used by the repo-only
    /// test harness and available to other mods). Setting <see cref="Enabled"/>
    /// to false makes the next search run the unchanged stock code.
    /// </summary>
    public static class JebJebGoStatus
    {
        /// <summary>Whether JebJebGo handles editor part searches. False after a failure or with -jebjebgo-disable.</summary>
        public static bool Enabled { get; set; } = Array.IndexOf(Environment.GetCommandLineArgs(), "-jebjebgo-disable") < 0;

        /// <summary>
        /// Engine mode: "smart" (default; any-word, typo-tolerant, ranked matching)
        /// or "parity" (stock-identical matching). -jebjebgo-parity selects parity.
        /// </summary>
        public static string Mode
        {
            get => mode;
            set => mode = value == "parity" ? "parity" : "smart";
        }

        /// <summary>
        /// Search results ordered best match first (true) or by the list's sort order: the
        /// "Search results: best matches first" setting of the current save (default on), or
        /// off for the session with -jebjebgo-stock-order. Setting it writes the save's setting.
        /// </summary>
        public static bool RelevanceOrder
        {
            get
            {
                if (StockOrderOption)
                {
                    return false;
                }
                JebJebGoSettings settings = JebJebGoSettings.Current;
                return settings != null ? settings.bestMatchesFirst : relevanceOrderDefault;
            }
            set
            {
                StockOrderOption = false;
                JebJebGoSettings settings = JebJebGoSettings.Current;
                if (settings != null)
                {
                    settings.bestMatchesFirst = value;
                }
                relevanceOrderDefault = value;
            }
        }

        private static bool StockOrderOption { get; set; } = Array.IndexOf(Environment.GetCommandLineArgs(), "-jebjebgo-stock-order") >= 0;

        private static bool relevanceOrderDefault = true;

        private static string mode = Array.IndexOf(Environment.GetCommandLineArgs(), "-jebjebgo-parity") >= 0 ? "parity" : "smart";

        /// <summary>Whether the Harmony patches were applied.</summary>
        public static bool Patched { get; internal set; }

        /// <summary>The error that disabled JebJebGo, if any.</summary>
        public static string LastError { get; internal set; }

        /// <summary>Searches JebJebGo has completed this session.</summary>
        public static int SearchesCompleted { get; internal set; }

        /// <summary>Parts in the current search snapshot.</summary>
        public static int SnapshotParts { get; internal set; }

        /// <summary>Whether the parts list is virtual (icons only for visible rows). False after a list failure or with -jebjebgo-stock-list.</summary>
        public static bool VirtualList { get; set; } = Array.IndexOf(Environment.GetCommandLineArgs(), "-jebjebgo-stock-list") < 0;

        /// <summary>Icons the virtual list still has to create for the visible window (0 when settled).</summary>
        public static int PendingIcons { get; internal set; }

        /// <summary>Main-thread milliseconds of the last virtual list refresh.</summary>
        public static double LastListShowMs { get; internal set; }

        /// <summary>Main-thread milliseconds of applying the last search result (the stock SearchFilterResult, including the list refresh).</summary>
        public static double LastApplyMs { get; internal set; }

        /// <summary>Whether the last match ran on the main thread (it must not).</summary>
        public static bool LastMatchOnMainThread { get; internal set; }

        /// <summary>Worker-thread milliseconds of the last match (engine plus result set).</summary>
        public static double LastMatchMs { get; internal set; }

        /// <summary>Worker-thread milliseconds of the last snapshot build (tag parsing).</summary>
        public static double LastSnapshotBuildMs { get; internal set; }

        /// <summary>Main-thread milliseconds of the last list refresh's filters and ordering.</summary>
        public static double LastRefreshFilterSortMs { get; internal set; }

        /// <summary>Main-thread milliseconds of the last sort-rank build (once per sort mode).</summary>
        public static double LastRankBuildMs { get; internal set; }

        /// <summary>Icons the virtual list has created this session, and the slowest single creation.</summary>
        public static int IconsCreated { get; internal set; }

        public static double MaxIconCreateMs { get; set; }

        public static string SlowestIconPart { get; internal set; }

        /// <summary>Main-thread milliseconds of the last pass of the parts list's exclude filters (the game's and other mods'; see SlowFilterReport).</summary>
        public static double LastExcludeFiltersMs { get; internal set; }

        /// <summary>
        /// The "Janitor's Closet Filter Fix" setting of the current save (JebJebGo's tab in the game's
        /// difficulty options; default on). Outside a loaded game, the value new games start with.
        /// </summary>
        public static bool JanitorsClosetFilterFix
        {
            get
            {
                JebJebGoSettings settings = JebJebGoSettings.Current;
                return settings != null ? settings.janitorsClosetFilterFix : janitorsClosetFilterFixDefault;
            }
            set
            {
                JebJebGoSettings settings = JebJebGoSettings.Current;
                if (settings != null)
                {
                    settings.janitorsClosetFilterFix = value;
                }
                janitorsClosetFilterFixDefault = value;
            }
        }

        private static bool janitorsClosetFilterFixDefault = true;

        /// <summary>Whether the Janitor's Closet Filter Fix is in effect (Janitor's Closet found, setting on).</summary>
        public static bool JanitorsClosetFixActive => global::JebJebGo.JanitorsClosetFilterFix.Active;

        /// <summary>Janitor's Closet filters wrapped this session, and how often their remembered answers were reset.</summary>
        public static int JanitorsClosetFiltersWrapped { get; internal set; }

        public static int JanitorsClosetCacheResets { get; internal set; }

        /// <summary>Editor scenes entered this session.</summary>
        public static int EditorVisits { get; internal set; }

        /// <summary>Editor hitch lines written this session by the field diagnostics (see EditorHitchLog).</summary>
        public static int HitchesLogged { get; internal set; }

        /// <summary>
        /// Test-only fault injection, set by the repo-only harness through reflection:
        /// "VirtualListUpdate" or "SearchApply" makes that hook throw once, to prove the
        /// stock fallback. Null in normal play.
        /// </summary>
        internal static string InjectFault;

        internal static void ThrowIfInjected(string point)
        {
            if (InjectFault != null && InjectFault == point)
            {
                InjectFault = null;
                throw new InvalidOperationException("injected test fault at " + point);
            }
        }

        public static string Version => typeof(JebJebGoStatus).Assembly.GetName().Version.ToString();
    }
}
