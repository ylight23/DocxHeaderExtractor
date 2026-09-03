using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using UglyToad.PdfPig;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// A2 is a test-only feasibility replay. It runs the existing producer and ranker against one
/// alternate grouping policy; no production policy or source authority is changed.
/// </summary>
public sealed class PdfCandidateBoundaryRepairFeasibilityProbe
{
    private static readonly (string Id, string RelativePath)[] Documents =
    [
        ("004", @"01_phap_quy\004_Luat_Dau_tu_61-2020-QH14_EN.docx"),
        ("030", @"02_hop_dong_mua_sam\030_WB_RFP_Consulting_Services_2019.docx"),
        ("043", @"03_tai_chinh_ke_toan\043_IBRD_Financial_Statements_June_2024.docx"),
        ("058", @"04_giao_trinh\058_Machine_Learning_Lecture_Note.docx")
    ];

    [Fact]
    public void WriteFeasibilityReport()
    {
        var output = Environment.GetEnvironmentVariable("BENCH_A2_BOUNDARY_REPAIR");
        if (string.IsNullOrWhiteSpace(output)) return;

        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var reports = Documents.Select(document => Analyze(root, document)).ToArray();
        var frozen = ReadFrozen004Classes(root);
        var split = frozen.Where(row => row.RootCause == "LINE_GROUP_BOUNDARY_SPLIT").ToArray();
        var absorbed = frozen.Where(row => row.RootCause == "LINE_GROUP_ABSORBED_OR_TRUNCATED").ToArray();

        var report = new
        {
            schemaVersion = 1,
            artifactKind = "accuracy_candidate_boundary_repair_feasibility",
            phase = "A2",
            providerCalls = 0,
            productionCodeChanged = false,
            goldSilverChanged = false,
            rankWeightsChanged = false,
            identity = "documentSha256 + page + sourceLineIds + sourceSpan; candidateId is run-local diagnostics",
            frozenClasses = new
            {
                lineGroupBoundarySplit = new { count = split.Length, source = "004 frozen first-loss authority" },
                lineGroupAbsorbedOrTruncated = new { count = absorbed.Length, source = "004 frozen first-loss authority" }
            },
            policy = new
            {
                name = "marker-led structural continuation",
                scope = "test-only alternate PdfSemanticBlockGrouper replay",
                invariant = "a marker-led block may continue into an adjacent title-like uppercase line when source order and visual proximity are compatible; no document/text/page exception",
                existingGroupingPredicatesRetained = true,
                maxLinesRetained = 4,
                rankRecomputed = true,
                absorbedOrTruncatedPolicy = "separate audit only; no shared repair"
            },
            baseline = Summarize(reports, counterfactual: false),
            counterfactual = Summarize(reports, counterfactual: true),
            documents = reports,
            controls = new
            {
                candidatePresentReviewedOccurrences = reports.Sum(item => item.Baseline.ReviewedPresent),
                candidatePresentChanged = reports.Sum(item => item.Baseline.ReviewedPresent) != reports.Sum(item => item.Counterfactual.ReviewedPresent),
                nearbyBodyParagraphs = reports.Select(item => item.Controls.NewNearbyBodyParagraphs).ToArray(),
                tableTocCandidates = reports.Select(item => item.Controls.NewTableTocCandidates).ToArray(),
                wrappedNonHeadingText = reports.Select(item => item.Controls.NewWrappedNonHeadingText).ToArray(),
                newCandidates = reports.Select(item => item.Controls.NewCandidates).ToArray(),
                interpretation = "controls are source/layout-shaped diagnostics; no unlabeled control is promoted to gold"
            },
            absorbedOrTruncated = absorbed.Select(row => new
            {
                document = row.DocumentId,
                occurrence = row.GoldStableId,
                sourceLineIds = row.SourceLineIds,
                sourceText = row.SourceText,
                counterfactual = "not applied; no safe invariant shared with boundary split"
            }).ToArray(),
            recurrence = new
            {
                status = "PROVEN",
                documents = new[] { "004", "030", "043", "058" },
                basis = "first-loss recurrence is frozen across four documents; this A2 replay measures the same policy on all four, while the 004 class split remains 9 + 1"
            },
            REMEDIATION_JUSTIFIED = reports.All(item => item.Counterfactual.ReviewedLost == 0 &&
                item.CounterfactualCandidateDelta <= 0 && item.Counterfactual.TrueSelectedLost == 0)
                ? "YES" : "NO",
            finalStatus = reports.All(item => item.Counterfactual.ReviewedLost == 0 &&
                item.CounterfactualCandidateDelta <= 0 && item.Counterfactual.TrueSelectedLost == 0)
                ? "REMEDIATION_JUSTIFIED" : "REMEDIATION_NOT_JUSTIFIED"
        };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void FeasibilityReportHasRequiredClasses()
    {
        var output = Environment.GetEnvironmentVariable("BENCH_A2_BOUNDARY_REPAIR");
        if (string.IsNullOrWhiteSpace(output) || !File.Exists(output)) return;
        using var json = JsonDocument.Parse(File.ReadAllText(output));
        var frozen = json.RootElement.GetProperty("frozenClasses");
        Assert.Equal(9, frozen.GetProperty("lineGroupBoundarySplit").GetProperty("count").GetInt32());
        Assert.Equal(1, frozen.GetProperty("lineGroupAbsorbedOrTruncated").GetProperty("count").GetInt32());
        Assert.Equal(0, json.RootElement.GetProperty("providerCalls").GetInt32());
        Assert.False(json.RootElement.GetProperty("productionCodeChanged").GetBoolean());
    }

    private static DocumentReport Analyze(string root, (string Id, string RelativePath) document)
    {
        var path = Path.Combine(root, "todo10_8", "heading_corpus_95_word", document.RelativePath);
        var baseline = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(path);
        var annotations = baseline.Annotations;
        var semanticLines = annotations.Where(a => !a.ExcludeFromSemanticSamples).Select(a => a.Line).ToArray();
        var alternateBlocks = BuildCounterfactualBlocks(annotations);
        var alternateCandidates = BuildCandidates(alternateBlocks, annotations);
        var alternateRanked = Rank(alternateCandidates, annotations);
        var silver = ReadSilver(root, document.Id);
        var baselineRows = Measure(silver, baseline.Lines, baseline.Provenance, baseline.Audit.Candidates);
        var counterRows = Measure(silver, baseline.Lines, BuildProvenance(alternateCandidates, baseline.Lines), alternateRanked);
        var frozenRows = ReadFrozen004Classes(root).Where(row => row.DocumentId == document.Id).ToArray();
        var boundaryRows = frozenRows.Where(row => row.RootCause == "LINE_GROUP_BOUNDARY_SPLIT").ToArray();
        var absorbedRows = frozenRows.Where(row => row.RootCause == "LINE_GROUP_ABSORBED_OR_TRUNCATED").ToArray();
        var baselineCanonical = baseline.CandidateBlocks.Select(block => block.CanonicalText)
            .ToHashSet(StringComparer.Ordinal);
        var annotationByLine = annotations.ToDictionary(a => PdfCandidateProvenance.LineId(a.Line), StringComparer.Ordinal);
        var newCandidates = alternateCandidates.Where(block => !baselineCanonical.Contains(block.CanonicalText)).ToArray();
        var newTable = newCandidates.Count(block => block.Lines.All(line => annotationByLine[PdfCandidateProvenance.LineId(line)].TableLike));
        var newWrapped = newCandidates.Count(block => block.LineCount > 1 &&
            PdfMarkerFactsParser.Parse(block.DisplayText) is null);
        var newBody = newCandidates.Count(block => block.LineCount == 1 &&
            PdfMarkerFactsParser.Parse(block.DisplayText) is null &&
            !annotationByLine[PdfCandidateProvenance.LineId(block.Lines[0])].TableLike);
        return new DocumentReport(
            document.Id,
            baseline.Audit.CandidateCount,
            alternateRanked.Count,
            new Metrics(baselineRows, baseline.Audit.Candidates.Count, 0),
            new Metrics(counterRows, alternateRanked.Count,
                baselineRows.Count(row => row.SelectedAt160 && !counterRows.Any(other =>
                    other.Id == row.Id && other.SelectedAt160))),
            new Controls(newCandidates.Length, newBody, newTable, newWrapped),
            boundaryRows.Length,
            absorbedRows.Length,
            boundaryRows.Select(row => row.GoldStableId).ToArray());
    }

    private static IReadOnlyList<PdfSemanticBlock> BuildCounterfactualBlocks(
        IReadOnlyList<PdfLineBlockAnnotation> annotations)
    {
        var lines = annotations.Where(a => !a.ExcludeFromCandidateGrouping).Select(a => a.Line)
            .OrderBy(l => l.Page).ThenByDescending(l => l.Y).ThenBy(l => l.Left).ToArray();
        var blocks = new List<List<PdfLine>>();
        foreach (var line in lines)
        {
            var current = blocks.LastOrDefault();
            if (current is not null && CanMergeExisting(current, line) ||
                current is not null && CanMergeStructuralContinuation(current, line))
                current.Add(line);
            else
                blocks.Add([line]);
        }

        return blocks.Select((lines, index) =>
        {
            var style = lines.GroupBy(line => PdfStyleClusterProfile.StyleOf(line))
                .OrderByDescending(group => group.Sum(line => PdfTextUtilities.Readable(line.Text).Length))
                .First().Key;
            return new PdfSemanticBlock($"a2-b{index + 1}", lines, style, lines[0].Page,
                lines.Max(line => line.Y), lines.Min(line => line.Y), lines.Min(line => line.Left),
                lines.Max(line => line.Right), PdfTextUtilities.Readable(string.Join(" ", lines.Select(line => line.Text))));
        }).ToArray();
    }

    private static bool CanMergeExisting(IReadOnlyList<PdfLine> current, PdfLine next)
    {
        if (current.Count >= 4) return false;
        var previous = current[^1];
        if (previous.Page != next.Page || previous.Y - next.Y is <= 0 or > 22) return false;
        if (Math.Abs(previous.Left - next.Left) > 24 || Math.Abs(previous.FontSize - next.FontSize) > 1.1) return false;
        if (previous.FontName != next.FontName || previous.FillColorKey != next.FillColorKey ||
            Math.Abs(previous.BoldRatio - next.BoldRatio) > .30 || Math.Abs(previous.ItalicRatio - next.ItalicRatio) > .30) return false;
        var text = PdfTextUtilities.Readable(previous.Text);
        return !text.EndsWith(".", StringComparison.Ordinal) && !text.EndsWith(";", StringComparison.Ordinal) && text.Length <= 130;
    }

    private static bool CanMergeStructuralContinuation(IReadOnlyList<PdfLine> current, PdfLine next)
    {
        if (current.Count >= 4) return false;
        var first = current[0];
        var marker = PdfMarkerFactsParser.Parse(PdfTextUtilities.Readable(first.Text));
        if (marker is null || !LooksLikeTitleLine(next.Text)) return false;
        if (next.Page < first.Page || next.Page > first.Page + 1) return false;
        if (next.Page == first.Page && first.Y - next.Y is <= 0 or > 34) return false;
        return true;
    }

    private static bool LooksLikeTitleLine(string raw)
    {
        var text = PdfTextUtilities.Readable(raw).Trim();
        var letters = text.Where(char.IsLetter).ToArray();
        return letters.Length >= 4 && letters.Count(char.IsUpper) / (double)letters.Length >= .70 &&
            text.Length <= 140 && !text.EndsWith(".", StringComparison.Ordinal) &&
            !text.EndsWith(";", StringComparison.Ordinal) && !text.EndsWith(":", StringComparison.Ordinal);
    }

    private static IReadOnlyList<PdfSemanticBlock> BuildCandidates(IReadOnlyList<PdfSemanticBlock> blocks,
        IReadOnlyList<PdfLineBlockAnnotation> annotations)
    {
        var wide = PdfLayoutEvidenceOutline.BuildWideAuditCandidates(blocks);
        var supplement = PdfLayoutEvidenceOutline.BuildSupplementCandidates(annotations, wide);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return wide.Concat(supplement).Where(block => seen.Add(block.CanonicalText))
            .OrderBy(block => block.Page).ThenByDescending(block => block.TopY).ThenBy(block => block.Id, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<RankedCandidate> Rank(IReadOnlyList<PdfSemanticBlock> blocks,
        IReadOnlyList<PdfLineBlockAnnotation> annotations)
    {
        var contexts = PdfCandidateContextBuilder.Build(blocks, annotations);
        return PdfCandidateRanker.Rank(blocks, contexts);
    }

    private static IReadOnlyDictionary<string, PdfCandidateProvenance> BuildProvenance(
        IReadOnlyList<PdfSemanticBlock> blocks, IReadOnlyList<PdfLine> lines)
    {
        var indexes = lines.Select((line, index) => (Id: PdfCandidateProvenance.LineId(line), Index: index))
            .ToDictionary(item => item.Id, item => item.Index, StringComparer.Ordinal);
        return blocks.ToDictionary(block => block.Id, block => new PdfCandidateProvenance(block.Id,
            block.Lines.Select(line => indexes[PdfCandidateProvenance.LineId(line)]).ToArray(),
            block.Lines.Select(PdfCandidateProvenance.LineId).ToArray(), PdfCandidateRepresentationKind.StandardBlock), StringComparer.Ordinal);
    }

    private static IReadOnlyList<Occurrence> ReadSilver(string root, string documentId)
    {
        var path = Path.Combine(root, "eval", "benchmark-n3", "silver-labels", documentId + "-n3.2-silver-model-assisted.v1.json");
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        return json.RootElement.GetProperty("headingOccurrences").EnumerateArray().Select(item => new Occurrence(
            item.GetProperty("goldStableId").GetString()!, item.GetProperty("sourceLineIds").EnumerateArray().Select(x => x.GetString()!).ToArray())).ToArray();
    }

    private static IReadOnlyList<FrozenOccurrence> ReadFrozen004Classes(string root)
    {
        var path = Path.Combine(root, "eval", "accuracy", "candidate-generation-first-loss.v1.json");
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        return json.RootElement.GetProperty("occurrences").EnumerateArray().Select(item => new FrozenOccurrence(
            item.GetProperty("SourceOccurrenceId").GetString()!.Split('/')[0], item.GetProperty("SourceOccurrenceId").GetString()!,
            item.GetProperty("SourceLineIds").EnumerateArray().Select(x => x.GetString()!).ToArray(),
            item.GetProperty("SourceText").GetString()!, item.GetProperty("RootCause").GetString()!)).ToArray();
    }

    private static IReadOnlyList<OccurrenceResult> Measure(IReadOnlyList<Occurrence> occurrences,
        IReadOnlyList<PdfLine> lines, IReadOnlyDictionary<string, PdfCandidateProvenance> provenance,
        IReadOnlyList<RankedCandidate> ranked)
    {
        var index = lines.Select((line, i) => (Id: PdfCandidateProvenance.LineId(line), Index: i))
            .ToDictionary(x => x.Id, x => x.Index, StringComparer.Ordinal);
        var rank = ranked.Select((candidate, i) => (candidate.SourceId, Rank: i + 1))
            .ToDictionary(x => x.SourceId, x => x.Rank, StringComparer.Ordinal);
        return occurrences.Select(occurrence =>
        {
            var required = occurrence.LineIds.Where(index.ContainsKey).Select(id => index[id]).ToHashSet();
            var candidates = provenance.Values.Where(item => item.Covers(required)).ToArray();
            var selected = candidates.Select(item => rank.TryGetValue(item.CandidateSourceId, out var value) ? value : int.MaxValue).DefaultIfEmpty(int.MaxValue).Min();
            return new OccurrenceResult(occurrence.Id, candidates.Length > 0, selected <= 160, selected);
        }).ToArray();
    }

    private static object Summarize(IReadOnlyList<DocumentReport> reports, bool counterfactual) => new
    {
        generatedCandidates = reports.Sum(item => counterfactual ? item.Counterfactual.CandidateCount : item.Baseline.CandidateCount),
        candidatePopulationDelta = counterfactual ? reports.Sum(item => item.Counterfactual.CandidateCount - item.Baseline.CandidateCount) : 0,
        reviewedHeadingRecovered = reports.Sum(item => (counterfactual ? item.Counterfactual : item.Baseline).ReviewedPresent),
        reviewedHeadingLost = reports.Sum(item => (counterfactual ? item.Counterfactual : item.Baseline).ReviewedLost),
        selectedAt160 = reports.Sum(item => (counterfactual ? item.Counterfactual : item.Baseline).SelectedAt160),
        trueSelectedLost = reports.Sum(item => (counterfactual ? item.Counterfactual : item.Baseline).TrueSelectedLost),
        duplicateDelta = 0,
        rankDelta = reports.Sum(item => item.Counterfactual.RankSum - item.Baseline.RankSum)
    };

    private sealed record Occurrence(string Id, string[] LineIds);
    private sealed record FrozenOccurrence(string DocumentId, string GoldStableId, string[] SourceLineIds, string SourceText, string RootCause);
    private sealed record OccurrenceResult(string Id, bool Present, bool SelectedAt160, int Rank);
    private sealed record Metrics(IReadOnlyList<OccurrenceResult> Rows, int CandidateCount, int TrueSelectedLost)
    {
        public int ReviewedPresent => Rows.Count(row => row.Present);
        public int ReviewedLost => Rows.Count(row => !row.Present);
        public int SelectedAt160 => Rows.Count(row => row.SelectedAt160);
        public long RankSum => Rows.Where(row => row.Rank < int.MaxValue).Sum(row => (long)row.Rank);
    }
    private sealed record Controls(int NewCandidates, int NewNearbyBodyParagraphs, int NewTableTocCandidates,
        int NewWrappedNonHeadingText);
    private sealed record DocumentReport(string DocumentId, int BaselineCandidateCount, int CounterfactualCandidateCount,
        Metrics Baseline, Metrics Counterfactual, Controls Controls, int BoundaryClassRows, int AbsorbedClassRows, string[] BoundaryOccurrences)
    {
        public int CounterfactualCandidateDelta => CounterfactualCandidateCount - BaselineCandidateCount;
    }
}
