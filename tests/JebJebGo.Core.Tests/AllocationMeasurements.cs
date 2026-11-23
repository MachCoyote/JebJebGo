using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using JebJebGo.Core;
using Xunit;
using Xunit.Abstractions;

namespace JebJebGo.Core.Tests
{
    /// <summary>Allocation measurements must not run in parallel with other tests.</summary>
    [CollectionDefinition("Allocation measurements", DisableParallelization = true)]
    public class AllocationMeasurementsCollection
    {
    }

    /// <summary>
    /// Measures what one smart-engine query allocates (managed bytes, all
    /// threads of the test domain) on a real part corpus when JEBJEBGO_CORPUS
    /// points at one (a JSON file of parts with name, title, manufacturer, tags,
    /// and category fields), else on 4,800 generated parts. Prints per-query
    /// numbers; asserts only that the engine works and that allocations stay
    /// bounded (a regression guard, not a zero-allocation requirement: the typo
    /// pass once allocated up to 3.8 MB per query). The allocation counter is
    /// domain-wide, so the test runs alone, never alongside other tests.
    /// </summary>
    [Collection("Allocation measurements")]
    public class AllocationMeasurements
    {
        private static readonly string[] Queries =
        {
            "fuel tank", "e", "rcs", "lvt30", "mk1 pod", "1.25 adapter", "relaint", "structural", "solar panel", "docking port",
        };

        private readonly ITestOutputHelper output;

        public AllocationMeasurements(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact]
        public void SmartQueriesAllocateABoundedAmount()
        {
            AppDomain.MonitoringIsEnabled = true;
            List<PartInput> parts = LoadParts(out string source);
            PartSnapshot snapshot = PartSnapshot.Build(parts);
            SmartSearchEngine engine = new SmartSearchEngine();
            engine.Prepare(snapshot);
            List<int> results = new List<int>(512);
            foreach (string query in Queries)
            {
                engine.Search(snapshot, query, results); // warm up buffers
            }
            output.WriteLine(parts.Count + " parts from " + source);
            long worst = 0;
            const int repeats = 50;
            foreach (string query in Queries)
            {
                GC.Collect();
                long before = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
                for (int i = 0; i < repeats; i++)
                {
                    engine.Search(snapshot, query, results);
                }
                long perQuery = (AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - before) / repeats;
                worst = Math.Max(worst, perQuery);
                output.WriteLine(string.Format("{0,-14} {1,6} results {2,9:N0} bytes/query", "\"" + query + "\"", results.Count, perQuery));
            }
            engine.Search(snapshot, "fuel tank", results);
            Assert.NotEmpty(results);
            // Regression guard: the typo pass once allocated up to 3.8 MB per query; now ~1.4 KB.
            Assert.True(worst < 64 * 1024, "a query allocated " + worst + " bytes");
        }

        private static List<PartInput> LoadParts(out string source)
        {
            string path = Environment.GetEnvironmentVariable("JEBJEBGO_CORPUS");
            List<PartInput> parts = new List<PartInput>();
            if (path != null && File.Exists(path))
            {
                // Minimal field extraction (the corpus is flat JSON objects with string values).
                string text = File.ReadAllText(path);
                foreach (Match part in Regex.Matches(text, "\\{\\s*\"name\":\\s*\"(?<name>(?:[^\"\\\\]|\\\\.)*)\"(?<body>.*?)\"unresolvedLoc\"", RegexOptions.Singleline))
                {
                    string body = part.Groups["body"].Value;
                    parts.Add(new PartInput(part.Groups["name"].Value, Field(body, "tags"), Field(body, "category") == "none",
                        Field(body, "title"), Field(body, "manufacturer")));
                }
                source = path;
                if (parts.Count > 0)
                {
                    return parts;
                }
            }
            source = "generated parts";
            string[] words = { "fuel", "tank", "engine", "adapter", "structural", "panel", "solar", "rcs", "pod", "dock", "port", "wing", "nose", "cone", "heat", "shield" };
            for (int i = 0; i < 4800; i++)
            {
                string title = words[i % words.Length] + " " + words[(i / 7) % words.Length] + " " + (i % 5 * 0.625) + "m Mk" + (i % 4);
                parts.Add(new PartInput("part" + i, title.ToLowerInvariant() + " generated", false, title, "Maker " + (i % 31)));
            }
            return parts;
        }

        private static string Field(string body, string name)
        {
            Match match = Regex.Match(body, "\"" + name + "\":\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            return match.Success ? Regex.Unescape(match.Groups[1].Value) : string.Empty;
        }
    }
}
