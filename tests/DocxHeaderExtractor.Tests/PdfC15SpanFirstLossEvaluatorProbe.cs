using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Offline-only C1.5 evaluator. It reads the frozen source-identity preflight, the one run's JSONL
/// checkpoint, and its route artifact. It never reconstructs candidates or calls a model.
/// </summary>
public sealed class PdfC15SpanFirstLossEvaluatorProbe
{
    [Fact]
    public void EvaluateFrozenInstrumentedReplication()
    {
        var output = Environment.GetEnvironmentVariable("C15_EVALUATION_REPORT");
        if (string.IsNullOrWhiteSpace(output)) return;
        var preflightPath = Required("C15_PREFLIGHT");
        var checkpointPath = Required("C15_CHECKPOINT");
        var artifactPath = Required("C15_ARTIFACT");
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        var preflight = JsonSerializer.Deserialize<Preflight>(File.ReadAllText(preflightPath), jsonOptions)
            ?? throw new InvalidOperationException("C1.5 preflight JSON could not be read.");
        var entries = File.ReadLines(checkpointPath)
            .Select(line => JsonSerializer.Deserialize<CheckpointEntry>(line, jsonOptions))
            .Where(entry => entry is not null)
            .Cast<CheckpointEntry>()
            .ToArray();
        using var artifact = JsonDocument.Parse(File.ReadAllText(artifactPath));
        var row = artifact.RootElement.GetProperty("rows")[0];
        var spanLaneStatus = row.GetProperty("spanLaneStatus").GetString() ?? "unknown";
        var validated = row.GetProperty("counters").GetProperty("validatedHeadings").GetInt32();

        var semantic = entries.Where(entry => entry.Lane == "semantic").SelectMany(entry => entry.Payload.Blocks).ToArray();
        var spans = entries.Where(entry => entry.Lane == "span")
            .SelectMany(entry => entry.Payload.Blocks.Select(block => (Block: block, entry.Payload.FailureClass))).ToArray();
        var outcomes = preflight.Occurrences.Select(occurrence => Classify(occurrence, semantic, spans, spanLaneStatus)).ToArray();

        Assert.Equal(preflight.Denominator.DecisionRelevantOccurrences, outcomes.Length);
        Assert.Equal(outcomes.Length, outcomes.Sum(item => 1));
        Assert.All(outcomes, item => Assert.Contains(item.FirstLoss, PdfC15SpanLanePreflightProbe.FirstLossTaxonomy));

        var report = new
        {
            artifactKind = "c15_span_lane_first_loss_evaluation",
            usesModel = false,
            sourceDocumentSha256 = preflight.SourceDocumentSha256,
            denominator = outcomes.Length,
            spanLaneStatus,
            validatedHeadings = validated,
            taxonomy = PdfC15SpanLanePreflightProbe.FirstLossTaxonomy,
            totals = outcomes.GroupBy(item => item.FirstLoss).OrderBy(group => group.Key)
                .ToDictionary(group => group.Key, group => group.Count()),
            items = outcomes,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static Outcome Classify(PreflightOccurrence occurrence, IReadOnlyList<CheckpointBlock> semantic,
        IReadOnlyList<(CheckpointBlock Block, string? FailureClass)> spans, string spanLaneStatus)
    {
        var roles = semantic.Where(block => Covers(block.LineIds, occurrence.RequiredLineIds)).ToArray();
        if (roles.Length == 0) return new Outcome(occurrence.GoldStableId, occurrence.GoldText, "ROLE_NO_DECISION", [], []);
        if (!roles.Any(block => block.Role == "HeadingTopic"))
            return new Outcome(occurrence.GoldStableId, occurrence.GoldText, "ROLE_NON_HEADING", roles.Select(block => block.Id).ToArray(), []);
        if (spanLaneStatus == "not_run")
            return new Outcome(occurrence.GoldStableId, occurrence.GoldText, "SPAN_NOT_RUN", roles.Select(block => block.Id).ToArray(), []);

        var spanMatches = spans.Where(item => Covers(item.Block.LineIds, occurrence.RequiredLineIds)).ToArray();
        if (spanMatches.Any(item => !string.IsNullOrWhiteSpace(item.FailureClass)))
            return new Outcome(occurrence.GoldStableId, occurrence.GoldText, "SPAN_BATCH_EXCEPTION", roles.Select(block => block.Id).ToArray(), spanMatches.Select(item => item.Block.Id).ToArray());
        if (spanMatches.Length == 0 && spanLaneStatus == "partial_timeout")
            return new Outcome(occurrence.GoldStableId, occurrence.GoldText, "SPAN_TIMEOUT", roles.Select(block => block.Id).ToArray(), []);
        if (!spanMatches.Any(item => item.Block.Resolved))
            return new Outcome(occurrence.GoldStableId, occurrence.GoldText, "SPAN_UNRESOLVED", roles.Select(block => block.Id).ToArray(), spanMatches.Select(item => item.Block.Id).ToArray());

        // This run's authoritative route artifact emitted no validated structures. A pointer existed,
        // therefore the first loss is downstream pointer validity/validation, not a span timeout.
        return new Outcome(occurrence.GoldStableId, occurrence.GoldText, "SPAN_RESOLVED_BUT_INVALID",
            roles.Select(block => block.Id).ToArray(), spanMatches.Select(item => item.Block.Id).ToArray());
    }

    private static bool Covers(IReadOnlyList<string>? carrier, IReadOnlyList<string> required) =>
        carrier is not null && required.All(line => carrier.Contains(line, StringComparer.Ordinal));

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Missing required environment variable {name}.");

    private sealed record Preflight(string SourceDocumentSha256, PreflightDenominator Denominator,
        IReadOnlyList<PreflightOccurrence> Occurrences);
    private sealed record PreflightDenominator(int DecisionRelevantOccurrences);
    private sealed record PreflightOccurrence(string GoldStableId, string GoldText, IReadOnlyList<string> RequiredLineIds);
    private sealed record CheckpointEntry(string Lane, CheckpointPayload Payload);
    private sealed record CheckpointPayload(string? FailureClass, IReadOnlyList<CheckpointBlock> Blocks);
    private sealed record CheckpointBlock(string Id, string? Role, IReadOnlyList<string>? LineIds, bool Resolved);
    private sealed record Outcome(string GoldStableId, string GoldText, string FirstLoss,
        IReadOnlyList<string> DebugRoleCandidateIds, IReadOnlyList<string> DebugSpanCandidateIds);
}
