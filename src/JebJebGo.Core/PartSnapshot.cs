using System;
using System.Collections.Generic;

namespace JebJebGo.Core
{
    /// <summary>
    /// What the plugin copies from one AvailablePart on the main thread: the
    /// internal name, the final tags string the game searches (cfg tags plus
    /// stock auto-tags), and whether the part is in category none (never
    /// shown by search). Plain data, safe to hand to worker threads.
    /// </summary>
    internal readonly struct PartInput
    {
        public readonly string Name;
        public readonly string Tags;
        public readonly bool CategoryNone;
        public readonly string Title;
        public readonly string Manufacturer;

        public PartInput(string name, string tags, bool categoryNone)
            : this(name, tags, categoryNone, null, null)
        {
        }

        public PartInput(string name, string tags, bool categoryNone, string title, string manufacturer)
        {
            Name = name ?? string.Empty;
            Tags = tags ?? string.Empty;
            CategoryNone = categoryNone;
            Title = title ?? string.Empty;
            Manufacturer = manufacturer ?? string.Empty;
        }
    }

    /// <summary>
    /// Immutable search view of the loaded parts, indexed like the input list.
    /// Tags are pre-parsed once at build time so queries allocate nothing per part.
    /// </summary>
    internal sealed class PartSnapshot
    {
        private readonly PartRecord[] records;

        private PartSnapshot(PartRecord[] records)
        {
            this.records = records;
        }

        public int Count => records.Length;

        public PartRecord this[int index] => records[index];

        public static PartSnapshot Build(IList<PartInput> parts)
        {
            if (parts == null)
            {
                throw new ArgumentNullException(nameof(parts));
            }
            PartRecord[] records = new PartRecord[parts.Count];
            for (int i = 0; i < parts.Count; i++)
            {
                PartInput input = parts[i];
                records[i] = new PartRecord(i, input.Name, input.CategoryNone, StockSearchSyntax.ParseTags(input.Tags), input.Title, input.Manufacturer, input.Tags);
            }
            return new PartSnapshot(records);
        }
    }

    internal sealed class PartRecord
    {
        public readonly int Index;
        public readonly string Name;
        public readonly bool CategoryNone;
        public readonly StockTag[] StockTags;
        public readonly string Title;
        public readonly string Manufacturer;
        public readonly string RawTags;

        public PartRecord(int index, string name, bool categoryNone, StockTag[] stockTags, string title, string manufacturer, string rawTags)
        {
            Index = index;
            Name = name;
            CategoryNone = categoryNone;
            StockTags = stockTags;
            Title = title;
            Manufacturer = manufacturer;
            RawTags = rawTags;
        }
    }
}
