using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.DocumentProcessing.Gather;
using DocxHeaderExtractor.DocumentProcessing.Output;
using DocxHeaderExtractor.Contracts.Docx;
using DocxHeaderExtractor.Sources.Docx.OpenXmlParser;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

// Qualification only: writes previously reviewed Qwen results for the manually checked broad monthly sections.
internal static class MonthlyQwenWriteback
{
    static string Norm(string value) => string.Join(' ', value.Trim().TrimEnd(':').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
    static string FactKey(string value) => string.Join(' ', value.Normalize(System.Text.NormalizationForm.FormC)
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    public static int Run(string[] args)
    {
        if (args.Length != 10 || args[9] != "--manual-boundaries-checked")
            throw new ArgumentException("monthly-qwen-writeback <target> <target-outline> <source8> <source9> <outline8> <outline9> <review.json> <output-dir> --manual-boundaries-checked");
        var target = Path.GetFullPath(args[1]);
        var targetOutline = DocumentTrial.Outline(args[2]);
        var output = Path.GetFullPath(args[8]);
        if (Directory.Exists(output)) throw new IOException("OUTPUT_DIRECTORY_ALREADY_EXISTS");
        using var input = JsonDocument.Parse(File.ReadAllText(args[7]));
        var root = input.RootElement;
        var paths = new Dictionary<int, string> { [8] = Path.GetFullPath(args[3]), [9] = Path.GetFullPath(args[4]), [10] = target };
        foreach (var (month, path) in paths)
            if (root.GetProperty("hashes").GetProperty(month.ToString()).GetString() != Hash(path))
                throw new InvalidDataException("SOURCE_HASH_MISMATCH:" + month);
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var records = root.GetProperty("results").EnumerateArray().Select(x => new
        {
            Month = x.GetProperty("Month").GetInt32(), Phase = x.GetProperty("Phase").GetString()!, Topic = x.GetProperty("Topic").GetString()!,
            Scope = x.GetProperty("Scope").Deserialize<GatherScopeResult>(opts) ?? throw new InvalidDataException("SCOPE_MISSING"),
        }).ToList();
        if (records.Count != 16) throw new InvalidDataException("SCOPE_COUNT_MISMATCH");
        var source = new Dictionary<int, GatherSource>
        {
            [8] = GatherUnits.Read(paths[8], "T8", DocumentTrial.Outline(args[5])),
            [9] = GatherUnits.Read(paths[9], "T9", DocumentTrial.Outline(args[6])),
        };
        foreach (var r in records)
        {
            var byId = source[r.Month].Units.ToDictionary(u => u.Id, StringComparer.Ordinal);
            foreach (var part in r.Scope.ReviewedSection.Run.Facts.SelectMany(f => f.Parts))
                if (!byId.TryGetValue(part.UnitId, out var u) || part.Start is not int start || part.End is not int end ||
                    start < 0 || end <= start || end > u.Text.Length || u.Text[start..end] != part.Text || u.Location != part.Location || u.SourceId != part.SourceId)
                    throw new InvalidDataException("REVIEWED_SOURCE_MISMATCH:" + part.UnitId);
        }
        // A quote-copy failure is not an unresolved omission if the separate coverage pass independently kept every exact source unit
        // that proposal referenced. Keep the original refusal in Repair for audit, but do not surface a false "missing content" warning.
        var resolvedRefusals = records.ToDictionary(r => (r.Month, r.Phase, r.Topic), r => ResolvedRefusals(r.Scope, source[r.Month]));
        var plan = targetOutline.Single(h => Norm(h.Text).StartsWith("kế hoạch trọng tâm hoạt động tháng", StringComparison.Ordinal));
        var keys = records.Select(r => (r.Phase, r.Topic)).Distinct().ToList();
        if (keys.Count != 8) throw new InvalidDataException("TARGET_SCOPE_AMBIGUOUS");
        var anchors = keys.Select(key =>
        {
            var hits = targetOutline.Where(h => Norm(h.Text) == Norm(key.Topic) &&
                (key.Phase == "activities" ? h.Index < plan.Index : h.Index > plan.Index)).ToList();
            if (hits.Count != 1) throw new InvalidDataException("TARGET_HEADING_AMBIGUOUS:" + key.Topic);
            return (key.Phase, key.Topic, Heading: hits[0]);
        }).OrderBy(x => x.Heading.Index).ToList();
        // The historical qualification writer has only eight broad anchors. It cannot place reviewed facts under child headings.
        // Refuse before copying any file whenever an anchor owns subsections; a manual check of broad boundaries is not sufficient.
        foreach (var anchor in anchors)
        {
            if (anchor.Heading.Level is not { } level) throw new InvalidDataException("TARGET_HEADING_LEVEL_UNKNOWN:" + anchor.Topic);
            var following = targetOutline.Where(h => h.Index > anchor.Heading.Index).OrderBy(h => h.Index).ToArray();
            var firstUnknown = following.FirstOrDefault(h => h.Level is null);
            var firstClosed = following.FirstOrDefault(h => h.Level is { } known && known <= level);
            if (firstUnknown is not null && (firstClosed is null || firstUnknown.Index < firstClosed.Index))
                throw new InvalidDataException("TARGET_HEADING_LEVEL_UNKNOWN:" + firstUnknown.StableId);
            var end = following.FirstOrDefault(h => h.Level is null || h.Level <= level)?.Index ?? int.MaxValue;
            var child = following.FirstOrDefault(h => h.Index < end && h.Level > level);
            if (child is not null) throw new InvalidDataException("TARGET_CHILD_ROUTING_REQUIRED:" + anchor.Topic + ":" + child.StableId);
        }
        var targetUnits = GatherUnits.Read(target, "target").Units;
        var alreadyInTarget = new List<object>();
        var matchedTargetLabels = new List<object>();
        var mergedDuplicates = new List<object>();
        var resolvedStructuralFlags = new List<object>();
        var lastLocation = anchors[^1].Heading.StableId ?? throw new InvalidDataException("TARGET_LOCATION_MISSING");
        var lastIndex = targetUnits.ToList().FindIndex(u => u.Location == lastLocation);
        var signatureTable = targetUnits.Skip(lastIndex + 1).FirstOrDefault(u => u.TablePath is not null)?.TablePath
            ?? throw new InvalidDataException("TARGET_FINAL_BOUNDARY_MISSING");
        string signature;
        using (var word = WordprocessingDocument.Open(target, false))
        {
            var body = word.MainDocumentPart?.Document?.Body ?? throw new InvalidDataException("TARGET_BODY_MISSING");
            signature = ParagraphWalker.Enumerate(body, new ExtractionOptions())
                .FirstOrDefault(p => p.StableId.StartsWith(signatureTable + "/", StringComparison.Ordinal))?.StableId
                ?? throw new InvalidDataException("TARGET_SIGNATURE_FIRST_PARAGRAPH_MISSING");
        }
        var sections = new List<GatherSection>();
        for (var i = 0; i < anchors.Count; i++)
        {
            var anchor = anchors[i];
            var pair = records.Where(r => r.Phase == anchor.Phase && r.Topic == anchor.Topic).OrderBy(r => r.Month).ToList();
            if (pair.Count != 2 || pair[0].Month != 8 || pair[1].Month != 9) throw new InvalidDataException("SOURCE_SCOPE_MISMATCH");
            var next = i + 1 == anchors.Count ? signature :
                anchor.Phase != anchors[i + 1].Phase ? plan.StableId : anchors[i + 1].Heading.StableId;
            var scopeStart = targetUnits.ToList().FindIndex(u => u.Location == anchor.Heading.StableId);
            var scopeEnd = targetUnits.ToList().FindIndex(u => u.Location == next);
            if (scopeStart < 0 || scopeEnd <= scopeStart) throw new InvalidDataException("TARGET_SCOPE_BOUNDARY_INVALID:" + anchor.Topic);
            var targetText = targetUnits.Skip(scopeStart).Take(scopeEnd - scopeStart).Select(u => Norm(u.Text)).ToHashSet(StringComparer.Ordinal);
            var targetHeadingText = targetOutline.Where(h => h.Index >= anchor.Heading.Index &&
                    (i + 1 == anchors.Count || h.Index < anchors[i + 1].Heading.Index))
                .GroupBy(h => Norm(h.Text), StringComparer.Ordinal).Where(g => g.Count() == 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
            static int Position(GatheredFact f) => int.TryParse(Regex.Match(f.Parts[0].Location, @"/p\[(\d+)\]$").Groups[1].Value, out var p) ? p : int.MaxValue;
            // The document body contains only model-kept, source-bound facts. Exclusions stay in the audit sidecar;
            // they are not inserted as if they were accepted content.
            var candidates = pair.SelectMany(r => r.Scope.ReviewedSection.Run.Facts).ToList();
            var notAlreadyPresent = new List<GatheredFact>();
            foreach (var fact in candidates)
            {
                var preparedFact = fact;
                if (fact.Parts.Count > 1 && fact.Parts[0].Role == "label" && targetHeadingText.Contains(Norm(fact.Parts[0].Text)))
                {
                    var matchedLabel = fact.Parts[0];
                    matchedTargetLabels.Add(new { phase = anchor.Phase, topic = anchor.Topic, text = matchedLabel.Text,
                        source = new { matchedLabel.SourceId, matchedLabel.Location } });
                    preparedFact = fact with { Parts = fact.Parts.Skip(1).ToArray(), AlsoFrom = fact.AlsoFrom.Append(matchedLabel).ToArray() };
                }
                if (targetText.Contains(Norm(preparedFact.Render())))
                {
                    alreadyInTarget.Add(new { phase = anchor.Phase, topic = anchor.Topic, text = preparedFact.Render(),
                        sources = fact.Parts.Concat(fact.AlsoFrom).Select(p => new { p.SourceId, p.Location }).Distinct().ToArray() });
                    continue;
                }
                var structural = preparedFact.Flags.Where(f => f is "MULTIPLE_UNITS" or "MULTIPLE_VALUES").ToArray();
                if (structural.Length > 0 && IsResolvedMultiPartFact(fact, source))
                {
                    resolvedStructuralFlags.Add(new { phase = anchor.Phase, topic = anchor.Topic, text = preparedFact.Render(),
                        flags = structural, reason = "source-bound parts are consecutive paragraphs in one source and the scoped semantic review kept the fact" });
                    preparedFact = preparedFact with { Flags = preparedFact.Flags.Where(f => f is not ("MULTIPLE_UNITS" or "MULTIPLE_VALUES")).ToArray() };
                }
                notAlreadyPresent.Add(preparedFact);
            }
            var facts = notAlreadyPresent.GroupBy(f => FactKey(f.Render()), StringComparer.Ordinal).Select(group =>
            {
                var all = group.ToArray();
                var first = all[0];
                var repeated = all.Skip(1).SelectMany(f => f.Parts.Concat(f.AlsoFrom))
                    .DistinctBy(p => p.SourceId + "\u001f" + p.Location + "\u001f" + p.Start + "\u001f" + p.End).ToArray();
                if (repeated.Length > 0)
                    mergedDuplicates.Add(new { phase = anchor.Phase, topic = anchor.Topic, text = first.Render(),
                        sources = all.SelectMany(f => f.Parts.Concat(f.AlsoFrom)).Select(p => new { p.SourceId, p.Location }).Distinct().ToArray() });
                return first with { AlsoFrom = first.AlsoFrom.Concat(repeated).ToArray() };
            }).OrderBy(Position).ToArray();
            var runs = pair.Select(r =>
            {
                var resolved = resolvedRefusals[(r.Month, r.Phase, r.Topic)].ToHashSet();
                var run = r.Scope.ReviewedSection.Run;
                return run with { Refused = run.Refused.Where(f => !resolved.Contains(f.Index)).ToArray() };
            }).ToList();
            // Re-read source gap inventory after snapshot verification. Older Qwen review artifacts retain
            // their original gap records, while the output uses the current reader's verified inventory.
            var currentGaps = runs.SelectMany(r => r.Gaps).Where(g => g.SourceId is not ("T8" or "T9"))
                .Concat(source[8].Gaps).Concat(source[9].Gaps).Distinct().ToArray();
            var combined = new GatherRun(facts, runs.SelectMany(r => r.Refused).ToArray(), runs.SelectMany(r => r.Duplicates).ToArray(),
                runs.SelectMany(r => r.BatchFailures).ToArray(), runs.SelectMany(r => r.UnreviewedUnitIds).ToArray(),
                currentGaps, runs.Sum(r => r.Batches));
            sections.Add(new GatherSection(anchor.Heading.StableId!, anchor.Heading.Text, anchor.Topic, combined, GatherDocument.Conflicts(facts), next));
        }
        foreach (var (month, path) in paths)
            if (root.GetProperty("hashes").GetProperty(month.ToString()).GetString() != Hash(path))
                throw new InvalidDataException("SOURCE_CHANGED_DURING_WRITE_PREPARATION:" + month);
        Directory.CreateDirectory(output);
        var copy = Path.Combine(output, "Bao_cao_thang_10_tong_hop_Qwen.docx");
        var written = GatherWriteback.Apply(target, copy, sections, DateTime.UtcNow, true, Hash(target));
        using (var word = WordprocessingDocument.Open(copy, true))
        {
            var body = word.MainDocumentPart?.Document?.Body ?? throw new InvalidDataException("OUTPUT_BODY_MISSING");
            var outputSignature = body.Elements<Table>().LastOrDefault() ?? throw new InvalidDataException("OUTPUT_SIGNATURE_TABLE_MISSING");
            foreach (var row in outputSignature.Elements<TableRow>())
            {
                var properties = row.GetFirstChild<TableRowProperties>();
                if (properties is null) { properties = new TableRowProperties(); row.PrependChild(properties); }
                if (properties.GetFirstChild<CantSplit>() is null) properties.AppendChild(new CantSplit());
            }
            word.MainDocumentPart!.Document!.Save();
        }
        File.WriteAllText(Path.Combine(output, "writeback.json"), JsonSerializer.Serialize(new
        {
            mode = "qualification; broad target section boundaries manually checked", targetHash = Hash(target),
            result = written, groups = sections.Select(s => new { s.Heading, s.TargetLocation, s.NextHeadingLocation,
                factCount = s.Run.Facts.Count, s.Run.Refused, s.Run.BatchFailures, s.Run.UnreviewedUnitIds, s.Run.Gaps }),
            excludedCandidatePolicy = "excluded candidates remain in scopes.Excluded and are not written into the target document",
            alreadyInTarget = alreadyInTarget,
            matchedTargetLabels = matchedTargetLabels,
            mergedExactDuplicates = mergedDuplicates,
            resolvedStructuralFlags = resolvedStructuralFlags,
            scopes = records.Select(r => new { r.Month, r.Phase, r.Topic, r.Scope.UnproposedUnitIds, r.Scope.UnresolvedUnitIds,
                resolvedRefusalIndices = resolvedRefusals[(r.Month, r.Phase, r.Topic)],
                r.Scope.Selection.Duplicates, r.Scope.Excluded, coverage = r.Scope.CoverageReview }),
        }, opts));
        Console.WriteLine($"scopes={sections.Count} facts={sections.Sum(s => s.Run.Facts.Count)} skipped={written.SkippedHeadings.Count} output={copy}");
        return written.SkippedHeadings.Count == 0 ? 0 : 1;
    }

    static IReadOnlyList<int> ResolvedRefusals(GatherScopeResult scope, GatherSource source)
    {
        var units = source.Units.ToDictionary(u => u.Id, StringComparer.Ordinal);
        var copiedWholeUnits = scope.ReviewedSection.Run.Facts.Where(f => !f.Flags.Contains("SCOPE_UNREVIEWED", StringComparer.Ordinal))
            .SelectMany(f => f.Parts)
            .Where(p => units.TryGetValue(p.UnitId, out var u) &&
                p.Text == u.Text.Trim() && p.Start == u.Text.IndexOf(p.Text, StringComparison.Ordinal) && p.End == p.Start + p.Text.Length)
            .Select(p => p.UnitId).ToHashSet(StringComparer.Ordinal);
        return scope.Repair.Remaining
            .Where(r => r.Proposal is { Parts.Count: > 0 } && r.Proposal.Parts.All(p => copiedWholeUnits.Contains(p.UnitId)))
            .Select(r => r.Index).Distinct().Order().ToArray();
    }

    static bool IsResolvedMultiPartFact(GatheredFact fact, IReadOnlyDictionary<int, GatherSource> sources)
    {
        if (fact.Parts.Count < 2 || fact.Parts.Select(p => p.SourceId).Distinct(StringComparer.Ordinal).Count() != 1 ||
            fact.Parts[0].Role != "label" || fact.Parts.Skip(1).Any(p => p.Role is not ("value" or "text"))) return false;
        var sourceId = fact.Parts[0].SourceId;
        var source = sources.Values.SingleOrDefault(s => s.Units.Any(u => u.SourceId == sourceId));
        if (source is null) return false;
        var units = source.Units.ToDictionary(u => u.Id, StringComparer.Ordinal);
        var ordered = new List<(int Position, SourcedPart Part)>();
        foreach (var part in fact.Parts)
        {
            if (!units.TryGetValue(part.UnitId, out var unit) || part.Start != 0 || part.End != unit.Text.Length || part.Text != unit.Text)
                return false;
            var match = Regex.Match(part.Location, @"/p\[(\d+)\]$");
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var position)) return false;
            ordered.Add((position, part));
        }
        ordered.Sort((a, b) => a.Position.CompareTo(b.Position));
        return ordered.Zip(ordered.Skip(1), (a, b) => b.Position == a.Position + 1).All(x => x);
    }
}
