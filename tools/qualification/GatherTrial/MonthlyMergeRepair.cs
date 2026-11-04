using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.DocumentProcessing.Gather;

// Merge only source-validated and independently KEEP-reviewed targeted repairs; retain the old artifacts unchanged.
internal static class MonthlyMergeRepair
{
    public static int Run(string[] args)
    {
        if (args.Length != 8) throw new ArgumentException("monthly-merge-repair <previous> <repair> <source8> <source9> <outline8> <outline9> <output-dir>");
        var output = Path.GetFullPath(args[7]);
        if (Directory.Exists(output)) throw new IOException("OUTPUT_DIRECTORY_ALREADY_EXISTS");
        static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        var previousHash = Hash(args[1]);
        var root = JsonNode.Parse(File.ReadAllText(args[1])) as JsonObject ?? throw new InvalidDataException("PREVIOUS_INVALID");
        var repairs = JsonNode.Parse(File.ReadAllText(args[2])) as JsonObject ?? throw new InvalidDataException("REPAIR_INVALID");
        if ((string?)repairs["previousSha256"] != previousHash) throw new InvalidDataException("REPAIR_PREVIOUS_HASH_MISMATCH");
        var paths = new Dictionary<int, string> { [8] = Path.GetFullPath(args[3]), [9] = Path.GetFullPath(args[4]) };
        foreach (var (n, path) in paths)
            if ((string?)root["hashes"]?[n.ToString()] != Hash(path) || (string?)repairs["sourceHashes"]?[n.ToString()] != Hash(path))
                throw new InvalidDataException("SOURCE_HASH_MISMATCH:" + n);
        var sources = new Dictionary<int, GatherSource>
        {
            [8] = GatherUnits.Read(paths[8], "T8", DocumentTrial.Outline(args[5])),
            [9] = GatherUnits.Read(paths[9], "T9", DocumentTrial.Outline(args[6])),
        };
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var prior = (root["results"] as JsonArray ?? throw new InvalidDataException("PREVIOUS_RESULTS_MISSING"))
            .Cast<JsonObject>().ToDictionary(x => (int)x["Index"]!);
        var applied = 0;
        foreach (var node in (repairs["results"] as JsonArray ?? throw new InvalidDataException("REPAIR_RESULTS_MISSING")).Cast<JsonObject>())
        {
            var index = (int)node["Index"]!;
            var month = (int)node["Month"]!;
            if (!prior.TryGetValue(index, out var previous) || (int)previous["Month"]! != month ||
                (string?)previous["Phase"] != (string?)node["Phase"] || (string?)previous["Topic"] != (string?)node["Topic"])
                throw new InvalidDataException("REPAIR_SCOPE_MISMATCH:" + index);
            var scope = previous["Scope"]!.Deserialize<GatherScopeResult>(opts) ?? throw new InvalidDataException("PREVIOUS_SCOPE_INVALID");
            var targeted = node["Repair"]!.Deserialize<GatherQuoteRepairResult>(opts) ?? throw new InvalidDataException("TARGETED_REPAIR_INVALID");
            var review = node["Review"]!.Deserialize<GatherBatchedReviewResult>(opts) ?? throw new InvalidDataException("TARGETED_REVIEW_INVALID");
            var candidates = targeted.Decisions.Where(d => d.Status == "REPAIRED" && d.Fact is not null).ToArray();
            if (review.Review.Verdicts.Count != candidates.Length || review.Failures.Count != 0)
                throw new InvalidDataException("TARGETED_REVIEW_INCOMPLETE:" + index);
            var facts = scope.ReviewedSection.Run.Facts.ToList();
            var remaining = scope.Repair.Remaining.ToList();
            var byId = sources[month].Units.ToDictionary(u => u.Id, StringComparer.Ordinal);
            var accepted = new List<GatherQuoteRepairDecision>();
            for (var i = 0; i < candidates.Length; i++)
            {
                var candidate = candidates[i];
                if (review.Review.Verdicts[i].Index != i || review.Review.Verdicts[i].Decision != "KEEP") continue;
                var original = remaining.SingleOrDefault(x => x.Index == candidate.OriginalIndex) ?? throw new InvalidDataException("ORIGINAL_REFUSAL_MISSING");
                var allowed = original.Proposal!.Parts.Select(p => p.UnitId).ToHashSet(StringComparer.Ordinal);
                foreach (var part in candidate.Fact!.Parts)
                    if (!allowed.Contains(part.UnitId) || !byId.TryGetValue(part.UnitId, out var unit) || part.Start is not int start || part.End is not int end ||
                        start < 0 || end <= start || end > unit.Text.Length || unit.Text[start..end] != part.Text || unit.Location != part.Location || unit.SourceId != part.SourceId)
                        throw new InvalidDataException("TARGETED_REPAIR_SOURCE_MISMATCH");
                static string Identity(GatheredFact f) => string.Join("|", f.Parts.Select(p => $"{p.UnitId}:{p.Start}:{p.End}:{p.Role}:{p.Text}"));
                if (!facts.Any(f => Identity(f) == Identity(candidate.Fact))) facts.Add(candidate.Fact);
                remaining.Remove(original);
                accepted.Add(candidate with { Round = 3 });
                applied++;
            }
            var unresolvedIndices = remaining.Select(r => r.Index).ToHashSet();
            var updatedRun = scope.ReviewedSection.Run with { Facts = facts, Refused = scope.ReviewedSection.Run.Refused.Where(r => unresolvedIndices.Contains(r.Index)).ToArray() };
            var updated = scope with
            {
                Repair = new GatherQuoteRepairResult(scope.Repair.Decisions.Concat(accepted).ToArray(), remaining),
                ReviewedSection = scope.ReviewedSection with { Run = updatedRun, Conflicts = GatherDocument.Conflicts(facts) },
            };
            previous["Scope"] = JsonSerializer.SerializeToNode(updated, opts);
        }
        foreach (var (n, path) in paths)
            if ((string?)root["hashes"]?[n.ToString()] != Hash(path)) throw new InvalidDataException("SOURCE_CHANGED:" + n);
        root["targetedRepairSha256"] = Hash(args[2]);
        root["targetedRepairsApplied"] = applied;
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "review.json"), root.ToJsonString(opts));
        Console.WriteLine($"targeted_reviewed_repairs_applied={applied} output={output}");
        return 0;
    }
}
