using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Policy;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Explicit provider entrypoint for the frozen DOC-0116 segmented correctness plan. This is
/// intentionally separate from the offline preflight command. It consumes the frozen plan and
/// reconstructs only the full post-segment binding input; the executor consumes the materialized
/// segment requests and performs the runtime hash checks before the first provider call.
/// </summary>
public static class CanonicalDevVNextCorrectnessProviderExecutionRunner
{
    private const string PreflightRoot = "artifacts/level-accuracy/canonical-vnext-correctness-doc0116-live-preflight-v2-1";
    private const string ExecutionRoot = "artifacts/level-accuracy/canonical-vnext-correctness-doc0116-live-execution-v2-1";
    private const string SourcePreflightRoot = "artifacts/level-accuracy/canonical-vnext-correctness-doc0116-preflight";
    private const string DocumentId = "DOC-0116";

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var preflight = Path.Combine(repoRoot, PreflightRoot.Replace('/', Path.DirectorySeparatorChar));
        var execution = Path.Combine(repoRoot, ExecutionRoot.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(execution) && Directory.EnumerateFileSystemEntries(execution).Any())
            return Blocked("PROVIDER_EXECUTION_OUTPUT_ALREADY_EXISTS");

        var freezePath = Path.Combine(preflight, "request-freeze-manifest.v1.json");
        if (!File.Exists(freezePath))
            return Blocked("V2_1_PREFLIGHT_MISSING");
        using var freeze = JsonDocument.Parse(await File.ReadAllTextAsync(freezePath, ct));
        var root = freeze.RootElement;
        if (!string.Equals(root.GetProperty("status").GetString(),
                "READY_FOR_DOC0116_PROVIDER_EXECUTION_V2_1", StringComparison.Ordinal))
            return Blocked("V2_1_PREFLIGHT_NOT_AUTHORITY");
        if (root.GetProperty("providerCalls").GetInt32() != 0 ||
            root.GetProperty("modelCalls").GetInt32() != 0 ||
            root.GetProperty("goldReads").GetInt32() != 0)
            return Blocked("V2_1_PREFLIGHT_ALREADY_USED");

        var remote = RemoteInferenceOptions.FromEnvironment("openrouter");
        remote.Model = root.GetProperty("model").GetString()!;
        remote.Endpoint = new Uri(root.GetProperty("endpoint").GetString()!);
        remote.ContextSize = 1_000_000;
        remote.MaxOutputTokens = root.GetInt32OrDefault("requestPlanMaxOutputTokens", 48_000);
        remote.RequestTimeoutSeconds = root.GetProperty("requestTimeoutSeconds").GetInt32();
        remote.TransientRequestRetries = 0;
        remote.MissingIdRetries = 0;
        remote.MaxParallelRequests = 1;
        remote.OpenRouterProviderRoute = null;
        remote.OpenRouterAllowNonZdrPublicBenchmark = true;
        if (string.IsNullOrWhiteSpace(remote.ApiKey))
            return Blocked("OPENROUTER_API_KEY_MISSING");

        // V2.1 freezes the enabled/excluded reasoning wire shape. The capability is deliberately
        // pinned to the no-effort-list representation so the runtime body matches preflight.
        var capability = new OpenRouterModelCapability
        {
            ModelId = remote.Model,
            ContextLength = 1_000_000,
            ReasoningSupported = true,
            SelectedReasoningEffort = "enabled",
            ReasoningEnabled = true,
            EffortListReported = false,
            StructuredOutputSupported = true,
            MaxCompletionTokens = remote.MaxOutputTokens,
        };
        var fullInput = await BuildFullInputAsync(
            repoRoot,
            root.GetProperty("sourceUniverseSha256").GetString()!,
            ct);
        using var model = new OpenRouterCeilingReasoningModel(
            remote,
            capability,
            attemptDeadline: TimeSpan.FromSeconds(root.GetProperty("perAttemptHardTimeoutSeconds").GetInt32()));
        // Validate the frozen plan and runtime configuration before creating any execution
        // artifact. A local mismatch must remain retryable without an output-directory tombstone.
        await CanonicalDevVNextCorrectnessSegmentedExecutor.ValidateFrozenPlanAsync(preflight, ct, model);
        Directory.CreateDirectory(execution);
        await File.WriteAllTextAsync(Path.Combine(execution, "execution-start.v1.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = "a99-canonical-vnext-correctness-provider-execution-v2-1",
            status = "PROVIDER_EXECUTION_STARTED",
            preflightRoot = PreflightRoot,
            executionRoot = ExecutionRoot,
            requestFreezeSha256 = Sha256File(freezePath),
            model = remote.Model,
            endpoint = remote.Endpoint.ToString(),
            providerCalls = 0,
            goldReads = 0,
            scoring = false,
        }) + Environment.NewLine, ct);

        var prediction = await CanonicalDevVNextCorrectnessSegmentedExecutor.ExecuteAsync(
            preflight, execution, fullInput, model, ct);
        await File.WriteAllTextAsync(Path.Combine(execution, "execution-complete.v1.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = "a99-canonical-vnext-correctness-provider-execution-v2-1",
            status = "PREDICTION_FROZEN_BEFORE_GOLD",
            providerCalls = model.ProviderCalls,
            goldReads = 0,
            scoring = false,
            canonicalOccurrenceCount = prediction.CanonicalOccurrences.Count,
        }) + Environment.NewLine, ct);
        Console.WriteLine("STATUS=PREDICTION_FROZEN_BEFORE_GOLD");
        Console.WriteLine($"PROVIDER_CALLS={model.ProviderCalls}");
        Console.WriteLine("GOLD_READS=0");
        return 0;
    }

    private static async Task<CanonicalSemanticProductionInput> BuildFullInputAsync(
        string repoRoot, string expectedSourceUniverseSha, CancellationToken ct)
    {
        var sourceUniversePath = Path.Combine(repoRoot, SourcePreflightRoot.Replace('/', Path.DirectorySeparatorChar), "source-universe.v1.json");
        if (!File.Exists(sourceUniversePath))
            throw new InvalidDataException("SOURCE_UNIVERSE_MISSING");
        var actualSourceUniverseSha = Sha256File(sourceUniversePath);
        if (!string.Equals(actualSourceUniverseSha, expectedSourceUniverseSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("FROZEN_SOURCE_UNIVERSE_HASH_MISMATCH");
        using var sourceUniverse = JsonDocument.Parse(await File.ReadAllTextAsync(sourceUniversePath, ct));
        var sourceRoot = sourceUniverse.RootElement;
        var sourcePath = Path.Combine(repoRoot, sourceRoot.GetProperty("sourcePath").GetString()!
            .Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
        var expectedSourceSha = sourceRoot.GetProperty("sourceSha256").GetString()!;
        var actualSourceSha = Sha256File(sourcePath);
        if (!string.Equals(actualSourceSha, expectedSourceSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("FROZEN_SOURCE_DOCUMENT_HASH_MISMATCH");
        var frozenAliases = sourceRoot.GetProperty("sourceIdentity").EnumerateArray()
            .Select(item => new FrozenAlias(
                item.GetProperty("alias").GetString()!,
                item.GetProperty("sourceId").GetString()!,
                item.GetProperty("sourceOrdinal").GetInt32(),
                item.GetProperty("text").GetString()!))
            .ToArray();
        if (frozenAliases.Length != 1_921)
            throw new InvalidDataException("FROZEN_SOURCE_UNIVERSE_CARDINALITY_MISMATCH");

        var source = new OpenXmlDocumentSource().Read(sourcePath) with { DocumentId = DocumentId };
        var prepared = await VisualSourceEvidenceBuilder.BuildAsync(sourcePath, int.MaxValue, ct);
        var currentAliases = SemanticSourceAliasCatalog.FromCatalog(prepared.Catalog);
        if (currentAliases.Count != frozenAliases.Length ||
            currentAliases.Zip(frozenAliases).Any(pair =>
                !string.Equals(pair.First.Alias, pair.Second.Alias, StringComparison.Ordinal) ||
                !string.Equals(pair.First.SourceId, pair.Second.SourceId, StringComparison.Ordinal) ||
                pair.First.SourceOrdinal != pair.Second.SourceOrdinal ||
                !string.Equals(pair.First.Text, pair.Second.Text, StringComparison.Ordinal)))
            throw new InvalidDataException("FROZEN_SOURCE_ALIAS_IDENTITY_MISMATCH");

        var features = NumberingStyleFeatures.FromSourceDocument(source);
        var derived = new DocumentFeatureDeriver().Derive(source);
        var policy = DocxPolicyStateBuilder.Build(source, features, derived,
            new PipelineOptions { DisableLlm = false }.Extraction);
        var evidence = CanonicalSemanticRichEvidenceBuilder.Build(source, prepared.Catalog, policy);
        if (evidence.Count != frozenAliases.Length ||
            evidence.Any(item => !currentAliases.Any(alias =>
                string.Equals(alias.Alias, item.SourceAlias, StringComparison.Ordinal) &&
                string.Equals(alias.SourceId, item.SourceId, StringComparison.Ordinal) &&
                alias.SourceOrdinal == item.SourceOrdinal &&
                string.Equals(alias.Text, item.ExactSourceText, StringComparison.Ordinal))))
            throw new InvalidDataException("FROZEN_SOURCE_EVIDENCE_IDENTITY_MISMATCH");
        var candidateHints = evidence.Select(item => item.CandidateAttention).ToArray();
        var targetEvidence = evidence
            .Select(item => $"{item.SourceAlias}|ordinal:{item.SourceOrdinal}|scope:{item.StructuralScope}")
            .ToArray();
        var localContext = evidence.SelectMany(item => item.LocalBefore.Concat(item.LocalAfter)
            .Select(context => $"{item.SourceAlias}|{context}")).ToArray();
        var globalContext = new[]
        {
            $"documentId:{DocumentId}",
            $"sourceKind:{source.SourceKind}",
            $"sourceParagraphCount:{source.Paragraphs.Count}",
            $"canonicalOccurrenceCount:{evidence.Count}",
            "candidateGating:false",
            "contextPolicy:FULL_SOURCE_UNIVERSE_RICH_EVIDENCE_V1",
        };
        return new CanonicalSemanticProductionInput(
            prepared.Catalog, null, expectedSourceSha, prepared.Pages, candidateHints,
            targetEvidence, localContext, globalContext,
            VisualPages: prepared.VisualPages,
            ExpectedSourceSha256: expectedSourceSha,
            DocumentId: DocumentId,
            SourceEvidence: evidence);
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

    private static int GetInt32OrDefault(this JsonElement element, string property, int fallback) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : fallback;

    private sealed record FrozenAlias(string Alias, string SourceId, int SourceOrdinal, string Text);
}
