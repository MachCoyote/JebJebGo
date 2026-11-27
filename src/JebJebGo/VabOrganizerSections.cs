using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using KSP.UI.Screens;
using UnityEngine;

namespace JebJebGo
{
    /// <summary>
    /// Lets the virtual list show VAB Organizer's subcategories (KSPModStewards'
    /// VABOrganizer, a soft dependency read by reflection). VAB Organizer puts a
    /// header and a grid per subcategory into its own scroll content and, from an
    /// EditorPartList.UpdatePartIcon postfix, moves each icon into the grid of the
    /// part's subcategory. This adapter applies the same rule to the sorted list
    /// up front, so the virtual list knows every section's parts before creating
    /// any icon: each section's grid holds its parts in list order, as with stock.
    ///
    /// Members used: VABOrganizer.SubcategorySorting.Subcategories (public static
    /// list), Subcategory.Name (public), Subcategory.gridTransform (protected
    /// field), Subcategory.SetCategoryVisible(bool) (public). Assignment rule (VAB
    /// Organizer 1.2.0): the last subcategory whose name equals the part's
    /// VABORGANIZER/organizerSubcategory value, otherwise the last subcategory
    /// (its "misc" catch-all). Custom categories are not sorted by VAB Organizer:
    /// their parts stay in the stock grid. A mismatch in a later version is
    /// caught by the virtual list's check that every icon stays in its section.
    /// </summary>
    internal sealed class VabOrganizerSections
    {
        private const string AssemblyName = "VABOrganizer";
        private const string PartNode = "VABORGANIZER";
        private const string PartField = "organizerSubcategory";

        private static bool resolved;
        private static FieldInfo subcategoriesField;
        private static PropertyInfo nameProperty;
        private static FieldInfo gridField;
        private static MethodInfo setVisibleMethod;
        private static readonly object[] ShowArgs = { true };
        private static readonly object[] HideArgs = { false };

        private readonly List<object> subcategories = new List<object>();
        private readonly Dictionary<string, int> indexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<AvailablePart, int> sectionOf = new Dictionary<AvailablePart, int>();
        private List<AvailablePart> indexedSource;
        private int indexedCount;
        private int[] counts;
        private int[] cursor;
        private int[] partSection = new int[0];

        /// <summary>One section per subcategory in VAB Organizer's order, then the stock grid.</summary>
        public List<ListSection> Sections { get; } = new List<ListSection>();

        private VabOrganizerSections(IList source, RectTransform content, ListSection stock)
        {
            for (int i = 0; i < source.Count; i++)
            {
                object subcategory = source[i];
                string name = (string)nameProperty.GetValue(subcategory, null);
                Transform grid = (Transform)gridField.GetValue(subcategory);
                if (grid == null || grid.parent != content)
                {
                    throw new InvalidOperationException("VAB Organizer subcategory " + name + " has no grid under " + content.name);
                }
                subcategories.Add(subcategory);
                Sections.Add(new ListSection((RectTransform)grid));
                indexByName[name] = i; // the last subcategory with a name wins, as VAB Organizer assigns
            }
            Sections.Add(stock);
            counts = new int[Sections.Count];
            cursor = new int[Sections.Count];
        }

        /// <summary>
        /// The adapter for <paramref name="list"/>, or null when VAB Organizer is not
        /// installed. Throws (so the list falls back to stock, logged) when it is
        /// installed but its members or layout are not what this adapter knows.
        /// </summary>
        public static VabOrganizerSections TryAttach(EditorPartList list, ListSection stock)
        {
            if (!Resolve())
            {
                return null;
            }
            RectTransform content = list.partListScrollRect.content;
            // VAB Organizer's container is the scroll content; the stock grid stays beside it (not scrolled).
            if (content == null || content == list.partGrid)
            {
                throw new InvalidOperationException("VAB Organizer is installed but the parts list does not use its subcategory layout (scroll content "
                    + (content != null ? content.name : "none") + ")");
            }
            IList source = subcategoriesField.GetValue(null) as IList;
            if (source == null || source.Count == 0)
            {
                throw new InvalidOperationException("VAB Organizer is installed but SubcategorySorting.Subcategories is empty");
            }
            return new VabOrganizerSections(source, content, stock);
        }

        private static bool Resolve()
        {
            if (resolved)
            {
                return subcategoriesField != null;
            }
            resolved = true;
            Assembly assembly = null;
            foreach (AssemblyLoader.LoadedAssembly loaded in AssemblyLoader.loadedAssemblies)
            {
                if (loaded.name == AssemblyName)
                {
                    assembly = loaded.assembly;
                    break;
                }
            }
            if (assembly == null)
            {
                return false;
            }
            Type sorting = assembly.GetType("VABOrganizer.SubcategorySorting")
                ?? throw new MissingMemberException("VABOrganizer.SubcategorySorting not found");
            Type subcategory = assembly.GetType("VABOrganizer.Subcategory")
                ?? throw new MissingMemberException("VABOrganizer.Subcategory not found");
            const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            FieldInfo list = sorting.GetField("Subcategories", BindingFlags.Static | BindingFlags.Public)
                ?? throw new MissingMemberException("VABOrganizer.SubcategorySorting.Subcategories not found");
            nameProperty = subcategory.GetProperty("Name", Instance)
                ?? throw new MissingMemberException("VABOrganizer.Subcategory.Name not found");
            gridField = subcategory.GetField("gridTransform", Instance)
                ?? throw new MissingMemberException("VABOrganizer.Subcategory.gridTransform not found");
            setVisibleMethod = subcategory.GetMethod("SetCategoryVisible", Instance, null, new[] { typeof(bool) }, null)
                ?? throw new MissingMemberException("VABOrganizer.Subcategory.SetCategoryVisible(bool) not found");
            subcategoriesField = list;
            return true;
        }

        /// <summary>
        /// Order <paramref name="sorted"/> into <paramref name="arranged"/> grouped by
        /// section (each section keeps the list order), set every section's run,
        /// and show the headers of the subcategories that have parts.
        /// </summary>
        public void Arrange(List<AvailablePart> sorted, bool customCategory, List<AvailablePart> arranged)
        {
            int count = sorted.Count;
            int stockIndex = Sections.Count - 1;
            arranged.Clear();
            Array.Clear(counts, 0, counts.Length);
            if (customCategory)
            {
                counts[stockIndex] = count;
                arranged.AddRange(sorted);
            }
            else
            {
                EnsureIndex();
                if (partSection.Length < count)
                {
                    partSection = new int[Math.Max(count, partSection.Length * 2)];
                }
                for (int i = 0; i < count; i++)
                {
                    int section = SectionOf(sorted[i]);
                    partSection[i] = section;
                    counts[section]++;
                    arranged.Add(null);
                }
            }
            int start = 0;
            for (int s = 0; s < Sections.Count; s++)
            {
                Sections[s].Start = start;
                Sections[s].Count = counts[s];
                cursor[s] = start;
                start += counts[s];
            }
            if (!customCategory)
            {
                for (int i = 0; i < count; i++)
                {
                    arranged[cursor[partSection[i]]++] = sorted[i];
                }
            }
            for (int s = 0; s < subcategories.Count; s++)
            {
                bool show = counts[s] > 0;
                // VAB Organizer's own refresh hides every subcategory first; show the ones with parts.
                setVisibleMethod.Invoke(subcategories[s], show ? ShowArgs : HideArgs);
            }
        }

        private void EnsureIndex()
        {
            List<AvailablePart> loaded = PartLoader.LoadedPartsList;
            if (!ReferenceEquals(loaded, indexedSource) || loaded.Count != indexedCount)
            {
                sectionOf.Clear();
                indexedSource = loaded;
                indexedCount = loaded.Count;
            }
        }

        private int SectionOf(AvailablePart part)
        {
            if (sectionOf.TryGetValue(part, out int section))
            {
                return section;
            }
            string value = null;
            ConfigNode config = part.partConfig;
            if (config != null)
            {
                ConfigNode node = config.GetNode(PartNode);
                if (node != null)
                {
                    value = node.GetValue(PartField);
                }
            }
            if (value == null || !indexByName.TryGetValue(value, out section))
            {
                section = subcategories.Count - 1;
            }
            sectionOf[part] = section;
            return section;
        }
    }
}
