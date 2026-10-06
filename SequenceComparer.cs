using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace SequenceNavigator
{
    /// <summary>One field that differs between two backups.</summary>
    public sealed record SequenceDifference(
        string Sequence,
        int Step,
        string Group,
        string Member,
        string Field,
        string A,
        string B)
    {
        /// <summary>The tab the field lives on, as the main window names it.</summary>
        public string Tab => Group == "SEQ" ? "Setpoints" : Group;

        /// <summary>Step 0 exists in the data but is not reachable in the UI.</summary>
        public string StepText => Step == 0
            ? "0 (hidden)"
            : Step < 0 ? string.Empty : Step.ToString(CultureInfo.InvariantCulture);
    }

    public sealed record SequenceSummary(string Sequence, int Differences)
    {
        public string CountText => Differences.ToString(CultureInfo.InvariantCulture);
    }

    public sealed class ComparisonResult
    {
        public required string PathA { get; init; }
        public required string PathB { get; init; }
        public required string LabelA { get; init; }
        public required string LabelB { get; init; }
        public required IReadOnlyList<SequenceSummary> Differing { get; init; }
        public required IReadOnlyList<SequenceDifference> Differences { get; init; }
        public required int Compared { get; init; }
        public required IReadOnlyList<string> Skipped { get; init; }

        public int IdenticalCount => Compared - Differing.Count;
    }

    /// <summary>
    /// Compares two backups sequence by sequence. Pure: given the JSON text of each file
    /// and a way to describe a field, it returns the differences, so the Compare window,
    /// the text report and the CSV export all agree.
    /// </summary>
    public static class SequenceComparer
    {
        // Utility tags rather than real sequences. They are skipped in comparisons but
        // named, so a "no differences" result cannot be hiding one.
        public static readonly IReadOnlySet<string> HiddenTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "EMPTYSEQ",
            "COPYSEQDATA",
            "PROGRAM_SEQ_DATA",
        };

        public static Dictionary<string, string> LoadZipJson(string zipPath)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                if (!entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using var stream = entry.Open();
                using var reader = new StreamReader(stream);
                map[entry.FullName] = reader.ReadToEnd();
            }
            return map;
        }

        /// <remarks>describe: Group and member to a readable description, or null.</remarks>
        public static ComparisonResult Compare(
            string pathA, string labelA, IReadOnlyDictionary<string, string> a,
            string pathB, string labelB, IReadOnlyDictionary<string, string> b,
            Func<string, string, string?> describe)
        {
            var allNames = new SortedSet<string>(a.Keys, StringComparer.OrdinalIgnoreCase);
            allNames.UnionWith(b.Keys);

            var differing = new List<SequenceSummary>();
            var differences = new List<SequenceDifference>();
            var skipped = new List<string>();

            foreach (var name in allNames)
            {
                var tag = Path.GetFileNameWithoutExtension(name);
                if (HiddenTags.Contains(tag))
                {
                    skipped.Add(tag);
                    continue;
                }

                bool hasA = a.TryGetValue(name, out var jsonA);
                bool hasB = b.TryGetValue(name, out var jsonB);
                if (!hasA || !hasB)
                {
                    differences.Add(new SequenceDifference(tag, -1, string.Empty, string.Empty,
                        hasA ? "Sequence missing in B" : "Sequence missing in A",
                        hasA ? "present" : "missing", hasB ? "present" : "missing"));
                    differing.Add(new SequenceSummary(tag, 1));
                    continue;
                }

                var raw = new List<(string Path, string A, string B)>();
                CompareNodes(JsonNode.Parse(jsonA!), JsonNode.Parse(jsonB!), "$", raw);
                if (raw.Count == 0)
                {
                    continue;
                }

                foreach (var (path, valueA, valueB) in raw)
                {
                    differences.Add(Describe(tag, path, valueA, valueB, describe));
                }
                differing.Add(new SequenceSummary(tag, raw.Count));
            }

            return new ComparisonResult
            {
                PathA = pathA,
                PathB = pathB,
                LabelA = labelA,
                LabelB = labelB,
                Differing = differing,
                Differences = differences,
                Compared = allNames.Count - skipped.Count,
                Skipped = skipped,
            };
        }

        /// <summary>
        /// Turns "$.value[5].C1.TM" into step 5, group C1, member TM. A bare "$.value[5].MTFR"
        /// is one of the SEQ setpoints. Anything else is kept whole as the field name.
        /// </summary>
        private static SequenceDifference Describe(string tag, string path, string valueA, string valueB,
            Func<string, string, string?> describe)
        {
            const string prefix = "$.value[";
            int close = path.IndexOf(']', StringComparison.Ordinal);
            if (!path.StartsWith(prefix, StringComparison.Ordinal) || close < 0 ||
                !int.TryParse(path.AsSpan(prefix.Length, close - prefix.Length),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out int step))
            {
                return new SequenceDifference(tag, -1, string.Empty, string.Empty, path, valueA, valueB);
            }

            var rest = path[(close + 1)..].TrimStart('.');
            var parts = rest.Split('.');
            string group = parts.Length >= 2 ? parts[0] : "SEQ";
            string member = parts.Length >= 2 ? parts[1] : parts[0];
            var description = describe(group, member);
            var field = string.IsNullOrWhiteSpace(description) ? member : $"{description} ({member})";
            return new SequenceDifference(tag, step, group, member, field, valueA, valueB);
        }

        private static void CompareNodes(JsonNode? a, JsonNode? b, string path, List<(string, string, string)> diffs)
        {
            if (a is null && b is null)
            {
                return;
            }
            if (a is null || b is null)
            {
                diffs.Add((path, a is null ? "missing" : Display(a), b is null ? "missing" : Display(b)));
                return;
            }

            if (a is JsonValue va && b is JsonValue vb)
            {
                if (!ValuesEqual(va, vb))
                {
                    diffs.Add((path, Display(va), Display(vb)));
                }
                return;
            }

            if (a is JsonObject objA && b is JsonObject objB)
            {
                var keys = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in objA)
                {
                    keys.Add(prop.Key);
                }
                foreach (var prop in objB)
                {
                    keys.Add(prop.Key);
                }
                foreach (var key in keys)
                {
                    objA.TryGetPropertyValue(key, out var valA);
                    objB.TryGetPropertyValue(key, out var valB);
                    CompareNodes(valA, valB, $"{path}.{key}", diffs);
                }
                return;
            }

            if (a is JsonArray arrA && b is JsonArray arrB)
            {
                if (arrA.Count != arrB.Count)
                {
                    diffs.Add((path, $"{arrA.Count} items", $"{arrB.Count} items"));
                }
                int count = Math.Min(arrA.Count, arrB.Count);
                for (int i = 0; i < count; i++)
                {
                    CompareNodes(arrA[i], arrB[i], $"{path}[{i}]", diffs);
                }
                return;
            }

            diffs.Add((path, a.ToJsonString(), b.ToJsonString()));
        }

        /// <summary>
        /// Compares what the PLC would actually hold. Whole numbers (DINT) compare exactly;
        /// other numbers compare as the REAL (single precision) the controller stores, so
        /// 0.3 and 0.30000001192092896 are the same value, not a difference.
        /// </summary>
        public static bool ValuesEqual(JsonValue a, JsonValue b)
        {
            if (a.TryGetValue<bool>(out var boolA) && b.TryGetValue<bool>(out var boolB))
            {
                return boolA == boolB;
            }
            if (a.TryGetValue<long>(out var longA) && b.TryGetValue<long>(out var longB))
            {
                return longA == longB;
            }
            if (a.TryGetValue<double>(out var dblA) && b.TryGetValue<double>(out var dblB))
            {
                return (float)dblA == (float)dblB;
            }
            return string.Equals(a.ToJsonString(), b.ToJsonString(), StringComparison.Ordinal);
        }

        /// <summary>Values as the cards show them: true/false, and REALs at single precision.</summary>
        public static string Display(JsonNode node)
        {
            if (node is JsonValue value)
            {
                if (value.TryGetValue<bool>(out var flag))
                {
                    return flag ? "true" : "false";
                }
                if (value.TryGetValue<long>(out var whole))
                {
                    return whole.ToString(CultureInfo.InvariantCulture);
                }
                if (value.TryGetValue<double>(out var real))
                {
                    return ((float)real).ToString(CultureInfo.InvariantCulture);
                }
            }
            return node.ToJsonString();
        }

        /// <summary>The plain-text report, kept for records and email.</summary>
        public static string BuildTextReport(ComparisonResult r)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Sequence Comparison Report");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"A: {r.LabelA}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"B: {r.LabelB}");
            sb.AppendLine();
            sb.AppendLine(r.Differing.Count == 0
                ? string.Create(CultureInfo.InvariantCulture, $"No differences found across {r.Compared} sequence(s).")
                : string.Create(CultureInfo.InvariantCulture,
                    $"{r.Differing.Count} of {r.Compared} sequence(s) differ: {string.Join(", ", r.Differing.Select(d => d.Sequence))}"));
            if (r.Skipped.Count > 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"Not compared ({r.Skipped.Count} utility tag(s)): {string.Join(", ", r.Skipped)}");
            }
            sb.AppendLine("============================================================");

            foreach (var group in r.Differences.GroupBy(d => d.Sequence))
            {
                sb.AppendLine();
                sb.AppendLine(CultureInfo.InvariantCulture, $"Sequence: {group.Key}");
                foreach (var d in group)
                {
                    var where = d.Step < 0 ? d.Field : $"Step {d.StepText}  |  {d.Tab}  |  {d.Field}";
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  - {where} : {d.A} -> {d.B}");
                }
            }
            return sb.ToString();
        }

        public static string BuildCsv(ComparisonResult r)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Sequence,Step,Tab,Field,A,B");
            foreach (var d in r.Differences)
            {
                sb.AppendLine(string.Join(",", new[] { d.Sequence, d.StepText, d.Tab, d.Field, d.A, d.B }.Select(Csv)));
            }
            return sb.ToString();
        }

        private static string Csv(string value) =>
            value.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }
}
