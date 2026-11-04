using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Gather;

/// <summary>Qualification audit of previously reviewed facts; no LLM or Word output.</summary>
internal static class MonthlySubsectionAudit
{
    static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    public static int Run(string[] args)
    {
        if (args.Length != 5) throw new ArgumentException("monthly-subsection-audit <target> <outline> <review.json> <output.json>");
        var target = Path.GetFullPath(args[1]);
        var outline = DocumentTrial.Outline(args[2]).OrderBy(h => h.Index).ToArray();
        using var review = JsonDocument.Parse(File.ReadAllText(args[3]));
        if (review.RootElement.GetProperty("hashes").GetProperty("10").GetString() != Hash(target))
            throw new InvalidDataException("TARGET_HASH_MISMATCH");
        var plan = outline.Single(h => GatherByHeading.Name(h.Text).StartsWith("kế hoạch trọng tâm hoạt động tháng", StringComparison.Ordinal));
        var source = GatherUnits.Read(target, "target", outline);
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var results = new List<object>();
        foreach (var record in review.RootElement.GetProperty("results").EnumerateArray())
        {
            var month = record.GetProperty("Month").GetInt32();
            var phase = record.GetProperty("Phase").GetString()!;
            var topic = record.GetProperty("Topic").GetString()!;
            var scope = record.GetProperty("Scope").Deserialize<GatherScopeResult>(opts) ?? throw new InvalidDataException("SCOPE_MISSING");
            var matches = outline.Where(h => GatherByHeading.Name(h.Text) == GatherByHeading.Name(topic) &&
                (phase == "activities" ? h.Index < plan.Index : h.Index > plan.Index)).ToArray();
            if (matches.Length != 1 || matches[0].Level is null) throw new InvalidDataException("AMBIGUOUS_SCOPE:" + topic);
            var anchor = matches[0];
            var after = outline.Where(h => h.Index > anchor.Index).ToArray();
            var end = after.FirstOrDefault(h => h.Level is null || h.Level <= anchor.Level)?.Index ?? int.MaxValue;
            var children = after.Where(h => h.Index < end && h.Level > anchor.Level).ToArray();
            var targets = new List<GatherSubsectionTarget>();
            foreach (var child in children.Length == 0 ? [anchor] : children)
            {
                var at = source.Units.ToList().FindIndex(u => u.Location == child.StableId && u.IsHeading && u.PathCertain);
                if (at < 0) continue;
                var next = source.Units.Skip(at + 1).Select((u, i) => (u, i: at + 1 + i)).FirstOrDefault(x => x.u.IsHeading);
                var boundary = next.u is null ? source.Units.Count : next.i;
                var direct = new List<string>();
                if (source.Units[at].InlineBody.Length > 0) direct.Add(source.Units[at].InlineBody);
                direct.AddRange(source.Units.Skip(at + 1).Take(boundary - at - 1).Where(u => !u.IsHeading).Select(u => u.Text));
                targets.Add(new(child.Text, child.StableId!, next.u?.Location, direct));
            }
            var routed = GatherSubsectionRouter.Route(scope.ReviewedSection.Run.Facts, targets,
                singleTargetVerified: children.Length == 0 && targets.Count == 1);
            results.Add(new { month, phase, topic, anchor = anchor.StableId, targets,
                routed = routed.ByLocation.Where(x => x.Value.Count > 0), duplicates = routed.AlreadyInTarget,
                unresolved = routed.Unresolved, total = scope.ReviewedSection.Run.Facts.Count });
        }
        var output = Path.GetFullPath(args[4]);
        if (File.Exists(output)) throw new IOException("OUTPUT_ALREADY_EXISTS");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { targetSha256 = Hash(target),
            warning = "Audit only; heading coverage and parent relations are not verified, unresolved facts must not be written.", results }, opts));
        Console.WriteLine("audited scopes=" + results.Count + " output=" + output);
        return 0;
    }
}
