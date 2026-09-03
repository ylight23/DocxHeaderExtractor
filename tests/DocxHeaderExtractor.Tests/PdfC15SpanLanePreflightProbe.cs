using System.Security.Cryptography;
using System.Text.Json;
using DocxHeaderExtractor.Eval;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// C1.5 preflight, deliberately model-free. It freezes the 001 decision-relevant population before
/// the one instrumented B3 replication is observed. Source occurrence identity is every reviewed
/// PDF line, never the candidate id used only to debug this particular run.
/// </summary>
public sealed class PdfC15SpanLanePreflightProbe
{
    internal static readonly string[] FirstLossTaxonomy =
    [
        "ROLE_NO_DECISION",
        "ROLE_NON_HEADING",
        "SPAN_NOT_RUN",
        "SPAN_TIMEOUT",
        "SPAN_BATCH_EXCEPTION",
        "SPAN_UNRESOLVED",
        "SPAN_RESOLVED",
        "SPAN_RESOLVED_BUT_INVALID",
        "VALIDATED",
    ];

    [Fact]
    public void Freeze001DecisionRelevantOccurrenceIdentity()
    {
        var output = Environment.GetEnvironmentVariable("C15_PREFLIGHT_REPORT");
        if (string.IsNullOrWhiteSpace(output)) return;

        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var docx = Path.Combine(root, "todo10_8", "heading_corpus_95_word", "01_phap_quy",
            "001_Bo_luat_Dan_su_91-2015-QH13.docx");
        var bridgePath = Directory.GetFiles(Path.Combine(root, "keys", "occurrence-bridge"), "001_*.json").Single();
        var bridge = PdfReviewedOccurrenceBridge.Load(File.ReadAllText(bridgePath));
        var reviewed = bridge.Occurrences
            .Where(item => item.ReviewStatus == "reviewed" && item.RequiredLines.Count > 0)
            .ToArray();
        var classifications = PdfExtractorQualityBenchmarkProbe.Classify(docx, reviewed
            .Select(item => new PdfExtractorQualityBenchmarkProbe.Occurrence(item.GoldText, [],
                item.RequiredLines.Select(line => line.Index).ToArray()))
            .ToList());
        Assert.Equal(reviewed.Length, classifications.Count);

        var cohort = reviewed.Zip(classifications)
            .Where(pair => pair.Second.Selected && pair.Second.CoveringCandidateId is not null &&
                           pair.Second.DeterministicExclusionReason is null)
            .Select(pair => new
            {
                goldStableId = pair.First.GoldStableId,
                goldText = pair.First.GoldText,
                page = pair.First.Page,
                sourceLines = pair.First.RequiredLines.Select(line => new { line.Index, line.LineId, line.Text }),
                requiredLineIds = pair.Second.RequiredLineIds,
                // Debug-only: the evaluator must join checkpoint rows by requiredLineIds, not this id.
                debugCandidateId = pair.Second.CoveringCandidateId,
                debugRank = pair.Second.CoveringRank,
            })
            .ToArray();

        Assert.Equal(162, cohort.Length);
        var sourceSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(docx))).ToLowerInvariant();
        var json = JsonSerializer.Serialize(new
        {
            artifactKind = "c15_span_lane_preflight",
            usesModel = false,
            usesPipelineOutput = false,
            document = "001",
            sourceDocumentSha256 = sourceSha,
            bridgeFingerprint = bridge.PdfLineExtractionFingerprint,
            frozenB3Profile = new
            {
                backend = "OpenRouter",
                model = "qwen/qwen3.5-9b",
                wide = true,
                supplement = true,
                blocks = 160,
                requestTimeoutSeconds = 90,
                batchTimeoutSeconds = 120,
                laneDeadlineSeconds = 300,
                semanticConcurrency = "default_not_overridden",
            },
            denominator = new
            {
                reviewedOccurrences = reviewed.Length,
                decisionRelevantOccurrences = cohort.Length,
                identity = "goldStableId + page + requiredLineIds; candidate IDs are debug-only",
            },
            firstLossTaxonomy = FirstLossTaxonomy,
            occurrences = cohort,
        }, new JsonSerializerOptions { WriteIndented = true });

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, json);
    }
}
