using System.Text.Json;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// N3.7-A: bounded investigation into a deterministic invariant for the 6
/// `OWNER_A_ROLE_PLUS_DEGENERATE_SPAN` cases from N3.6, before any remediation. Not
/// <c>span.Length &lt;= 4</c> (a genuine heading marker like "I." or "1." can be that short) - the
/// candidate invariant is structural: <b>the resolved span, once the recognized marker is accounted
/// for, contains no semantic payload beyond it, while the source candidate's full text does carry
/// non-marker semantic content the span excluded.</b> A genuine marker-only heading (nothing beyond the
/// marker exists in the source at all) must not trip this - only a span that visibly discarded real
/// payload the source candidate had should.
/// <para>
/// Tested against the 6 positive cases and three independent control corpora of real, silver-supported
/// emitted headings (004's own R1 recoveries, 003's counterfactual recoveries from its own committed
/// checkpoint, and 057's naturally-resolved complete-lane validated items) - a genuine heading rejected
/// by this invariant anywhere in those controls is a stop condition, not a detail to explain away.
/// </para>
/// </summary>
public sealed class PdfN37ADegenerateSpanInvariantProbe
{
    [Fact]
    public void WriteInvestigation()
    {
        var output = Environment.GetEnvironmentVariable("N37A_REPORT");
        if (string.IsNullOrWhiteSpace(output)) return;

        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var report = BuildReport(root);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void CommittedInvestigationReproduces()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var path = Path.Combine(root, "eval", "benchmark-n3", "n3.4", "reports", "004-n3.7a-degenerate-span-invariant.v1.json");
        if (!File.Exists(path)) return;

        var expected = JsonSerializer.Serialize(BuildReport(root), new JsonSerializerOptions { WriteIndented = true });
        Assert.Equal(expected.Replace("\r\n", "\n"), File.ReadAllText(path).Replace("\r\n", "\n"));
    }

    /// <summary>
    /// The invariant itself, isolated so the positive and every control corpus run through the exact
    /// same check. Returns true only when the span is structurally marker-only AND the full candidate
    /// text has semantic content the span excluded - never on span length alone.
    /// </summary>
    private static (bool IsMarkerOnlySpanWithLostPayload, string SpanText, string? MarkerRaw, string Residue) EvaluateInvariant(
        string fullText, int spanStart, int spanEnd)
    {
        if (spanStart < 0 || spanEnd > fullText.Length || spanEnd <= spanStart)
            return (false, "", null, "");

        var spanText = fullText[spanStart..spanEnd];
        var marker = SourceFactsBuilder.FromPdfBlock(PdfSemanticBlockStub(fullText)).Marker;
        if (marker is null) return (false, spanText, null, spanText.Trim());

        // Residue: what the span contains beyond the recognized marker. If the span text starts with
        // the marker, strip it; if the span text is itself wholly contained within the marker's own
        // characters (a truncated marker fragment), residue is empty by definition.
        string residue;
        if (spanText.StartsWith(marker.Raw, StringComparison.Ordinal))
            residue = spanText[marker.Raw.Length..];
        else if (marker.Raw.StartsWith(spanText, StringComparison.Ordinal))
            residue = "";
        else
            residue = spanText; // span doesn't align with the marker at all - not a marker-only span

        residue = residue.Trim();
        var spanIsMarkerOnly = residue.Length == 0;

        // The full source text must carry real semantic content beyond the marker for this to be a
        // LOST-payload case, not a genuine marker-only heading with nothing to lose.
        var fullTextResidue = fullText.StartsWith(marker.Raw, StringComparison.Ordinal)
            ? fullText[marker.Raw.Length..].Trim()
            : fullText.Trim();
        var sourceHasSemanticPayloadBeyondMarker = fullTextResidue.Length >= 4 && fullTextResidue.Any(char.IsLetter);

        return (spanIsMarkerOnly && sourceHasSemanticPayloadBeyondMarker, spanText, marker.Raw, residue);
    }

    private static object BuildReport(string root)
    {
        using var diagnosis = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "eval", "benchmark-n3", "n3.4", "reports", "004-n3.6-twelve-output-diagnosis.v1.json")));
        var twelveRows = diagnosis.RootElement.GetProperty("rows").EnumerateArray()
            .Select(r => (
                SourceId: r.GetProperty("sourceId").GetString()!,
                OwnerClass: r.GetProperty("ownerClass").GetString()!,
                FullText: r.GetProperty("groundingStage").GetProperty("sourceTextFull").GetString()!,
                Start: r.GetProperty("spanStage").GetProperty("start").GetInt32(),
                End: r.GetProperty("spanStage").GetProperty("end").GetInt32()))
            .ToArray();

        var positiveRows = twelveRows.Select(t =>
        {
            var (fires, spanText, markerRaw, residue) = EvaluateInvariant(t.FullText, t.Start, t.End);
            return new { t.SourceId, t.OwnerClass, spanText, markerRaw, residue, invariantFires = fires };
        }).ToArray();

        var control004 = Control004(root);
        var control003 = Control003(root);
        var control057 = Control057(root);

        var allControlFalsePositives = control004.FalsePositives.Concat(control003.FalsePositives).Concat(control057.FalsePositives).ToArray();

        return new
        {
            schemaVersion = 1,
            artifactKind = "n3_7a_degenerate_span_invariant_investigation",
            usesModel = false,
            purpose = "Bounded diagnosis only - test whether the invariant rejects exactly Owner A's 6 cases and zero genuine headings across independent control corpora. No production code change here.",
            invariantDefinition = "resolved span, once the recognized production marker is accounted for, carries no semantic residue, AND the source candidate's full text does carry >=4 chars of semantic (letter-bearing) content beyond the marker that the span excluded.",
            positiveCases = new
            {
                total = positiveRows.Length,
                ownerAFiredCount = positiveRows.Count(r => r.OwnerClass == "OWNER_A_ROLE_PLUS_DEGENERATE_SPAN" && r.invariantFires),
                ownerACount = positiveRows.Count(r => r.OwnerClass == "OWNER_A_ROLE_PLUS_DEGENERATE_SPAN"),
                ownerBFiredCount = positiveRows.Count(r => r.OwnerClass == "OWNER_B_ROLE_ONLY_SPAN_WELL_FORMED" && r.invariantFires),
                rows = positiveRows,
            },
            control004,
            control003,
            control057,
            verdict = new
            {
                allSixOwnerARejected = positiveRows.Count(r => r.OwnerClass == "OWNER_A_ROLE_PLUS_DEGENERATE_SPAN" && r.invariantFires) == 6,
                zeroOwnerBFalselyRejected = positiveRows.Count(r => r.OwnerClass == "OWNER_B_ROLE_ONLY_SPAN_WELL_FORMED" && r.invariantFires) == 0,
                zeroGenuineHeadingsLostAcrossControls = allControlFalsePositives.Length == 0,
                totalGenuineHeadingsChecked = control004.TotalChecked + control003.TotalChecked + control057.TotalChecked,
            },
        };
    }

    private sealed record Control004Result(int TotalChecked, object[] FalsePositives, int OwnerAAlreadyExcluded);

    private static Control004Result Control004(string root)
    {
        using var collateral = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "eval", "benchmark-n3", "n3.4", "reports", "004-n3.4-collateral-check.v1.json")));
        var collateralIds = collateral.RootElement.GetProperty("r1").GetProperty("trueCollateralItems").EnumerateArray()
            .Select(i => i.GetProperty("SourceId").GetString()!).ToHashSet(StringComparer.Ordinal);

        using var run = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "eval", "benchmark-n3", "n3.4", "runs", "004-n3.4-canonical-run.v1.json")));
        using var silver = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "eval", "benchmark-n3", "silver-labels", "004-n3.2-silver-model-assisted.v1.json")));

        var silverLineSets = silver.RootElement.GetProperty("headingOccurrences").EnumerateArray()
            .Select(o => o.GetProperty("sourceLineIds").EnumerateArray().Select(l => l.GetString()!).ToHashSet(StringComparer.Ordinal))
            .ToArray();

        var falsePositives = new List<object>();
        var checkedCount = 0;
        foreach (var item in run.RootElement.GetProperty("rows")[0].GetProperty("items").EnumerateArray())
        {
            var sourceId = item.GetProperty("sourceFactId").GetString()!;
            if (collateralIds.Contains(sourceId)) continue; // these are Owner A/B themselves, not controls
            var lineIds = item.GetProperty("lineIds").EnumerateArray().Select(l => l.GetString()!).ToHashSet(StringComparer.Ordinal);
            var isRealHeading = silverLineSets.Any(required => required.All(lineIds.Contains));
            if (!isRealHeading) continue; // only genuine, silver-supported headings are controls

            checkedCount++;
            var fullText = item.GetProperty("sourceBlockText").GetString()!;
            var span = item.GetProperty("headingSpan");
            var (fires, _, _, _) = EvaluateInvariant(fullText, span.GetProperty("start").GetInt32(), span.GetProperty("end").GetInt32());
            if (fires) falsePositives.Add(new { corpus = "004", sourceId, fullText = Truncate(fullText) });
        }

        return new Control004Result(checkedCount, falsePositives.ToArray(), 0);
    }

    private sealed record Control003Result(int TotalChecked, object[] FalsePositives);

    private static Control003Result Control003(string root)
    {
        var checkpointPath = Path.Combine(root, "eval", "benchmark-n0", "n2-s", "checkpoints", "003-n2-s.jsonl");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var spanBlocks = File.ReadLines(checkpointPath)
            .Select(line => JsonSerializer.Deserialize<CheckpointEntry>(line, options))
            .Where(e => e is not null).Cast<CheckpointEntry>()
            .Where(e => e.Lane == "span")
            .SelectMany(e => e.Payload.Blocks)
            .Where(b => b.Resolved && b.Start is not null && b.End is not null)
            .ToDictionary(b => b.Id, StringComparer.Ordinal);

        using var silver = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "eval", "benchmark-n0", "silver-labels", "003-n1.2-silver-model-assisted.v1.json")));
        var silverLineSets = silver.RootElement.GetProperty("headingOccurrences").EnumerateArray()
            .Select(o => o.GetProperty("sourceLineIds").EnumerateArray().Select(l => l.GetString()!).ToHashSet(StringComparer.Ordinal))
            .ToArray();

        var docx = Path.Combine(root, "todo10_8", "heading_corpus_95_word", "01_phap_quy", "003_Luat_Doanh_nghiep_59-2020-QH14.docx");
        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docx);
        var candidatesById = snapshot.CandidateBlocks.ToDictionary(b => b.Id, StringComparer.Ordinal);
        var provenance = snapshot.Provenance;

        var falsePositives = new List<object>();
        var checkedCount = 0;
        foreach (var (id, span) in spanBlocks)
        {
            if (!candidatesById.TryGetValue(id, out var block)) continue;
            if (!provenance.TryGetValue(id, out var prov)) continue;
            var isRealHeading = silverLineSets.Any(required => required.All(l => prov.LineIds.Contains(l, StringComparer.Ordinal)));
            if (!isRealHeading) continue;

            checkedCount++;
            var (fires, _, _, _) = EvaluateInvariant(block.Text, span.Start!.Value, span.End!.Value);
            if (fires) falsePositives.Add(new { corpus = "003", sourceId = id, fullText = Truncate(block.Text) });
        }

        return new Control003Result(checkedCount, falsePositives.ToArray());
    }

    private sealed record Control057Result(int TotalChecked, object[] FalsePositives);

    private static Control057Result Control057(string root)
    {
        using var run = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "eval", "benchmark-n0", "n2-s", "runs", "057-n2-s-run.v1.json")));
        using var silver = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "eval", "benchmark-n0", "silver-labels", "057-n1.2-silver-model-assisted.v1.json")));
        var silverLineSets = silver.RootElement.GetProperty("headingOccurrences").EnumerateArray()
            .Select(o => o.GetProperty("sourceLineIds").EnumerateArray().Select(l => l.GetString()!).ToHashSet(StringComparer.Ordinal))
            .ToArray();

        var falsePositives = new List<object>();
        var checkedCount = 0;
        foreach (var item in run.RootElement.GetProperty("rows")[0].GetProperty("items").EnumerateArray())
        {
            var lineIds = item.GetProperty("lineIds").EnumerateArray().Select(l => l.GetString()!).ToHashSet(StringComparer.Ordinal);
            var isRealHeading = silverLineSets.Any(required => required.All(lineIds.Contains));
            if (!isRealHeading) continue;

            checkedCount++;
            var fullText = item.GetProperty("sourceBlockText").GetString()!;
            var span = item.GetProperty("headingSpan");
            var (fires, _, _, _) = EvaluateInvariant(fullText, span.GetProperty("start").GetInt32(), span.GetProperty("end").GetInt32());
            if (fires) falsePositives.Add(new { corpus = "057", sourceId = item.GetProperty("sourceFactId").GetString(), fullText = Truncate(fullText) });
        }

        return new Control057Result(checkedCount, falsePositives.ToArray());
    }

    private static string Truncate(string value) => value.Length <= 100 ? value : value[..100] + "...";

    /// <summary>Minimal stand-in so the production marker parser (which reads only .Text/.Lines) can run over an arbitrary string without reconstructing a full PdfSemanticBlock.</summary>
    private static PdfSemanticBlock PdfSemanticBlockStub(string text) =>
        new("stub", [], new PdfStyleKey(12, "stub", "none"), 0, 0, 0, 0, 0, text);

    private sealed record CheckpointEntry(string Lane, CheckpointPayload Payload);
    private sealed record CheckpointPayload(IReadOnlyList<CheckpointBlock> Blocks);
    private sealed record CheckpointBlock(string Id, string? Role, double Confidence, string? Reason, bool Resolved, int? Start, int? End);
}
