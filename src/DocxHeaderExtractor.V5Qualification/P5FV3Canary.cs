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
/// P5F is deliberately a four-call transport/contract observation, not a semantic score or a
/// promotion run. The roles, manifest identities and response evidence are fixed here so this mode
/// cannot silently turn into a cohort execution.
/// </summary>
internal static class P5FV3Canary
{
    public const string ConfirmSentinel = "yes-i-authorize-p5f-v3-canary-4-frozen-calls";
    private const string ArtifactRoot = "artifacts/v5-p5f-v3-canary";
    private const string ManifestPath = "artifacts/v5-p5d-v3-preflight/execution-manifest.v1.json";
    private const string Src089 = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";
    private const string Src095 = "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf";

    private sealed record SelectionSpec(string Role, string DocumentId, string PackId, string SemanticRequestHash, string ProviderRequestHash, string Focus);

    // These are selections from the P5D manifest at 049b101. Each independently exercises an
    // actual 96-owned pack; PACK_001 also had historical multipart subjects, and PACK_011 had a
    // historical multipart relation endpoint. P17/P20 are the historical L1472/L1710 owner cases.
    private static readonly SelectionSpec[] Frozen =
    [
        new("MAX_OWNED_AND_MULTIPART", "SRC-089", "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_001",
            "dc7f9a8568cd1ef3ee697f6cbffcc9f788fe3a2c819889b0e4495ec72f6fa182", "4dbe2596e0735aacd91c7496ba920406ac74fae1a8a2c8ecbe176fed203beb02",
            "96 positional decisions; historical multi-atom subject evidence"),
        new("L1472_OWNER_OMISSION", "SRC-095", "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_017",
            "97d6294a177bfe699649fec4139b48bc5b0dca46d83e76e06d96fa37274c32cb", "d8b76ca64e66c3c5279b7c3b5219318c28e882d09997fce0ba0a13a0248df9e2",
            "historical L1472 owner omission"),
        new("L1710_RETYPING", "SRC-095", "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_020",
            "967b40fcf4b91f1cca97c54bb08cc5f737004d9b2a365ea7647790bd91d5d0e7", "224879112a7f3e5faa6fe2869f7c174f3bc795434e1fde1c0fb3fdf08954b467",
            "historical L1710 whole-atom retyping"),
        new("MULTIPART_RELATION", "SRC-095", "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_011",
            "4037e8985ba178c0dc0d1cead9b2f7ec548de33bb34ba90ebbf7d4620279ccd4", "a75e4c6650fa2876ea430bd27d4646d37731d35391df95b004ff394785005d93",
            "historical multi-atom relation endpoint"),
    ];

    public static async Task<int> RunAsync(string root, string[] args)
    {
        var authorized = args.Any(arg => arg == $"--confirm-p5f-v3-canary={ConfirmSentinel}");
        if (Frozen.Length != 4) throw new InvalidOperationException("p5f-frozen-selection-must-contain-exactly-4-calls");

        var manifestPath = Path.Combine(root, ManifestPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(manifestPath)) return Fail($"p5f: missing frozen manifest {ManifestPath}");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath)) ?? throw new InvalidOperationException("p5f-manifest-empty");
        if (manifest["protocol"]?.GetValue<string>() != V5SemanticDecisionContractV3.SchemaVersion)
            return Fail("p5f: frozen manifest is not v5-source-backed-decision-3.0");

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
                node?["documentId"]?.GetValue<string>() == spec.DocumentId && node["packId"]?.GetValue<string>() == spec.PackId)
                ?? throw new InvalidOperationException($"p5f-manifest-row-missing:{spec.DocumentId}:{spec.PackId}");
            if (row["semanticRequestHash"]?.GetValue<string>() != spec.SemanticRequestHash ||
                row["providerRequestHash"]?.GetValue<string>() != spec.ProviderRequestHash)
                return Fail($"p5f: static selection no longer matches frozen manifest: {spec.DocumentId}:{spec.PackId}");

            var pack = BuildFor(spec.DocumentId).SingleOrDefault(item => item.PackId == spec.PackId)
                ?? throw new InvalidOperationException($"p5f-rebuild-pack-missing:{spec.DocumentId}:{spec.PackId}");
            if (pack.Request.RequestHash != spec.SemanticRequestHash || pack.ProviderRequestHash != spec.ProviderRequestHash ||
                pack.MaxCompletionTokens != row["maxCompletionTokens"]!.GetValue<int>() || pack.OwnedAliases.Count != 96)
                return Fail($"p5f: rebuilt body does not reproduce frozen manifest: {spec.DocumentId}:{spec.PackId}");

            var bodyPath = Path.Combine(Path.GetDirectoryName(manifestPath)!,
                row["providerBodyFile"]!.GetValue<string>().Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(bodyPath) || !File.ReadAllBytes(bodyPath).AsSpan().SequenceEqual(pack.ProviderBody))
                return Fail($"p5f: rebuilt provider body differs from frozen body: {spec.DocumentId}:{spec.PackId}");
            var pdfPath = Path.Combine(root, (spec.DocumentId == "SRC-089" ? Src089 : Src095).Replace('/', Path.DirectorySeparatorChar));
            resolved.Add((spec, pack, pdfPath, row));
        }

        var artifactDir = Path.Combine(root, ArtifactRoot.Replace('/', Path.DirectorySeparatorChar));
        var resultPath = Path.Combine(artifactDir, "result.v1.json");
        if (File.Exists(resultPath)) return Fail($"p5f: result already exists; stop before network: {ArtifactRoot}/result.v1.json");
        Directory.CreateDirectory(artifactDir);
        WriteJson(Path.Combine(artifactDir, "selection.v1.json"), new
        {
            schemaVersion = "v5-p5f-v3-selection-v1",
            sourceManifest = ManifestPath,
            sourceHead = GitHead(root),
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
            }),
        });

        Console.WriteLine("p5f v3 canary parity PASS: exactly four frozen bodies reproduced byte-for-byte.");
        foreach (var item in resolved)
            Console.WriteLine($"  [{item.Spec.Role}] {item.Spec.DocumentId} {item.Spec.PackId} hash={item.Pack.ProviderRequestHash}");
        if (!authorized)
        {
            Console.WriteLine($"Not authorized. providerCalls=0, goldRead=false. Pass --confirm-p5f-v3-canary={ConfirmSentinel} to execute.");
            return 0;
        }

        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey)) return Fail("p5f: OPENROUTER_API_KEY is not set. Refusing to run.");
        P5FCanaryGate.Authorize(resolved.Count, authorized);

        var options = RemoteInferenceOptions.FromEnvironment();
        options.Model = envelope.Model;
        options.OpenRouterProviderRoute = envelope.Provider;
        options.OpenRouterReasoningEffort = envelope.Reasoning;
        options.TransientRequestRetries = 0; // P5F freezes one network attempt per authorized call.
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
                using var client = OpenRouterQualificationTransport.CreateOwned(options);
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
            schemaVersion = "v5-p5f-v3-provider-canary-result-v1",
            head = GitHead(root),
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
        Console.WriteLine($"p5f: wrote {ArtifactRoot}/result.v1.json; gate closed after {resolved.Count} calls.");
        return 0;
    }

    private sealed record Analysis(
        string Classification,
        int? DecisionCountActual,
        bool ContractValid,
        bool ParserAccepted,
        bool BinderAccepted,
        int ClaimsProduced,
        int EmptyDecisionCount,
        int MultiAtomClaimCount,
        string MultiAtomValid,
        int RelationTargetClaimCount,
        string RelationTargetValid,
        int BoundCount,
        int BindingRefusalCount,
        string? Error);

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

    private static void WriteJson(string path, object value)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
            new UTF8Encoding(false));
    }

    private static string? GitHead(string root)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD")
            {
                WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false,
            });
            var output = process!.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : null;
        }
        catch { return null; }
    }

    private static string Sha256(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static int Fail(string message) { Console.Error.WriteLine(message); return 1; }
}

/// <summary>Separate from historical V5CanaryGate: this authorization permits exactly four P5F calls.</summary>
internal static class P5FCanaryGate
{
    public const int CanaryRequestCount = 4;

    public static void Authorize(int requestCount, bool providerExecutionAuthorized)
    {
        if (requestCount != CanaryRequestCount)
            throw new InvalidOperationException($"p5f-canary-request-count-must-be-exactly-{CanaryRequestCount}:{requestCount}");
        if (!providerExecutionAuthorized)
            throw new InvalidOperationException("p5f-provider-execution-not-authorized");
    }
}
