using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Lane B of the N2-S follow-up: 057's semantic lane processed its decisionRelevant cohort almost
/// perfectly (23/24 validated), but none of those 23 reached <c>canonicalGroundings</c> - while 9 of
/// the document's 35 validated headings did, outside that cohort. This traces both populations through
/// the exact deterministic chain the live run used internally
/// (<see cref="PdfBlockGrounder.Ground"/> then <c>BuildBroadAlignmentForCandidateIds</c>), using only
/// facts already persisted by the canonical run's checkpoint - no provider call, no reimplemented
/// matching.
/// </summary>
public sealed class PdfN2SGroundingAlignmentTrace057Probe
{
    [Fact]
    public void TraceValidatedOccurrencesThroughGroundingAndAlignment()
    {
        var output = Environment.GetEnvironmentVariable("N2S_057_GROUNDING_TRACE_REPORT");
        if (string.IsNullOrWhiteSpace(output)) return;

        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var report = BuildReport(root);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void CommittedTraceReproducesFromCommittedCheckpointAndReconciliation()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var path = Path.Combine(root, "eval", "benchmark-n0", "n2-s", "counterfactual", "057-grounding-alignment-trace.v1.json");
        if (!File.Exists(path)) return;

        var expected = JsonSerializer.Serialize(BuildReport(root), new JsonSerializerOptions { WriteIndented = true });
        Assert.Equal(expected.Replace("\r\n", "\n"), File.ReadAllText(path).Replace("\r\n", "\n"));
    }

    private static object BuildReport(string root)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var checkpointPath = Path.Combine(root, "eval", "benchmark-n0", "n2-s", "checkpoints", "057-n2-s.jsonl");
        var checkpoint = File.ReadLines(checkpointPath)
            .Select(line => JsonSerializer.Deserialize<CheckpointEntry>(line, options))
            .Where(entry => entry is not null).Cast<CheckpointEntry>().ToArray();

        using var reconciliation = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "eval", "benchmark-n0", "n2-s", "reconciliation", "057-n2-s-reconciliation.v1.json")));
        var validatedTargets = reconciliation.RootElement.GetProperty("occurrences").EnumerateArray()
            .Where(o => o.GetProperty("validated").GetBoolean())
            .Select(o => (StableId: o.GetProperty("stableId").GetString()!, SourceFactId: o.GetProperty("coveringSourceFactId").GetString()!))
            .ToArray();

        var docx = Path.Combine(root, "todo10_8", "heading_corpus_95_word", "04_giao_trinh",
            "057_Quantitative_Methods_in_Finance_Lecture_Notes.docx");
        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docx);
        var selected = PdfLayoutEvidenceOutline.SelectRankedCandidates(snapshot.CandidateBlocks, snapshot.Audit.Candidates, 160).Selected;
        var contexts = PdfCandidateContextBuilder.Build(selected, snapshot.Annotations);
        var roles = checkpoint.Where(entry => entry.Lane == "semantic").SelectMany(entry => entry.Payload.Blocks)
            .ToDictionary(block => block.Id, StringComparer.Ordinal);
        var spans = checkpoint.Where(entry => entry.Lane == "span").SelectMany(entry => entry.Payload.Blocks)
            .Where(block => block.Resolved && block.Start is not null && block.End is not null)
            .ToDictionary(block => block.Id, StringComparer.Ordinal);

        var decisions = selected.Select(block =>
        {
            if (!roles.TryGetValue(block.Id, out var role))
                return new PdfBlockDecision(block.Id, PdfBlockRole.Uncertain, 0, "missing-checkpoint-role");
            var parsed = Enum.TryParse<PdfBlockRole>(role.Role, true, out var value) ? value : PdfBlockRole.Uncertain;
            return spans.TryGetValue(block.Id, out var span)
                ? new PdfBlockDecision(block.Id, parsed, role.Confidence, role.Reason ?? "checkpoint",
                    new TextOffsetSpan(span.Start!.Value, span.End!.Value))
                : new PdfBlockDecision(block.Id, parsed, role.Confidence, role.Reason ?? "checkpoint");
        }).ToArray();

        var validated = PdfProposalValidator.Validate(contexts, decisions);
        // Expected to reproduce the canonical run's own validated count (35) exactly, since nothing
        // here differs from what the live call computed; a mismatch is recorded, not silenced, and
        // does not by itself invalidate this trace - every target/control id below is checked
        // directly for membership, not inferred from the count.
        var validatedCountMatchesCanonicalRun = validated.Count == 35;

        var excluded = snapshot.Annotations.Where(a => a.ExcludeFromSemanticSamples).Select(a => a.Line).ToHashSet();
        var profile = PdfStyleClusterProfile.Learn(snapshot.Annotations.Where(a => !a.ExcludeFromSemanticSamples)
            .Select(a => a.Line).ToArray());
        var samples = PdfSemanticClusterAnalyst.BuildSamples(profile, snapshot.Lines, excluded);
        var validatedDecisions = validated.Select(item => decisions.Single(d => d.Id == item.SourceId)).ToArray();
        var grounding = PdfBlockGrounder.Ground(selected, validatedDecisions, profile, samples, [], requireLearnedCandidateStyle: false);
        var groundedIds = grounding.Headings.Select(h => h.Id).ToHashSet(StringComparer.Ordinal);
        var rejectedByCandidate = grounding.Rejected.ToDictionary(r => r.Id, StringComparer.Ordinal);

        var slim = new DocxSlimExtractor().Extract(docx);
        var alignment = PdfLayoutEvidenceOutline.BuildBroadAlignmentForCandidateIds(docx, slim, groundedIds);
        var traceByCandidate = alignment.Trace.ToDictionary(t => t.SourceBlockId, StringComparer.Ordinal);
        var emittedIds = alignment.Headings.Select(h => h.SourceId!).ToHashSet(StringComparer.Ordinal);

        object TraceOne(string sourceFactId)
        {
            if (!groundedIds.Contains(sourceFactId))
            {
                var reason = rejectedByCandidate.TryGetValue(sourceFactId, out var rejection) ? rejection.Reason : "not_in_validated_set";
                return new
                {
                    sourceFactId,
                    stage = "grounding",
                    owner = GroundingOwner(reason),
                    groundingRejectionReason = reason,
                };
            }

            if (!traceByCandidate.TryGetValue(sourceFactId, out var trace))
                return new { sourceFactId, stage = "alignment", owner = "EVALUATOR_JOIN_MISMATCH", note = "grounded but no alignment trace entry exists for this candidate id" };

            if (emittedIds.Contains(sourceFactId))
                return new { sourceFactId, stage = "emitted", owner = (string?)null, note = "reached canonicalGroundings" };

            return new
            {
                sourceFactId,
                stage = "alignment",
                owner = AlignmentOwner(trace.Branch.ToString(), trace.Accepted, trace.ParagraphIndex),
                alignmentBranch = trace.Branch.ToString(),
                alignmentAccepted = trace.Accepted,
                alignmentParagraphIndex = trace.ParagraphIndex,
            };
        }

        string? KindOf(string id) => snapshot.Provenance.TryGetValue(id, out var p) ? p.RepresentationKind.ToString() : null;
        var targetTraces = validatedTargets.Select(t => new { t.StableId, t.SourceFactId, kind = KindOf(t.SourceFactId), trace = TraceOne(t.SourceFactId) }).ToArray();
        var emittedControls = emittedIds
            .Select(id => new { sourceFactId = id, kind = KindOf(id), trace = TraceOne(id) })
            .OrderBy(x => x.sourceFactId, StringComparer.Ordinal)
            .ToArray();

        var ownerTally = targetTraces
            .Select(t => ((dynamic)t.trace).owner as string ?? "EMITTED")
            .GroupBy(o => o, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        using var run = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "eval", "benchmark-n0", "n2-s", "runs", "057-n2-s-run.v1.json")));
        var canonicalRunValidatedIds = run.RootElement.GetProperty("rows")[0].GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("sourceFactId").GetString()!).ToHashSet(StringComparer.Ordinal);
        var reconstructedValidatedIds = validated.Select(item => item.SourceId).ToHashSet(StringComparer.Ordinal);

        return new
        {
            schemaVersion = 1,
            artifactKind = "n2s_grounding_alignment_trace",
            documentId = "057",
            usesModel = false,
            reproducedValidatedCount = validated.Count,
            canonicalRunValidatedCount = canonicalRunValidatedIds.Count,
            validatedCountMatchesCanonicalRun,
            validatedOnlyInReconstruction = reconstructedValidatedIds.Except(canonicalRunValidatedIds).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            validatedOnlyInCanonicalRun = canonicalRunValidatedIds.Except(reconstructedValidatedIds).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            groundedCount = grounding.Headings.Count,
            emittedCount = alignment.Headings.Count,
            decisionRelevantValidatedTargets = validatedTargets.Length,
            ownerTally,
            targetKindTally = targetTraces.GroupBy(t => t.kind, StringComparer.Ordinal).ToDictionary(g => g.Key ?? "unknown", g => g.Count(), StringComparer.Ordinal),
            emittedControlKindTally = emittedControls.GroupBy(t => t.kind, StringComparer.Ordinal).ToDictionary(g => g.Key ?? "unknown", g => g.Count(), StringComparer.Ordinal),
            targets = targetTraces,
            emittedControls,
        };
    }

    private static string GroundingOwner(string reason) => reason switch
    {
        "unknown-block-id" => "EVALUATOR_JOIN_MISMATCH",
        "analyst-role-not-heading" => "SPAN_IDENTITY_MISMATCH",
        "low-block-confidence" => "GROUNDING_VALIDATOR_REJECTION",
        "not-visual-candidate-style" => "GROUNDING_VALIDATOR_REJECTION",
        "ungroundable-text-shape" => "GROUNDING_VALIDATOR_REJECTION",
        "not_in_validated_set" => "EVALUATOR_JOIN_MISMATCH",
        _ => "UNRESOLVED",
    };

    private static string AlignmentOwner(string branch, bool accepted, int? paragraphIndex) => (branch, accepted, paragraphIndex) switch
    {
        ("Unmatched", _, null) => "NO_DOCX_SOURCE_ANCHOR",
        (_, false, not null) => "GROUNDING_AMBIGUITY",
        (_, false, null) => "PDF_DOCX_ALIGNMENT_MISMATCH",
        _ => "UNRESOLVED",
    };

    private sealed record CheckpointEntry(string Lane, CheckpointPayload Payload);
    private sealed record CheckpointPayload(IReadOnlyList<CheckpointBlock> Blocks);
    private sealed record CheckpointBlock(string Id, string? Role, double Confidence, string? Reason, bool Resolved, int? Start, int? End);
}
