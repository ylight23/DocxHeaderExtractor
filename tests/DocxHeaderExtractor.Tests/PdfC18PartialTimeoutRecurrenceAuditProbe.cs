using System.Text.Json;
using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// C1.8 inventories persisted evidence only. A partial timeout is not counted as a recurrence of
/// the discard-loss mechanism unless the artifact also proves completed valid spans and a material
/// downstream loss. It never invokes a provider or reconstructs a span result.
/// </summary>
public sealed class PdfC18PartialTimeoutRecurrenceAuditProbe
{
    [Fact]
    public void AuditExistingArtifactsForTheCompletePartialTimeoutFailureShape()
    {
        var output = Environment.GetEnvironmentVariable("C18_REPORT");
        if (string.IsNullOrWhiteSpace(output)) return;

        var root = Environment.GetEnvironmentVariable("C18_ROOT")
            ?? Path.Combine(PdfExtractorQualityBenchmarkProbe.RepositoryRoot(), ".verify-build");
        var findings = Analyze(root);
        var report = new
        {
            artifactKind = "c18_partial_timeout_recurrence_audit",
            usesModel = false,
            root = Path.GetFullPath(root),
            failureShape = "partial_timeout + completed_valid_spans + wrapper_discard + material_downstream_loss",
            summary = findings.GroupBy(finding => finding.Classification)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            findings,
            conclusion = findings.Count(finding => finding.Classification == "class_1_strongest_positive_recurrence") >= 2
                ? "cross_document_mechanism_recurrence_observed"
                : "cross_document_mechanism_recurrence_not_proven",
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void RequiresTheWholeFailureShapeBeforeCallingItARecurrence()
    {
        var root = Path.Combine(Path.GetTempPath(), "dhx-c18-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            WriteHierarchy(root, "001", "partial_timeout");
            WriteC16(root, "001", valid: 3);
            WriteC17(root, "001", emitted: 2);
            File.WriteAllText(Path.Combine(root, "001-span.jsonl"), "{\"lane\":\"span\",\"payload\":{\"blocks\":[{\"resolved\":true}]}}\n");

            WriteHierarchy(root, "002", "partial_timeout");
            WriteC16(root, "002", valid: 1);
            File.WriteAllText(Path.Combine(root, "002-span.jsonl"), "{\"lane\":\"span\",\"payload\":{\"blocks\":[{\"resolved\":true}]}}\n");

            WriteHierarchy(root, "003", "partial_timeout");
            File.WriteAllText(Path.Combine(root, "003-span.jsonl"), "{\"lane\":\"span\",\"payload\":{\"blocks\":[{\"resolved\":false}]}}\n");
            WriteHierarchy(root, "004", "complete");

            var findings = Analyze(root).OrderBy(finding => finding.Document).ToArray();
            Assert.Equal("class_1_strongest_positive_recurrence", findings.Single(item => item.Document == "001").Classification);
            Assert.Equal("class_2_mechanism_only", findings.Single(item => item.Document == "002").Classification);
            Assert.Equal("class_3_timeout_without_usable_completed_spans", findings.Single(item => item.Document == "003").Classification);
            var control = findings.Single(item => item.Document == "004");
            Assert.Equal("class_4_negative_control", control.Classification);
            Assert.True(control.PreserveIsNoOp);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static IReadOnlyList<Finding> Analyze(string root)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var files = Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).ToArray();
        var c16 = files.Where(path => Path.GetFileName(path).Contains("c16-span-audit", StringComparison.OrdinalIgnoreCase))
            .Select(ReadC16).Where(item => item is not null).Cast<C16Evidence>()
            .ToDictionary(item => item.Document, StringComparer.Ordinal);
        var c17 = files.Where(path => Path.GetFileName(path).Contains("c17-partial-preserve", StringComparison.OrdinalIgnoreCase))
            .Select(ReadC17).Where(item => item is not null).Cast<C17Evidence>()
            .ToDictionary(item => item.Document, StringComparer.Ordinal);
        var checkpoints = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
            .GroupBy(DocumentKeyFromPath, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        var runs = files.SelectMany(ReadHierarchyRuns).ToArray();
        return runs.Select(run =>
        {
            checkpoints.TryGetValue(run.Document, out var checkpointPaths);
            var resolvedCheckpointBlocks = checkpointPaths?.Sum(CountResolvedSpanBlocks) ?? 0;
            c16.TryGetValue(run.Document, out var validation);
            c17.TryGetValue(run.Document, out var counterfactual);
            var validCompletedSpans = validation?.ValidCompletedSpans ?? 0;
            var materialDownstreamLoss = counterfactual?.EmittedReviewedOccurrences ?? 0;
            var hasPartialTimeout = string.Equals(run.SpanLaneStatus, "partial_timeout", StringComparison.Ordinal);
            var classification = !hasPartialTimeout
                ? "class_4_negative_control"
                : validCompletedSpans > 0 && materialDownstreamLoss > 0
                    ? "class_1_strongest_positive_recurrence"
                    : validCompletedSpans > 0
                        ? "class_2_mechanism_only"
                        : checkpointPaths is { Length: > 0 }
                            ? "class_3_timeout_without_usable_completed_spans"
                            : "insufficient_artifact";
            return new Finding(
                run.Document,
                run.SourceDocumentSha256,
                run.ArtifactPath,
                run.SpanLaneStatus,
                classification,
                checkpointPaths?.Length ?? 0,
                resolvedCheckpointBlocks,
                validCompletedSpans,
                hasPartialTimeout && validCompletedSpans > 0,
                materialDownstreamLoss,
                !hasPartialTimeout,
                hasPartialTimeout && validCompletedSpans == 0
                    ? "No C1.6-equivalent validator replay proves a usable completed span."
                    : null);
        }).OrderBy(finding => finding.Document, StringComparer.Ordinal).ThenBy(finding => finding.ArtifactPath, StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<RunEvidence> ReadHierarchyRuns(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) yield break;
        if (!root.TryGetProperty("artifactKind", out var kind) || kind.GetString() != "pdf_hierarchy_facts" ||
            !root.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var row in rows.EnumerateArray())
        {
            if (!row.TryGetProperty("file", out var file) || !row.TryGetProperty("spanLaneStatus", out var status)) continue;
            var document = DocumentKey(file.GetString() ?? string.Empty);
            if (string.IsNullOrEmpty(document)) continue;
            yield return new RunEvidence(document,
                row.TryGetProperty("sourceDocumentSha256", out var sha) ? sha.GetString() : null,
                path, status.GetString() ?? "unknown");
        }
    }

    private static C16Evidence? ReadC16(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("artifactKind", out var kind) || kind.GetString() != "c16_span_validation_and_throughput_audit" ||
            !root.TryGetProperty("document", out var document) || !root.TryGetProperty("laneA", out var laneA) ||
            !laneA.TryGetProperty("validatorSpanStatus", out var statuses) || !statuses.TryGetProperty("valid", out var valid)) return null;
        return new C16Evidence(DocumentKey(document.GetString() ?? string.Empty), valid.GetInt32());
    }

    private static C17Evidence? ReadC17(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("artifactKind", out var kind) || kind.GetString() != "c17_partial_span_preservation_counterfactual" ||
            !root.TryGetProperty("partialPreserve", out var preserve) ||
            !preserve.TryGetProperty("emittedDecisionRelevantOccurrences", out var emitted)) return null;
        return new C17Evidence(DocumentKeyFromPath(path), emitted.GetInt32());
    }

    private static int CountResolvedSpanBlocks(string path)
    {
        var count = 0;
        foreach (var line in File.ReadLines(path))
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            if (!root.TryGetProperty("lane", out var lane) || lane.GetString() != "span" ||
                !root.TryGetProperty("payload", out var payload) || !payload.TryGetProperty("blocks", out var blocks)) continue;
            count += blocks.EnumerateArray().Count(block => block.TryGetProperty("resolved", out var resolved) && resolved.GetBoolean());
        }
        return count;
    }

    private static string DocumentKeyFromPath(string path) => DocumentKey(Path.GetFileName(path));
    private static string DocumentKey(string text) => Regex.Match(text, @"(?<!\d)(?<id>\d{3})(?!\d)").Groups["id"].Value;

    private static void WriteHierarchy(string root, string document, string status) => WriteJson(
        Path.Combine(root, document + "-instrumented.json"), new
        {
            artifactKind = "pdf_hierarchy_facts",
            rows = new[] { new { file = document + "_doc.docx", sourceDocumentSha256 = "sha-" + document, spanLaneStatus = status } },
        });
    private static void WriteC16(string root, string document, int valid) => WriteJson(
        Path.Combine(root, document + "-c16-span-audit.json"), new
        {
            artifactKind = "c16_span_validation_and_throughput_audit",
            document,
            laneA = new { validatorSpanStatus = new { valid } },
        });
    private static void WriteC17(string root, string document, int emitted) => WriteJson(
        Path.Combine(root, document + "-c17-partial-preserve.json"), new
        {
            artifactKind = "c17_partial_span_preservation_counterfactual",
            partialPreserve = new { emittedDecisionRelevantOccurrences = emitted },
        });
    private static void WriteJson(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value));

    private sealed record RunEvidence(string Document, string? SourceDocumentSha256, string ArtifactPath, string SpanLaneStatus);
    private sealed record C16Evidence(string Document, int ValidCompletedSpans);
    private sealed record C17Evidence(string Document, int EmittedReviewedOccurrences);
    private sealed record Finding(string Document, string? SourceDocumentSha256, string ArtifactPath, string SpanLaneStatus,
        string Classification, int SpanCheckpointFiles, int ResolvedCheckpointBlocks, int ValidCompletedSpans,
        bool WrapperDiscardProven, int MaterialDownstreamLoss, bool PreserveIsNoOp, string? EvidenceLimitation);
}
