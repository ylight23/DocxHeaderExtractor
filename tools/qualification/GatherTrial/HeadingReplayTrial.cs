using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.DocumentProcessing.Gather;

// Qualification only. Replay review against exact source spans, with manually checked target boundaries.
internal static class HeadingReplayTrial
{
    public static int Run(string[] args)
    {
        if (args.Length != 8 || args[7] != "--manual-boundaries-checked")
            throw new ArgumentException("headings-replay-reviewed <target> <target-outline> <source> <source-outline> <review.json> <output-dir> --manual-boundaries-checked");
        var target = Path.GetFullPath(args[1]);
        var source = Path.GetFullPath(args[3]);
        var output = Path.GetFullPath(args[6]);
        if (Directory.Exists(output)) throw new IOException("OUTPUT_DIRECTORY_ALREADY_EXISTS");
        static string Hash(string p) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p)));
        using var stored = JsonDocument.Parse(File.ReadAllText(args[5]));
        var root = stored.RootElement;
        var targetHash = Hash(target);
        var sourceHash = Hash(source);
        if (root.GetProperty("targetHash").GetString() != targetHash || root.GetProperty("sourceHash").GetString() != sourceHash)
            throw new InvalidDataException("REVIEW_DOCUMENT_HASH_MISMATCH");
        var sourceId = "sha256:" + sourceHash;
        var (sections, repeated) = GatherByHeading.Build(target, DocumentTrial.Outline(args[2]),
            [new GatherSourceFile(sourceId, source, DocumentTrial.Outline(args[4]))]);
        var candidates = sections.Where(s => s.Run.Facts.Count > 0).ToList();
        var records = root.GetProperty("reviews").EnumerateArray().ToList();
        if (candidates.Count != records.Count) throw new InvalidDataException("REVIEW_SECTION_COUNT_MISMATCH");
        var reviewed = new List<GatherSection>();
        var rejected = new List<GatherExcluded>();
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        for (var i = 0; i < candidates.Count; i++)
        {
            var section = candidates[i];
            var record = records[i];
            if (!section.Run.Complete || record.GetProperty("Heading").GetString() != section.Heading ||
                record.GetProperty("TargetLocation").GetString() != section.TargetLocation ||
                record.GetProperty("NextHeadingLocation").GetString() != section.NextHeadingLocation)
                throw new InvalidDataException("REVIEW_SECTION_BOUNDARY_MISMATCH_OR_GAP");
            var saved = record.GetProperty("candidates").EnumerateArray().ToList();
            if (saved.Count != section.Run.Facts.Count) throw new InvalidDataException("REVIEW_CANDIDATE_COUNT_MISMATCH");
            for (var j = 0; j < saved.Count; j++)
            {
                var fact = section.Run.Facts[j];
                if (saved[j].GetProperty("id").GetInt32() != j || saved[j].GetProperty("text").GetString() != fact.Render() ||
                    !JsonNode.DeepEquals(JsonNode.Parse(saved[j].GetProperty("Parts").GetRawText()), JsonSerializer.SerializeToNode(fact.Parts)))
                    throw new InvalidDataException("REVIEW_CANDIDATE_SOURCE_MISMATCH");
            }
            var verdict = record.GetProperty("review").Deserialize<GatherReviewResult>(opts) ?? throw new InvalidDataException("REVIEW_MISSING");
            var (accepted, dropped) = GatherReviewApply.Apply(section, verdict);
            if (accepted.Run.Facts.Any(f => f.Flags.Contains("SCOPE_UNREVIEWED", StringComparer.Ordinal)))
                throw new InvalidDataException("REVIEW_UNRESOLVED");
            reviewed.Add(accepted);
            rejected.AddRange(dropped);
        }
        Directory.CreateDirectory(output);
        var result = GatherWriteback.Apply(target, Path.Combine(output, "075_reviewed_demo.docx"), reviewed, DateTime.UtcNow, true, targetHash);
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
        { targetHash, sourceHash, boundaryMethod = "manually reviewed for this qualification, not production extraction", result.SkippedHeadings, reviewed, rejected, repeated }, opts));
        Console.WriteLine($"written={reviewed.Sum(s => s.Run.Facts.Count)} skipped={result.SkippedHeadings.Count} output={result.OutputPath}");
        return result.SkippedHeadings.Count == 0 ? 0 : 1;
    }
}
