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
    /// One grid of the parts list and the run of parts it shows. The stock list
    /// has one (partGrid); VAB Organizer adds one per subcategory.
    /// </summary>
    internal sealed class ListSection
    {
        public readonly RectTransform Grid;
        public readonly GridLayoutGroup Layout;
        public readonly ContentSizeFitter Fitter;

        /// <summary>First index of this section's parts in the arranged list.</summary>
        public int Start;
        public int Count;

        // The window shown in this section (indices into the arranged list; -1 when none).
        public int First = -1;
        public int Last = -1;
        public int Columns;
        public int VisibleFirstRow;
        public int VisibleLastRow;

        public ListSection(RectTransform grid)
        {
            Grid = grid;
            Layout = grid.GetComponent<GridLayoutGroup>();
            Fitter = grid.GetComponent<ContentSizeFitter>();
            if (Layout == null)
            {
                throw new MissingComponentException(grid.name + " has no GridLayoutGroup");
            }
        }

        public float Pitch => Layout.cellSize.y + Layout.spacing.y;

        public int ComputeColumns()
        {
            if (Layout.constraint == GridLayoutGroup.Constraint.FixedColumnCount)
            {
                return Math.Max(1, Layout.constraintCount);
            }
            float width = Grid.rect.width - Layout.padding.left - Layout.padding.right;
            return Math.Max(1, (int)((width + Layout.spacing.x) / (Layout.cellSize.x + Layout.spacing.x)));
        }
    }

    /// <summary>
    /// Virtual view state for one EditorPartList, living on its GameObject (so it
    /// dies with the editor scene). The stock list lays out every icon with a
    /// GridLayoutGroup; while virtual, the layout components of every section
    /// grid are disabled, each grid is sized for its rows, and only icons in the
    /// visible window (plus a buffer row above and below) are created, positioned
    /// exactly where the grid would put them, greyed like stock, and active.
    /// Icons are still the stock EditorPartIcons, cached per part in the stock
    /// iconCache. New icons are created within a per-frame time budget, visible
    /// rows first. Without VAB Organizer there is one section (partGrid); with it,
    /// <see cref="VabOrganizerSections"/> supplies one per subcategory.
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
        private readonly List<ListSection> sections = new List<ListSection>();
        private readonly List<ListSection> allSections = new List<ListSection>();
        private readonly List<AvailablePart> arranged = new List<AvailablePart>();
        private EditorPartList list;
        private ListSection stockSection;
        private VabOrganizerSections organizer;
        private bool organizerChecked;
        private List<AvailablePart> parts;
        private bool customCategory;
        private bool virtualActive;
        private int pending;
        private bool sweepNeeded;
        private bool refreshShown;
        private readonly Vector3[] viewCorners = new Vector3[4];
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
            stockSection = new ListSection(list.partGrid);
            allSections.Add(stockSection);
            list.partListScrollRect.onValueChanged.AddListener(OnScroll);
        }

        private void OnDestroy()
        {
            if (list != null && list.partListScrollRect != null)
            {
                list.partListScrollRect.onValueChanged.RemoveListener(OnScroll);
            }
            JebJebGoStatus.PendingIcons = 0;
            JebJebGoStatus.VabOrganizerSections = false;
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
            if (!organizerChecked)
            {
                // After SetState("parts"): VAB Organizer makes its own container the scroll content.
                organizerChecked = true;
                organizer = VabOrganizerSections.TryAttach(list, stockSection);
                if (organizer != null)
                {
                    allSections.Clear();
                    allSections.AddRange(organizer.Sections);
                    JebJebGoStatus.VabOrganizerSections = true;
                    Log.Info("VAB Organizer detected: the virtual list shows its " + (organizer.Sections.Count - 1) + " subcategories");
                }
            }
            customCategory = custom;
            sections.Clear();
            if (organizer != null)
            {
                organizer.Arrange(results, custom, arranged);
                sections.AddRange(organizer.Sections);
                parts = arranged;
            }
            else
            {
                stockSection.Start = 0;
                stockSection.Count = results.Count;
                sections.Add(stockSection);
                parts = results;
            }
            EnterVirtual();
            if (organizer != null)
            {
                // Section positions must be current before the first window; with the
                // grids' own layout disabled this only places headers and grids.
                LayoutRebuilder.ForceRebuildLayoutImmediate(list.partListScrollRect.content);
            }
            sweepNeeded = true;
            SweepHiddenIcons();
            for (int s = 0; s < allSections.Count; s++)
            {
                allSections[s].First = allSections[s].Last = -1;
            }
            // A new list: icons that stay on screen get the stock update again (greyout
            // and buttons can differ between lists). Later window updates of this list
            // only place them, so their update cost (VAB Organizer's postfix checks every
            // subcategory per icon) cannot starve the creation of the icons still pending.
            refreshShown = true;
            try
            {
                UpdateWindow(force: true);
            }
            finally
            {
                refreshShown = false;
            }
            int nonEmpty = 0;
            for (int s = 0; s < sections.Count; s++)
            {
                if (sections[s].Count > 0)
                {
                    nonEmpty++;
                }
            }
            JebJebGoStatus.ListSections = nonEmpty;
            JebJebGoStatus.LastListShowMs = clock.Elapsed.TotalMilliseconds;
        }

        /// <summary>Hand the grids back to their stock layout (subassemblies, variants, or disabled).</summary>
        public void ExitVirtual()
        {
            if (!virtualActive)
            {
                return;
            }
            virtualActive = false;
            for (int s = 0; s < allSections.Count; s++)
            {
                ListSection section = allSections[s];
                if (section.Grid == null)
                {
                    continue;
                }
                section.Layout.enabled = true;
                if (section.Fitter != null)
                {
                    section.Fitter.enabled = true;
                }
                section.First = section.Last = -1;
            }
            pending = 0;
            JebJebGoStatus.PendingIcons = 0;
            parts = null;
            active.Clear();
        }

        private void EnterVirtual()
        {
            virtualActive = true;
            for (int s = 0; s < sections.Count; s++)
            {
                ListSection section = sections[s];
                section.Layout.enabled = false;
                if (section.Fitter != null)
                {
                    section.Fitter.enabled = false;
                }
                RectOffset padding = section.Layout.padding;
                int columns = section.ComputeColumns();
                int rows = (section.Count + columns - 1) / columns;
                float height = padding.top + padding.bottom + rows * section.Layout.cellSize.y + Math.Max(0, rows - 1) * section.Layout.spacing.y;
                RectTransform grid = section.Grid;
                if (grid != list.partListScrollRect.content && grid.anchorMin.y != grid.anchorMax.y)
                {
                    // A VAB Organizer grid its vertical layout has not placed yet still has the
                    // stretched anchors of the stock grid it was cloned from; sized against them
                    // its height would come out negative. The layout pins children to the top
                    // and takes their size as is, so pin it the same way first.
                    grid.anchorMin = new Vector2(grid.anchorMin.x, 1f);
                    grid.anchorMax = new Vector2(grid.anchorMax.x, 1f);
                }
                grid.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
            }
        }

        /// <summary>
        /// Inactive icons left under a grid (by the stock list or before JebJebGo
        /// took over) make every hierarchy, layout, and canvas operation pay for
        /// them; park them in the stock's inactive partIconStorage.
        /// </summary>
        private void SweepHiddenIcons()
        {
            if (!sweepNeeded)
            {
                return;
            }
            Transform storage = iconStorageRef(list).transform;
            Stopwatch budget = Stopwatch.StartNew();
            for (int s = 0; s < allSections.Count; s++)
            {
                Transform gridTransform = allSections[s].Grid;
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
            }
            sweepNeeded = false;
        }

        private RectTransform Viewport()
        {
            ScrollRect scroll = list.partListScrollRect;
            return scroll.viewport != null ? scroll.viewport : (RectTransform)scroll.transform;
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
            // panel changes (VAB Organizer collapsing a subcategory, laying out its
            // headers, or showing its sorter panel) move the view or the grids without
            // events too. Recompute every section's window each frame from the actual
            // geometry (two point conversions per section with parts in view's reach);
            // icons change only when a window did.
            UpdateWindow(force: false);
        }

        /// <summary>
        /// Work out each section's window: the rows of its grid inside the viewport,
        /// plus a buffer row. Geometry comes from the viewport converted into each
        /// grid's own space, so it holds whatever the scroll content is (partGrid
        /// itself, or VAB Organizer's container of subcategory grids).
        /// Returns true when any section's window changed.
        /// </summary>
        private bool ComputeWindows()
        {
            Viewport().GetWorldCorners(viewCorners);
            bool changed = false;
            for (int s = 0; s < sections.Count; s++)
            {
                ListSection section = sections[s];
                int first = -1;
                int last = -1;
                int columns = section.ComputeColumns();
                if (section.Count > 0 && section.Grid.gameObject.activeInHierarchy)
                {
                    float pitch = section.Pitch;
                    float gridTop = section.Grid.rect.yMax;
                    float padTop = section.Layout.padding.top;
                    // Distances below the grid's first row of the viewport's top and bottom edges.
                    float viewTop = gridTop - section.Grid.InverseTransformPoint(viewCorners[1]).y - padTop;
                    float viewBottom = gridTop - section.Grid.InverseTransformPoint(viewCorners[0]).y - padTop;
                    int rows = (section.Count + columns - 1) / columns;
                    int topRow = Mathf.FloorToInt(Mathf.Max(0f, viewTop) / pitch);
                    int bottomRow = Mathf.FloorToInt(viewBottom / pitch);
                    int firstRow = Math.Max(0, topRow - BufferRows);
                    int lastRow = Math.Min(rows - 1, bottomRow + BufferRows);
                    if (viewBottom >= -BufferRows * pitch && firstRow <= lastRow)
                    {
                        first = section.Start + firstRow * columns;
                        last = section.Start + Math.Min(section.Count - 1, (lastRow + 1) * columns - 1);
                        section.VisibleFirstRow = topRow;
                        section.VisibleLastRow = bottomRow;
                    }
                }
                if (first != section.First || last != section.Last || columns != section.Columns)
                {
                    changed = true;
                }
                section.First = first;
                section.Last = last;
                section.Columns = columns;
            }
            return changed;
        }

        private void UpdateWindow(bool force)
        {
            if (parts == null)
            {
                return;
            }
            bool changed = ComputeWindows();
            if (!force && !changed && pending == 0)
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
            // Visible rows first, then the buffer rows, so new icons appear where the player looks.
            for (int pass = 0; pass < 2; pass++)
            {
                for (int s = 0; s < sections.Count; s++)
                {
                    ListSection section = sections[s];
                    if (section.First < 0)
                    {
                        continue;
                    }
                    Transform grid = section.Grid;
                    for (int i = section.First; i <= section.Last; i++)
                    {
                        int row = (i - section.Start) / section.Columns;
                        bool inView = row >= section.VisibleFirstRow && row <= section.VisibleLastRow;
                        if ((pass == 0) != inView)
                        {
                            continue;
                        }
                        AvailablePart part = parts[i];
                        bool cached = cache.TryGetValue(part.name, out EditorPartIcon icon) && icon != null;
                        bool alreadyShown = cached && icon.gameObject.activeSelf && icon.transform.parent == grid;
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
                            icon.transform.SetParent(grid, worldPositionStays: false);
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
                        else if (icon.transform.parent != grid)
                        {
                            icon.transform.SetParent(grid, worldPositionStays: false);
                        }
                        if (!alreadyShown)
                        {
                            newlyShown++;
                        }
                        if (!alreadyShown || refreshShown)
                        {
                            updatePartIcon(list, icon, part, customCategory); // greyout + buttons + SetActive(true), as stock
                        }
                        if (icon.transform.parent != grid)
                        {
                            // Another mod's UpdatePartIcon patch re-homed the icon somewhere this
                            // list does not lay out (an organizer it does not know, or a VAB
                            // Organizer version whose rules changed): hand the list to stock.
                            Transform moved = icon.transform.parent;
                            throw new InvalidOperationException("another mod moved the icon of " + part.name + " from " + grid.name
                                + " to " + (moved != null ? moved.name : "nothing") + " after EditorPartList.UpdatePartIcon");
                        }
                        Place(icon, i - section.Start, section);
                        if (!alreadyShown || maskMoved)
                        {
                            // New icons come out of Instantiate already active, so "was inactive" is not enough.
                            PartListTooltipController.SetScreenSpaceMaskMaterials(icon.materials);
                        }
                        keep.Add(icon);
                    }
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
            for (int s = 0; s < sections.Count; s++)
            {
                ListSection section = sections[s];
                if (section.First < 0)
                {
                    continue;
                }
                for (int i = section.First; i <= section.Last; i++)
                {
                    if (cache.TryGetValue(parts[i].name, out EditorPartIcon icon) && icon != null && keep.Contains(icon))
                    {
                        active.Add(icon);
                        icons.Add(icon);
                    }
                }
            }
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

        private static void Place(EditorPartIcon icon, int index, ListSection section)
        {
            GridLayoutGroup layout = section.Layout;
            int row = index / section.Columns;
            int column = index % section.Columns;
            RectTransform rect = (RectTransform)icon.transform;
            float x = layout.padding.left + column * (layout.cellSize.x + layout.spacing.x);
            float y = layout.padding.top + row * (layout.cellSize.y + layout.spacing.y);
            rect.SetInsetAndSizeFromParentEdge(RectTransform.Edge.Left, x, layout.cellSize.x);
            rect.SetInsetAndSizeFromParentEdge(RectTransform.Edge.Top, y, layout.cellSize.y);
        }
    }
}
