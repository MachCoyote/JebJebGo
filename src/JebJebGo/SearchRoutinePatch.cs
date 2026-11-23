using System.Collections;
using KSP.UI.Screens;

namespace JebJebGo
{
    /// <summary>
    /// Harmony prefix on BasePartCategorizer.SearchRoutine() (an iterator;
    /// PartCategorizer.SearchRoutine runs it through base). For the editor's
    /// PartCategorizer, JebJebGo's coroutine replaces the stock one; every
    /// other categorizer (mission builder) and the disabled state keep stock.
    /// </summary>
    internal static class SearchRoutinePatch
    {
        public static bool Prefix(BasePartCategorizer __instance, ref IEnumerator __result)
        {
            if (!JebJebGoStatus.Enabled || !(__instance is PartCategorizer))
            {
                return true;
            }
            __result = SearchCoordinator.Run(__instance);
            return false;
        }
    }
}
