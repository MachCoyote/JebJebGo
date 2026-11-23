using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using KSP.UI.Screens;
using KSP.UI.Screens.Editor;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace JebJebGo
{
    /// <summary>
    /// Harmony prefixes that make the stock parts list virtual: a refresh (search
    /// results or a category) only materializes icons for the rows in view.
    ///
    /// Patched members:
    /// * EditorPartList.UpdatePartIcons(bool) — replaced by <see cref="VirtualPartListView.Show"/>
    ///   when JebJebGo and the virtual list are enabled.
    /// * EditorPartList.ClearAllItems() — before the stock body runs (for the
    ///   subassembly and variant views), the virtual view hands the grid back to
    ///   its stock layout components.
    /// </summary>
    internal static class VirtualPartListPatch
    {
        private static AccessTools.FieldRef<EditorPartList, List<AvailablePart>> categoryList;

        public static void Install(Harmony harmony)
        {
            categoryList = AccessTools.FieldRefAccess<EditorPartList, List<AvailablePart>>("categoryList");
            VirtualPartListView.Initialize();
            harmony.Patch(AccessTools.Method(typeof(EditorPartList), "UpdatePartIcons", new[] { typeof(bool) })
                    ?? throw new MissingMethodException("EditorPartList.UpdatePartIcons(bool) not found"),
                prefix: new HarmonyMethod(typeof(VirtualPartListPatch), nameof(UpdatePartIconsPrefix)));
            harmony.Patch(AccessTools.Method(typeof(EditorPartList), "ClearAllItems")
                    ?? throw new MissingMethodException("EditorPartList.ClearAllItems not found"),
                prefix: new HarmonyMethod(typeof(VirtualPartListPatch), nameof(ClearAllItemsPrefix)));
        }

        public static bool UpdatePartIconsPrefix(EditorPartList __instance, bool customCategory)
        {
            if (!JebJebGoStatus.Enabled || !JebJebGoStatus.VirtualList)
            {
                VirtualPartListView.Get(__instance, create: false)?.ExitVirtual();
                return true;
            }
            VirtualPartListView view = VirtualPartListView.Get(__instance, create: true);
            try
            {
                view.Show(categoryList(__instance), customCategory);
                return false;
            }
            catch (Exception error)
            {
                JebJebGoStatus.VirtualList = false;
                JebJebGoStatus.LastError = error.ToString();
                Log.Error("virtual parts list failed; using the stock list for this session: " + error);
                view.ExitVirtual();
                return true;
            }
        }

        public static void ClearAllItemsPrefix(EditorPartList __instance)
        {
            VirtualPartListView view = VirtualPartListView.Get(__instance, create: false);
            if (view != null && !view.InShow)
            {
                view.ExitVirtual();
            }
        }
    }

    /// <summary>
    /// Virtual view state for one EditorPartList, living on its GameObject (so it
    /// dies with the editor scene). The stock grid lays out every icon with a
    /// GridLayoutGroup; while virtual, those layout components are disabled, the
    /// content is sized for all rows, and only icons in the visible window (plus
    /// a buffer row above and below) are created, positioned exactly where the
    /// grid would put them, greyed like stock, and active. Icons are still the
    /// stock EditorPartIcons, cached per part in the stock iconCache. New icons
    /// are created within a per-frame time budget, visible rows first.
    /// </summary>
    internal sealed class VirtualPartListView : MonoBehaviour
    {
        private const double CreateBudgetMs = 6.0;
        // Each icon that becomes visible costs Unity a canvas rebuild and a first
        // render in the next frame (outside script time), so cap how many appear per frame.
        private const int MaxNewlyShownPerFrame = 8;
        private const int BufferRows = 1;
        private const double SweepBudgetMs = 2.0;

        private static AccessTools.FieldRef<EditorPartList, List<EditorPartIcon>> iconsRef;
        private static AccessTools.FieldRef<EditorPartList, Dictionary<string, EditorPartIcon>> iconCacheRef;
        private static AccessTools.FieldRef<EditorPartList, GameObject> iconStorageRef;
        private static AccessTools.FieldRef<EditorPartList, List<EditorSubassemblyItem>> subassemblyItemsRef;
        private static AccessTools.FieldRef<EditorPartList, List<EditorVariantItem>> variantItemsRef;
        private static Action<EditorPartList, EditorPartIcon, AvailablePart, bool> updatePartIcon;
        private static Action<EditorPartList> clearAllItems;

        private readonly List<EditorPartIcon> active = new List<EditorPartIcon>(64);
        private readonly HashSet<EditorPartIcon> keep = new HashSet<EditorPartIcon>();
        private EditorPartList list;
        private GridLayoutGroup grid;
        private ContentSizeFitter fitter;
        private List<AvailablePart> parts;
        private bool customCategory;
        private bool virtualActive;
        private int firstShown = -1;
        private int lastShown = -1;
        private int pending;
        private bool sweepNeeded;
        private float lastScroll = float.NaN;
        private float lastViewHeight = float.NaN;
        private int lastColumns = -1;
        private int shownColumns = -1;
        private readonly Vector3[] maskCorners = new Vector3[4];
        private Vector3 lastMaskMin = new Vector3(float.NaN, 0f, 0f);
        private Vector3 lastMaskMax;

        public bool InShow { get; private set; }

        public static void Initialize()
        {
            iconsRef = AccessTools.FieldRefAccess<EditorPartList, List<EditorPartIcon>>("icons");
            iconCacheRef = AccessTools.FieldRefAccess<EditorPartList, Dictionary<string, EditorPartIcon>>("iconCache");
            iconStorageRef = AccessTools.FieldRefAccess<EditorPartList, GameObject>("partIconStorage");
            subassemblyItemsRef = AccessTools.FieldRefAccess<EditorPartList, List<EditorSubassemblyItem>>("saItems");
            variantItemsRef = AccessTools.FieldRefAccess<EditorPartList, List<EditorVariantItem>>("vItems");
            System.Reflection.MethodInfo update = AccessTools.Method(typeof(EditorPartList), "UpdatePartIcon", new[] { typeof(EditorPartIcon), typeof(AvailablePart), typeof(bool) })
                ?? throw new MissingMethodException("EditorPartList.UpdatePartIcon not found");
            updatePartIcon = (l, icon, part, custom) => update.Invoke(l, new object[] { icon, part, custom });
            System.Reflection.MethodInfo clear = AccessTools.Method(typeof(EditorPartList), "ClearAllItems")
                ?? throw new MissingMethodException("EditorPartList.ClearAllItems not found");
            clearAllItems = l => clear.Invoke(l, null);
        }

        public static VirtualPartListView Get(EditorPartList list, bool create)
        {
            VirtualPartListView view = list.GetComponent<VirtualPartListView>();
            if (view == null && create)
            {
                view = list.gameObject.AddComponent<VirtualPartListView>();
                view.Attach(list);
            }
            return view;
        }

        private void Attach(EditorPartList owner)
        {
            list = owner;
            grid = list.partGrid.GetComponent<GridLayoutGroup>();
            fitter = list.partGrid.GetComponent<ContentSizeFitter>();
            if (grid == null)
            {
                throw new MissingComponentException("EditorPartList.partGrid has no GridLayoutGroup");
            }
            list.partListScrollRect.onValueChanged.AddListener(OnScroll);
        }

        private void OnDestroy()
        {
            if (list != null && list.partListScrollRect != null)
            {
                list.partListScrollRect.onValueChanged.RemoveListener(OnScroll);
            }
            JebJebGoStatus.PendingIcons = 0;
        }

        /// <summary>Replacement for the stock UpdatePartIcons: show <paramref name="results"/> virtually.</summary>
        public void Show(List<AvailablePart> results, bool custom)
        {
            Stopwatch clock = Stopwatch.StartNew();
            try
            {
                ShowCore(results, custom);
            }
            finally
            {
                MainThreadCost.Record("VirtualPartListView.Show", clock);
            }
        }

        private void ShowCore(List<AvailablePart> results, bool custom)
        {
            Stopwatch clock = Stopwatch.StartNew();
            List<EditorSubassemblyItem> subassemblies = subassemblyItemsRef(list);
            List<EditorVariantItem> variants = variantItemsRef(list);
            if (!virtualActive || (subassemblies != null && subassemblies.Count > 0) || (variants != null && variants.Count > 0))
            {
                // Coming from the stock list, subassemblies, or variants: let stock clear
                // (deactivates its icons, destroys subassembly/variant items).
                InShow = true;
                try
                {
                    clearAllItems(list);
                }
                finally
                {
                    InShow = false;
                }
                active.Clear();
            }
            // Otherwise keep the current window's icons: UpdateWindow deactivates
            // and parks only the ones that leave it, so icons that stay on screen
            // are not switched off and on again.
            list.scrollRectState.SetState("parts");
            parts = results;
            customCategory = custom;
            EnterVirtual();
            sweepNeeded = true;
            SweepHiddenIcons();
            firstShown = lastShown = -1;
            UpdateWindow(force: true);
            JebJebGoStatus.LastListShowMs = clock.Elapsed.TotalMilliseconds;
        }

        /// <summary>Hand the grid back to its stock layout (subassemblies, variants, or disabled).</summary>
        public void ExitVirtual()
        {
            if (!virtualActive)
            {
                return;
            }
            virtualActive = false;
            grid.enabled = true;
            if (fitter != null)
            {
                fitter.enabled = true;
            }
            pending = 0;
            JebJebGoStatus.PendingIcons = 0;
            parts = null;
            active.Clear();
            firstShown = lastShown = -1;
        }

        private void EnterVirtual()
        {
            virtualActive = true;
            grid.enabled = false;
            if (fitter != null)
            {
                fitter.enabled = false;
            }
            int columns = Columns();
            int rows = (parts.Count + columns - 1) / columns;
            float height = grid.padding.top + grid.padding.bottom + rows * grid.cellSize.y + Math.Max(0, rows - 1) * grid.spacing.y;
            list.partGrid.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
        }

        /// <summary>
        /// Inactive icons left under the grid (by the stock list or before JebJebGo
        /// took over) make every hierarchy, layout, and canvas operation pay for
        /// them; park them in the stock's inactive partIconStorage.
        /// </summary>
        private void SweepHiddenIcons()
        {
            if (!sweepNeeded)
            {
                return;
            }
            Transform gridTransform = list.partGrid.transform;
            Transform storage = iconStorageRef(list).transform;
            Stopwatch budget = Stopwatch.StartNew();
            for (int i = gridTransform.childCount - 1; i >= 0; i--)
            {
                // Spread over frames: thousands of re-parents in one frame would hitch.
                if (budget.Elapsed.TotalMilliseconds > SweepBudgetMs)
                {
                    return;
                }
                Transform child = gridTransform.GetChild(i);
                if (!child.gameObject.activeSelf && child.GetComponent<EditorPartIcon>() != null)
                {
                    child.SetParent(storage, worldPositionStays: false);
                }
            }
            sweepNeeded = false;
        }

        private int Columns()
        {
            if (grid.constraint == GridLayoutGroup.Constraint.FixedColumnCount)
            {
                return Math.Max(1, grid.constraintCount);
            }
            float width = list.partGrid.rect.width - grid.padding.left - grid.padding.right;
            return Math.Max(1, (int)((width + grid.spacing.x) / (grid.cellSize.x + grid.spacing.x)));
        }

        private void OnScroll(Vector2 position)
        {
            if (virtualActive)
            {
                Guarded(() => UpdateWindow(force: false));
            }
        }

        private void Update()
        {
            Stopwatch clock = Stopwatch.StartNew();
            Guarded(UpdateCore);
            MainThreadCost.Record("VirtualPartListView.Update", clock);
        }

        /// <summary>Per-frame and scroll work: on any failure, hand the grid back to the stock list for the session (logged once).</summary>
        private void Guarded(Action work)
        {
            if (!JebJebGoStatus.VirtualList)
            {
                return;
            }
            try
            {
                work();
            }
            catch (Exception error)
            {
                JebJebGoStatus.VirtualList = false;
                JebJebGoStatus.LastError = error.ToString();
                Log.Error("virtual parts list failed; using the stock list for this session: " + error);
                try
                {
                    ExitVirtual();
                    list.Refresh();
                }
                catch (Exception again)
                {
                    Log.Error("could not restore the stock parts list: " + again.Message);
                }
            }
        }

        private void UpdateCore()
        {
            if (!virtualActive)
            {
                return;
            }
            JebJebGoStatus.ThrowIfInjected("VirtualListUpdate");
            SweepHiddenIcons();
            if (pending > 0)
            {
                UpdateWindow(force: true);
                return;
            }
            // The stock list restores each category's remembered scroll position a
            // few frames after a refresh (EditorPartList.ScrollBarFix) and then calls
            // ScrollRect.Rebuild, which swallows the onValueChanged event; layout or
            // panel changes can move the view without events too. Follow the actual
            // scroll offset, viewport, and column count every frame (cheap compares).
            if (list.partGrid.anchoredPosition.y != lastScroll
                || ((RectTransform)list.partListScrollRect.transform).rect.height != lastViewHeight
                || Columns() != lastColumns)
            {
                UpdateWindow(force: false);
            }
        }

        private void UpdateWindow(bool force)
        {
            if (parts == null)
            {
                return;
            }
            int columns = Columns();
            float pitch = grid.cellSize.y + grid.spacing.y;
            lastScroll = list.partGrid.anchoredPosition.y;
            float scroll = Mathf.Max(0f, lastScroll - grid.padding.top);
            float viewHeight = ((RectTransform)list.partListScrollRect.transform).rect.height;
            lastViewHeight = viewHeight;
            lastColumns = columns;
            int rows = (parts.Count + columns - 1) / columns;
            int firstRow = Math.Max(0, (int)(scroll / pitch) - BufferRows);
            int lastRow = Math.Min(rows - 1, (int)((scroll + viewHeight) / pitch) + BufferRows);
            int first = firstRow * columns;
            int last = Math.Min(parts.Count - 1, (lastRow + 1) * columns - 1);
            if (!force && first == firstShown && last == lastShown && pending == 0 && columns == shownColumns)
            {
                return;
            }

            // Icon models are clipped to the drawer by mask values in their materials,
            // taken from shared statics that the part tooltip also resets (to the
            // whole tooltip canvas). Stock re-applies them on every scroll event;
            // the virtual list must set them up itself, or icons it shows (above all
            // the buffer row below the drawer) draw over the UI around it.
            bool maskMoved = SetupMask();
            Dictionary<string, EditorPartIcon> cache = iconCacheRef(list);
            List<EditorPartIcon> icons = iconsRef(list);
            keep.Clear();
            pending = 0;
            Stopwatch budget = Stopwatch.StartNew();
            int newlyShown = 0;
            int visibleFirst = Math.Max(first, (int)(scroll / pitch) * columns);
            // Visible rows first, then the buffer rows, so new icons appear where the player looks.
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = first; i <= last; i++)
                {
                    bool inView = i >= visibleFirst && i <= last - BufferRows * columns;
                    if ((pass == 0) != inView)
                    {
                        continue;
                    }
                    AvailablePart part = parts[i];
                    bool cached = cache.TryGetValue(part.name, out EditorPartIcon icon) && icon != null;
                    bool alreadyShown = cached && icon.gameObject.activeSelf && icon.transform.parent == list.partGrid.transform;
                    if (!alreadyShown && newlyShown >= MaxNewlyShownPerFrame)
                    {
                        pending++;
                        continue;
                    }
                    if (!cached)
                    {
                        if (budget.Elapsed.TotalMilliseconds > CreateBudgetMs)
                        {
                            pending++;
                            continue;
                        }
                        double createStart = budget.Elapsed.TotalMilliseconds;
                        icon = Object.Instantiate(list.partPrefab);
                        icon.transform.SetParent(list.partGrid.transform, worldPositionStays: false);
                        icon.Create(list, part, list.iconSize, list.iconOverScale, list.iconOverSpin);
                        cache[part.name] = icon;
                        double createMs = budget.Elapsed.TotalMilliseconds - createStart;
                        JebJebGoStatus.IconsCreated++;
                        if (createMs > JebJebGoStatus.MaxIconCreateMs)
                        {
                            JebJebGoStatus.MaxIconCreateMs = createMs;
                            JebJebGoStatus.SlowestIconPart = part.name;
                        }
                    }
                    else if (icon.transform.parent != list.partGrid.transform)
                    {
                        icon.transform.SetParent(list.partGrid.transform, worldPositionStays: false);
                    }
                    if (!alreadyShown)
                    {
                        newlyShown++;
                    }
                    Place(icon, i, columns);
                    updatePartIcon(list, icon, part, customCategory); // greyout + buttons + SetActive(true), as stock
                    if (!alreadyShown || maskMoved)
                    {
                        // New icons come out of Instantiate already active, so "was inactive" is not enough.
                        PartListTooltipController.SetScreenSpaceMaskMaterials(icon.materials);
                    }
                    keep.Add(icon);
                }
            }

            Transform storage = iconStorageRef(list).transform;
            for (int i = 0; i < active.Count; i++)
            {
                EditorPartIcon icon = active[i];
                if (icon != null && !keep.Contains(icon))
                {
                    icon.UnsetGrey();
                    icon.gameObject.SetActive(false);
                    icon.transform.SetParent(storage, worldPositionStays: false);
                }
            }
            active.Clear();
            icons.Clear();
            for (int i = first; i <= last; i++)
            {
                if (cache.TryGetValue(parts[i].name, out EditorPartIcon icon) && icon != null && keep.Contains(icon))
                {
                    active.Add(icon);
                    icons.Add(icon);
                }
            }
            firstShown = first;
            lastShown = last;
            shownColumns = columns;
            JebJebGoStatus.PendingIcons = pending;
        }

        /// <summary>
        /// Point the shared screen-space mask at the drawer, as the stock scroll
        /// listener does. Returns true when the drawer's screen rectangle changed
        /// since the last call (window resize, panel change), so every shown icon
        /// needs its mask values again.
        /// </summary>
        private bool SetupMask()
        {
            RectTransform drawer = (RectTransform)list.scrollRectState.transform;
            PartListTooltipController.SetupScreenSpaceMask(drawer);
            drawer.GetWorldCorners(maskCorners);
            bool moved = maskCorners[0] != lastMaskMin || maskCorners[2] != lastMaskMax;
            lastMaskMin = maskCorners[0];
            lastMaskMax = maskCorners[2];
            return moved;
        }

        private void Place(EditorPartIcon icon, int index, int columns)
        {
            int row = index / columns;
            int column = index % columns;
            RectTransform rect = (RectTransform)icon.transform;
            float x = grid.padding.left + column * (grid.cellSize.x + grid.spacing.x);
            float y = grid.padding.top + row * (grid.cellSize.y + grid.spacing.y);
            rect.SetInsetAndSizeFromParentEdge(RectTransform.Edge.Left, x, grid.cellSize.x);
            rect.SetInsetAndSizeFromParentEdge(RectTransform.Edge.Top, y, grid.cellSize.y);
        }
    }
}
