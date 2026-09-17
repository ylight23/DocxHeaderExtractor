using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Replays only the deterministic post-inference stages over the already-frozen parsed segment
/// responses. It never calls a provider, reads Gold, or changes the provider prediction.
/// </summary>
public static class CanonicalDevVNextCorrectnessPredictionForensicRunner
{
    private const string PreflightRoot = "artifacts/level-accuracy/canonical-vnext-correctness-doc0116-live-preflight-v2-1";
    private const string ExecutionRoot = "artifacts/level-accuracy/canonical-vnext-correctness-doc0116-live-execution-v2-1";
    private const string OutputRoot = "artifacts/level-accuracy/canonical-vnext-correctness-doc0116-live-execution-v2-1/prediction-forensic-v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var execution = Path.Combine(repoRoot, ExecutionRoot.Replace('/', Path.DirectorySeparatorChar));
        var preflight = Path.Combine(repoRoot, PreflightRoot.Replace('/', Path.DirectorySeparatorChar));
        var freezePath = Path.Combine(execution, "prediction-freeze.v1.json");
        var completePath = Path.Combine(execution, "execution-complete.v1.json");
        var planPath = Path.Combine(preflight, "request-plan.v1.json");
        var requestFreezePath = Path.Combine(preflight, "request-freeze-manifest.v1.json");
        if (!File.Exists(freezePath) || !File.Exists(completePath) || !File.Exists(planPath) || !File.Exists(requestFreezePath))
            return Blocked("PREDICTION_FREEZE_OR_PLAN_MISSING");

        using var complete = JsonDocument.Parse(await File.ReadAllTextAsync(completePath, ct));
        if (!string.Equals(complete.RootElement.GetProperty("status").GetString(),
                "PREDICTION_FROZEN_BEFORE_GOLD", StringComparison.Ordinal) ||
            complete.RootElement.GetProperty("goldReads").GetInt32() != 0 ||
            complete.RootElement.GetProperty("providerCalls").GetInt32() != 7)
            return Blocked("PREDICTION_FREEZE_NOT_ELIGIBLE_FOR_FORENSIC");

        using var freeze = JsonDocument.Parse(await File.ReadAllTextAsync(freezePath, ct));
        using var plan = JsonDocument.Parse(await File.ReadAllTextAsync(planPath, ct));
        using var requestFreeze = JsonDocument.Parse(await File.ReadAllTextAsync(requestFreezePath, ct));
        var requestRows = plan.RootElement.GetProperty("requests").EnumerateArray().ToArray();
        var rawProposals = new List<CanonicalSemanticProposal>();
        var segmentRows = new List<object>();
        foreach (var request in requestRows.OrderBy(item => item.GetProperty("requestOrdinal").GetInt32()))
        {
            var ordinal = request.GetProperty("requestOrdinal").GetInt32();
            var parsedPath = Path.Combine(execution, $"segment-{ordinal:000}.parsed.v1.json");
            var attemptPath = Path.Combine(execution, $"segment-{ordinal:000}.attempt.v1.json");
            if (!File.Exists(parsedPath) || !File.Exists(attemptPath))
                return Blocked($"PARSED_SEGMENT_MISSING:{ordinal}");
            using var parsed = JsonDocument.Parse(await File.ReadAllTextAsync(parsedPath, ct));
            using var attempt = JsonDocument.Parse(await File.ReadAllTextAsync(attemptPath, ct));
            var rawResponse = attempt.RootElement.GetProperty("rawResponse").GetString() ??
                throw new InvalidDataException($"RAW_RESPONSE_MISSING:{ordinal}");
            var parsedResponse = SemanticTextExactBindingContract.Parse(rawResponse);
            var proposals = parsedResponse.Headings.Select(item => new CanonicalSemanticProposal(
                item.Source,
                true,
                item.Text,
                SemanticRole: item.Role,
                Occurrence: item.Occurrence,
                LeftExactContext: item.LeftExactContext,
                RightExactContext: item.RightExactContext)).ToArray();
            rawProposals.AddRange(proposals);
            segmentRows.Add(new
            {
                requestOrdinal = ordinal,
                proposalCount = proposals.Length,
                parsedArtifactSha256 = Sha256File(parsedPath),
            });
        }

        var sourceUniverseSha = requestFreeze.RootElement.GetProperty("sourceUniverseSha256").GetString() ??
            throw new InvalidDataException("FROZEN_SOURCE_UNIVERSE_SHA_MISSING");
        var fullInput = await CanonicalDevVNextCorrectnessProviderExecutionRunner.BuildFullInputAsync(
            repoRoot, sourceUniverseSha, ct);
        var sourceSha = fullInput.SourceSha256;
        var replay = CanonicalSemanticProductionEntryPoint.Run(
            fullInput with { SemanticProposals = rawProposals });
        var normalization = replay.ConflictNormalization;
        var statusCounts = replay.TextPipeline.BindingObservations
            .GroupBy(item => item.Status.ToString(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var hardConflictProposalCount = normalization.Conflicts.Sum(item => item.Alternatives.Count);
        var attributeAlternativeCount = normalization.AttributeConflicts.Sum(item => item.Alternatives.Count);
        var freezeGraphCount = freeze.RootElement.GetProperty("canonicalGraphOccurrenceCount").GetInt32();
        var output = new
        {
            schemaVersion = "a99-canonical-vnext-correctness-prediction-forensic-v1",
            documentId = "DOC-0116",
            executionRoot = ExecutionRoot,
            predictionFreezeSha256 = Sha256File(freezePath),
            executionCompleteSha256 = Sha256File(completePath),
            providerCalls = 0,
            modelCalls = 0,
            goldReads = 0,
            scoring = false,
            segmentCount = requestRows.Length,
            segments = segmentRows,
            rawProposals = rawProposals.Count,
            exactSemanticDuplicatesCollapsed = normalization.ExactSemanticDuplicatesCollapsed,
            hardConflictProposalsWithheld = hardConflictProposalCount,
            hardConflictGroupsWithheld = normalization.Conflicts.Count,
            attributeConflictAlternatives = attributeAlternativeCount,
            attributeConflictGroups = normalization.AttributeConflicts.Count,
            normalizedProposals = normalization.SemanticProposalNormalizedCount,
            bindingReadyProposals = normalization.BindingReadyProposals.Count,
            bindingObservationStatusCounts = statusCounts,
            boundHeadings = replay.TextPipeline.BoundHeadings.Count,
            canonicalGraphOccurrencesReplay = replay.CanonicalOccurrences.Count,
            canonicalGraphOccurrencesFrozen = freezeGraphCount,
            replayMatchesFrozenGraph = replay.CanonicalOccurrences.Count == freezeGraphCount,
            equation = new
            {
                rawProposals = rawProposals.Count,
                exactSemanticDuplicatesCollapsed = normalization.ExactSemanticDuplicatesCollapsed,
                hardConflictProposalsWithheld = hardConflictProposalCount,
                attributeConflictGroups = normalization.AttributeConflicts.Count,
                normalizedProposals = normalization.SemanticProposalNormalizedCount,
                bindingReadyProposals = normalization.BindingReadyProposals.Count,
                boundHeadings = replay.TextPipeline.BoundHeadings.Count,
                canonicalGraphOccurrences = replay.CanonicalOccurrences.Count,
            },
            normalizationConflictDetails = normalization.Conflicts.Select(item => new
            {
                physicalSourceIdentity = item.PhysicalSourceIdentity,
                alternativeCount = item.Alternatives.Count,
                classification = item.Classification,
            }).ToArray(),
            attributeConflictDetails = normalization.AttributeConflicts.Select(item => new
            {
                physicalSourceIdentity = item.PhysicalSourceIdentity,
                alternativeCount = item.Alternatives.Count,
                contestedFields = item.ContestedFields,
            }).ToArray(),
        };

        Directory.CreateDirectory(Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar)));
        var outputPath = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar), "prediction-forensic.v1.json");
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(output, JsonOptions) + Environment.NewLine, ct);
        var report = string.Join(Environment.NewLine, new[]
        {
            "# DOC-0116 prediction forensic",
            "",
            "Provider/model calls: 0  ",
            "Gold reads: 0  ",
            "Scoring: false  ",
            "",
            $"Raw proposals: {rawProposals.Count}  ",
            $"Exact semantic duplicates collapsed: {normalization.ExactSemanticDuplicatesCollapsed}  ",
            $"Hard-conflict proposals withheld: {hardConflictProposalCount}  ",
            $"Attribute-conflict groups: {normalization.AttributeConflicts.Count}  ",
            $"Binding-ready proposals: {normalization.BindingReadyProposals.Count}  ",
            $"Bound headings: {replay.TextPipeline.BoundHeadings.Count}  ",
            $"Canonical graph occurrences (replay): {replay.CanonicalOccurrences.Count}  ",
            $"Canonical graph occurrences (frozen): {freezeGraphCount}  ",
            $"Replay matches frozen graph: {replay.CanonicalOccurrences.Count == freezeGraphCount}  ",
            "",
            "Binding statuses:",
            "",
            string.Join(Environment.NewLine, statusCounts.OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => $"- `{item.Key}`: {item.Value}")),
            "",
            "This is a deterministic replay of frozen parsed responses only. It is not a new prediction and does not read Gold.",
        });
        await File.WriteAllTextAsync(Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar), "report.md"), report, ct);
        await File.WriteAllTextAsync(Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar), "manifest.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = "a99-canonical-vnext-correctness-prediction-forensic-manifest-v1",
            predictionForensicSha256 = Sha256File(outputPath),
            executionRoot = ExecutionRoot,
            sourceSha256 = sourceSha,
            providerCalls = 0,
            goldReads = 0,
        }, JsonOptions) + Environment.NewLine, ct);
        Console.WriteLine("STATUS=PREDICTION_FORENSIC_COMPLETE");
        Console.WriteLine($"RAW_PROPOSALS={rawProposals.Count}");
        Console.WriteLine($"BINDING_READY={normalization.BindingReadyProposals.Count}");
        Console.WriteLine($"BOUND_HEADINGS={replay.TextPipeline.BoundHeadings.Count}");
        Console.WriteLine($"GRAPH_OCCURRENCES={replay.CanonicalOccurrences.Count}");
        Console.WriteLine("PROVIDER_CALLS=0");
        Console.WriteLine("GOLD_READS=0");
        return 0;
    }

    private static int Blocked(string reason)
    {
        Console.WriteLine($"STATUS=BLOCKED:{reason}");
        Console.WriteLine("PROVIDER_CALLS=0");
        Console.WriteLine("GOLD_READS=0");
        return 2;
    }

    private static string Sha256File(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
