using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.V5Qualification;

/// <summary>
/// P5F2 is the post-P5G, four-attempt wire/contract canary. It can only use the exact P5H
/// manifest bodies; it never opens the historical 31-pack lane or reads/scoring Gold.
/// </summary>
internal static class P5F2V3Canary
{
    public const string ConfirmSentinel = "yes-i-authorize-p5f2-v3-canary-4-p5h-calls";
    private const string ArtifactRoot = "artifacts/v5-p5f2-v3-canary";
    private const string ManifestPath = "artifacts/v5-p5h-v3-manifest-refresh/execution-manifest.v1.json";
    private const string Src089 = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";
    private const string Src095 = "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf";

    private sealed record SelectionSpec(string Role, string DocumentId, string PackId, string Focus);

    private static readonly SelectionSpec[] Frozen =
    [
        new("MAX_OWNED_AND_MULTIPART", "SRC-089", "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_001",
            "96 positional decisions; historical multi-atom subject evidence"),
        new("L1472_OWNER_OMISSION", "SRC-095", "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_017",
            "historical L1472 owner omission"),
        new("L1710_RETYPING", "SRC-095", "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_020",
            "historical L1710 whole-atom retyping"),
        new("MULTIPART_RELATION", "SRC-095", "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_011",
            "historical multi-atom relation endpoint"),
    ];

    public static async Task<int> RunAsync(string root, string[] args)
    {
        var authorized = args.Any(arg => arg == $"--confirm-p5f2-v3-canary={ConfirmSentinel}");
        if (Frozen.Length != P5F2CanaryGate.CanaryRequestCount)
            throw new InvalidOperationException("p5f2-frozen-selection-must-contain-exactly-4-calls");

        var manifestPath = Path.Combine(root, ManifestPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(manifestPath)) return Fail($"p5f2: missing frozen manifest {ManifestPath}");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath)) ?? throw new InvalidOperationException("p5f2-manifest-empty");
        if (manifest["status"]?.GetValue<string>() != "PREPARED_NOT_AUTHORIZED" ||
            manifest["protocol"]?.GetValue<string>() != V5SemanticDecisionContractV3.SchemaVersion ||
            manifest["requestCount"]?.GetValue<int>() != P5F2CanaryGate.CanaryRequestCount ||
            manifest["executionGate"]?["freshExplicitAuthorizationRequired"]?.GetValue<bool>() != true ||
            manifest["executionGate"]?["maximumProviderCallsIfAuthorized"]?.GetValue<int>() != P5F2CanaryGate.CanaryRequestCount ||
            manifest["executionGate"]?["full31PackCohortAuthorized"]?.GetValue<bool>() != false)
            return Fail("p5f2: P5H manifest gate is not the approved four-pack provider canary shape");

        var contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        var envelope = new V5ProviderEnvelope("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300) { UsageInclude = true };
        var builds = new Dictionary<string, IReadOnlyList<V5PackedDecisionRequestV3>>(StringComparer.Ordinal);
        IReadOnlyList<V5PackedDecisionRequestV3> BuildFor(string documentId)
        {
            if (!builds.TryGetValue(documentId, out var built))
            {
                var pdf = Path.Combine(root, (documentId == "SRC-089" ? Src089 : Src095).Replace('/', Path.DirectorySeparatorChar));
                built = V5PdfPreflightBuilder.BuildV3(pdf, documentId, contract,
                    V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, envelope);
                builds[documentId] = built;
            }
            return built;
        }

        var resolved = new List<(SelectionSpec Spec, V5PackedDecisionRequestV3 Pack, string PdfPath, JsonNode ManifestRow)>();
        foreach (var spec in Frozen)
        {
            var row = manifest["rows"]!.AsArray().SingleOrDefault(node =>
                node?["role"]?.GetValue<string>() == spec.Role &&
                node["documentId"]?.GetValue<string>() == spec.DocumentId &&
                node["packId"]?.GetValue<string>() == spec.PackId)
                ?? throw new InvalidOperationException($"p5f2-manifest-row-missing:{spec.Role}");
            var pack = BuildFor(spec.DocumentId).SingleOrDefault(item => item.PackId == spec.PackId)
                ?? throw new InvalidOperationException($"p5f2-rebuild-pack-missing:{spec.DocumentId}:{spec.PackId}");
            if (row["semanticRequestHash"]?.GetValue<string>() != pack.Request.RequestHash ||
                row["providerRequestHash"]?.GetValue<string>() != pack.ProviderRequestHash ||
                row["providerRequestBytes"]?.GetValue<int>() != pack.ProviderRequestBytes ||
                row["maxCompletionTokens"]?.GetValue<int>() != pack.MaxCompletionTokens ||
                row["decisionCountExpected"]?.GetValue<int>() != pack.OwnedAliases.Count ||
                pack.OwnedAliases.Count != 96 ||
                pack.Request.ResponseBounds.MaxClaimsTotal != 129 ||
                pack.Request.ResponseBounds.MaxResponseUtf8Bytes != 49152)
                return Fail($"p5f2: rebuilt body does not reproduce P5H manifest: {spec.Role}");

            var bodyPath = Path.Combine(Path.GetDirectoryName(manifestPath)!,
                row["providerBodyFile"]!.GetValue<string>().Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(bodyPath) || !File.ReadAllBytes(bodyPath).AsSpan().SequenceEqual(pack.ProviderBody))
                return Fail($"p5f2: rebuilt provider body differs from P5H frozen body: {spec.Role}");
            var pdfPath = Path.Combine(root, (spec.DocumentId == "SRC-089" ? Src089 : Src095).Replace('/', Path.DirectorySeparatorChar));
            resolved.Add((spec, pack, pdfPath, row));
        }

        var artifactDir = Path.Combine(root, ArtifactRoot.Replace('/', Path.DirectorySeparatorChar));
        var resultPath = Path.Combine(artifactDir, "result.v1.json");
        if (File.Exists(resultPath)) return Fail($"p5f2: result already exists; stop before network: {ArtifactRoot}/result.v1.json");
        Directory.CreateDirectory(artifactDir);
        WriteJson(Path.Combine(artifactDir, "selection.v1.json"), new
        {
            schemaVersion = "v5-p5f2-v3-selection-v1",
            sourceManifest = ManifestPath,
            providerCalls = 0,
            goldRead = false,
            semanticScore = "NOT_RUN",
            selectedPacks = resolved.Select(item => new
            {
                item.Spec.Role, item.Spec.Focus, item.Spec.DocumentId, item.Spec.PackId,
                semanticRequestHash = item.Pack.Request.RequestHash,
                providerRequestHash = item.Pack.ProviderRequestHash,
                maxCompletionTokens = item.Pack.MaxCompletionTokens,
                decisionCountExpected = item.Pack.OwnedAliases.Count,
                maxClaimsTotal = item.Pack.Request.ResponseBounds.MaxClaimsTotal,
                maxResponseUtf8Bytes = item.Pack.Request.ResponseBounds.MaxResponseUtf8Bytes,
            }),
        });

        Console.WriteLine("p5f2 v3 canary parity PASS: exactly four P5H bodies reproduced byte-for-byte.");
        foreach (var item in resolved)
            Console.WriteLine($"  [{item.Spec.Role}] {item.Spec.DocumentId} {item.Spec.PackId} hash={item.Pack.ProviderRequestHash}");
        if (!authorized)
        {
            Console.WriteLine($"Not authorized. providerCalls=0, goldRead=false. Pass --confirm-p5f2-v3-canary={ConfirmSentinel} to execute.");
            return 0;
        }

        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey)) return Fail("p5f2: OPENROUTER_API_KEY is not set. Refusing to run.");
        P5F2CanaryGate.Authorize(resolved.Count, authorized);

        var options = RemoteInferenceOptions.FromEnvironment();
        options.Model = envelope.Model;
        options.OpenRouterProviderRoute = envelope.Provider;
        options.OpenRouterReasoningEffort = envelope.Reasoning;
        options.TransientRequestRetries = 0;
        options.Validate();

        var results = new List<object>();
        foreach (var item in resolved)
        {
            var atoms = V5PdfPreflightBuilder.LoadAtoms(item.PdfPath);
            var scope = ClaimBindingScope.Create(item.Pack.OwnedAliases, item.Pack.VisibleAliases);
            OpenRouterExecutionObservation? observation = null;
            string? transportError = null;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var client = OpenRouterHeaderExtractor.CreateOwned(options);
                observation = await client.ExecuteObservedAsync(item.Pack.ProviderBody, item.Pack.MaxCompletionTokens,
                    V5SystemPromptV2_1.Text, item.Pack.Request.Prompt);
            }
            catch (Exception ex)
            {
                transportError = ex.Message;
            }
            stopwatch.Stop();
            var response = Analyze(observation?.Content, transportError, contract, item.Pack, atoms, scope);
            results.Add(new
            {
                role = item.Spec.Role,
                focus = item.Spec.Focus,
                documentId = item.Spec.DocumentId,
                packId = item.Spec.PackId,
                semanticRequestHash = item.Pack.Request.RequestHash,
                providerRequestHash = item.Pack.ProviderRequestHash,
                decisionCountExpected = item.Pack.OwnedAliases.Count,
                maxCompletionTokens = item.Pack.MaxCompletionTokens,
                transportError,
                finishReason = observation?.FinishReason,
                usage = observation?.Usage,
                retryCount = observation?.RetryCount ?? 0,
                latencyMs = stopwatch.Elapsed.TotalMilliseconds,
                sseEventCount = observation?.SseEventCount ?? 0,
                sseRawSha256 = observation is null ? null : Sha256(observation.RawSse),
                sseRaw = observation?.RawSse,
                rawResponseSha256 = observation is null ? null : Sha256(observation.Content),
                rawResponseBytes = observation is null ? 0 : Encoding.UTF8.GetByteCount(observation.Content),
                rawResponse = observation?.Content,
                response,
            });
            Console.WriteLine($"  -> [{item.Spec.Role}] {response.Classification} decisions={response.DecisionCountActual}/{item.Pack.OwnedAliases.Count} claims={response.ClaimsProduced} latencyMs={stopwatch.ElapsedMilliseconds}");
        }

        WriteJson(resultPath, new
        {
            schemaVersion = "v5-p5f2-v3-provider-canary-result-v1",
            sourceManifest = ManifestPath,
            protocolVersion = V5SemanticDecisionContractV3.SchemaVersion,
            model = envelope.Model,
            provider = envelope.Provider,
            reasoning = envelope.Reasoning,
            responseFormat = envelope.ResponseFormat,
            providerCalls = resolved.Count,
            goldRead = false,
            semanticScore = "NOT_RUN",
            semanticRetries = 0,
            responseRepairApplied = false,
            providerExecutionAuthorized = true,
            stopGate = "CLOSED_AFTER_4_CALLS",
            results,
        });
        Console.WriteLine($"p5f2: wrote {ArtifactRoot}/result.v1.json; gate closed after {resolved.Count} calls.");
        return 0;
    }

    private sealed record Analysis(
        string Classification, int? DecisionCountActual, bool ContractValid, bool ParserAccepted, bool BinderAccepted,
        int ClaimsProduced, int EmptyDecisionCount, int MultiAtomClaimCount, string MultiAtomValid,
        int RelationTargetClaimCount, string RelationTargetValid, int BoundCount, int BindingRefusalCount, string? Error);

    private static Analysis Analyze(string? raw, string? transportError, DocumentTaskContract contract,
        V5PackedDecisionRequestV3 pack, IReadOnlyList<SemanticSourceAtom> atoms, ClaimBindingScope scope)
    {
        if (transportError is not null || raw is null)
            return new("TRANSPORT_ERROR", null, false, false, false, 0, 0, 0, "NOT_OBSERVED", 0, "NOT_OBSERVED", 0, 0, transportError);
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            int? actual = root.TryGetProperty("decisions", out var decisions) && decisions.ValueKind == JsonValueKind.Array
                ? decisions.GetArrayLength() : null;
            V5SemanticDecisionResponseV3 response;
            try
            {
                response = V5SemanticDecisionContractV3.Parse(root, contract, pack.OwnedAliases.Count, pack.Packet.ContextOnlyEvidence.Count);
            }
            catch (Exception ex)
            {
                return new("CONTRACT_INVALID", actual, false, false, false, 0, 0, 0, "NOT_OBSERVED", 0, "NOT_OBSERVED", 0, 0, ex.Message);
            }

            var claims = response.Decisions.SelectMany(item => item.Claims).ToArray();
            var multi = claims.Where(item => (item.AdditionalSubjectParts?.Count ?? 0) > 0 || (item.TargetParts?.Count ?? 0) > 1).ToArray();
            var relations = claims.Where(item => contract.Relations.Any(relation => relation.Name == item.Predicate) && (item.TargetParts?.Count ?? 0) > 0).ToArray();
            var binding = V5SemanticDecisionContractV3.Bind(pack.Request.RequestHash, response, contract,
                pack.Packet.SubjectEvidence, pack.Packet.ContextOnlyEvidence, atoms, scope);
            var binderAccepted = binding.Binding is not null;
            return new(binding.Refusals.Count == 0 ? "BINDER_ACCEPTED" : binderAccepted ? "BINDER_PARTIAL" : "BINDER_REJECTED",
                actual, true, true, binderAccepted, claims.Length, response.Decisions.Count(item => item.Claims.Count == 0),
                multi.Length, multi.Length == 0 ? "NOT_OBSERVED" : binderAccepted ? "OBSERVED_AND_BINDER_ACCEPTED" : "OBSERVED_BUT_BINDER_REJECTED",
                relations.Length, relations.Length == 0 ? "NOT_OBSERVED" : binderAccepted ? "OBSERVED_AND_BINDER_ACCEPTED" : "OBSERVED_BUT_BINDER_REJECTED",
                binding.Bound.Count, binding.Refusals.Count, null);
        }
        catch (JsonException ex)
        {
            return new("JSON_INVALID", null, false, false, false, 0, 0, 0, "NOT_OBSERVED", 0, "NOT_OBSERVED", 0, 0, ex.Message);
        }
    }

    private static void WriteJson(string path, object value) =>
        File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
            new UTF8Encoding(false));

    private static string Sha256(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static int Fail(string message) { Console.Error.WriteLine(message); return 1; }
}

internal static class P5F2CanaryGate
{
    public const int CanaryRequestCount = 4;

    public static void Authorize(int requestCount, bool providerExecutionAuthorized)
    {
        if (requestCount != CanaryRequestCount)
            throw new InvalidOperationException($"p5f2-canary-request-count-must-be-exactly-{CanaryRequestCount}:{requestCount}");
        if (!providerExecutionAuthorized)
            throw new InvalidOperationException("p5f2-provider-execution-not-authorized");
    }
}
