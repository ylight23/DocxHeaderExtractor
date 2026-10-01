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
/// Executes precisely the four P5J-frozen 32-decision shards. This is a transport/contract canary,
/// never a semantic score: it reads no Gold, makes no repair, retries or provider fallback, and
/// refuses to reach the network if a previous result artifact exists.
/// </summary>
internal static class P5KV3ShardedCanary
{
    public const string ConfirmSentinel = "yes-i-authorize-p5k-v3-sharded-canary-4-p5j-calls";
    private const string ArtifactRoot = "artifacts/v5-p5k-v3-sharded-canary";
    private const string ManifestPath = "artifacts/v5-p5j-v3-sharded-canary-manifest/execution-manifest.v1.json";
    private const string Src089 = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";
    private const string Src095 = "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf";
    private const int MaxOwnedPerShard = 32;

    private sealed record RoleSpec(string Role, string DocumentId, string OriginalPackId, string SubjectAlias,
        string? AdditionalSubjectAlias, string? RelationTargetAlias);

    private sealed record Resolved(RoleSpec Spec, V5ShardedDecisionRequestV3 Shard, string PdfPath, JsonNode Row);

    private static readonly RoleSpec[] Roles =
    [
        new("MAX_OWNED_AND_MULTIPART", "SRC-089", "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_001", "L0002:S0", "L0002:S1", null),
        new("L1472_OWNER_OMISSION", "SRC-095", "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_017", "L1472:S0", null, null),
        new("L1710_RETYPING", "SRC-095", "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_020", "L1710:S0", null, null),
        new("MULTIPART_RELATION", "SRC-095", "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_011", "L0973:S0", "L0974:S0", "L0918:S4"),
    ];

    public static async Task<int> RunAsync(string root, string[] args)
    {
        var authorized = args.Any(arg => arg == $"--confirm-p5k-v3-sharded-canary={ConfirmSentinel}");
        if (Roles.Length != 4 || Roles.Select(role => role.Role).Distinct(StringComparer.Ordinal).Count() != 4)
            throw new InvalidOperationException("p5k-frozen-selection-must-contain-exactly-4-unique-roles");

        var artifactDirectory = Path.Combine(root, ArtifactRoot.Replace('/', Path.DirectorySeparatorChar));
        var resultPath = Path.Combine(artifactDirectory, "result.v1.json");
        if (File.Exists(resultPath)) return Fail($"p5k: result already exists; stop before network: {ArtifactRoot}/result.v1.json");

        var manifestPath = Path.Combine(root, ManifestPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(manifestPath)) return Fail($"p5k: missing P5J manifest {ManifestPath}");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath)) ?? throw new InvalidOperationException("p5k-manifest-empty");
        if (manifest["status"]?.GetValue<string>() != "PREPARED_NOT_AUTHORIZED" ||
            manifest["policyId"]?.GetValue<string>() != V5DecisionShardingV3.PolicyId ||
            manifest["selectedMaxOwnedDecisionsPerCall"]?.GetValue<int>() != MaxOwnedPerShard ||
            manifest["executionAuthorized"]?.GetValue<bool>() != false ||
            manifest["providerCalls"]?.GetValue<int>() != 0 ||
            manifest["maximumFutureCalls"]?.GetValue<int>() != 4 ||
            manifest["selectedRequests"]?.GetValue<int>() != 4)
            return Fail("p5k: P5J manifest is not the approved four-call, 32-decision frozen shape");

        var contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        var envelope = new V5ProviderEnvelope("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300) { UsageInclude = true };
        var originals = BuildOriginals(root, contract, envelope);
        var resolved = new List<Resolved>();
        foreach (var spec in Roles)
        {
            var row = manifest["rows"]?.AsArray().SingleOrDefault(node => node?["role"]?.GetValue<string>() == spec.Role)
                ?? throw new InvalidOperationException($"p5k-manifest-role-missing:{spec.Role}");
            var original = originals.Single(item => item.DocumentId == spec.DocumentId && item.Pack.PackId == spec.OriginalPackId);
            var shards = V5DecisionShardingV3.Shard(original.Pack, contract, envelope, MaxOwnedPerShard);
            var shardOrdinal = row["selectedShardOrdinal"]?.GetValue<int>()
                ?? throw new InvalidOperationException($"p5k-shard-ordinal-missing:{spec.Role}");
            var shard = shards.Single(candidate => candidate.ShardOrdinal == shardOrdinal);
            var coordinate = shard.Coordinates.SingleOrDefault(candidate => candidate.SourceAlias == spec.SubjectAlias)
                ?? throw new InvalidOperationException($"p5k-subject-not-owned:{spec.Role}:{spec.SubjectAlias}");
            var bodyRelative = row["providerBodyFile"]?.GetValue<string>()
                ?? throw new InvalidOperationException($"p5k-body-file-missing:{spec.Role}");
            var bodyPath = Path.Combine(Path.GetDirectoryName(manifestPath)!, bodyRelative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(bodyPath)) return Fail($"p5k: frozen body missing:{bodyRelative}");
            var frozenBody = File.ReadAllBytes(bodyPath);
            if (!frozenBody.AsSpan().SequenceEqual(shard.Request.ProviderBody) ||
                Sha256(frozenBody) != shard.Request.ProviderRequestHash ||
                row["providerBodySha256"]?.GetValue<string>() != shard.Request.ProviderRequestHash ||
                row["providerRequestHash"]?.GetValue<string>() != shard.Request.ProviderRequestHash ||
                row["semanticRequestHash"]?.GetValue<string>() != shard.Request.Request.RequestHash ||
                row["semanticRequestBytes"]?.GetValue<int>() != shard.Request.Request.Utf8Bytes ||
                row["providerRequestBytes"]?.GetValue<int>() != shard.Request.ProviderRequestBytes ||
                row["ownedCount"]?.GetValue<int>() != shard.Request.OwnedAliases.Count ||
                row["visibleCount"]?.GetValue<int>() != shard.Request.VisibleAliases.Count ||
                row["maxResponseUtf8Bytes"]?.GetValue<int>() != shard.Request.Request.ResponseBounds.MaxResponseUtf8Bytes ||
                row["maxCompletionTokens"]?.GetValue<int>() != shard.Request.MaxCompletionTokens ||
                row["originalPackId"]?.GetValue<string>() != shard.OriginalPackId ||
                row["originalSemanticRequestHash"]?.GetValue<string>() != shard.OriginalSemanticRequestHash ||
                row["originalOwnedOrdinal"]?.GetValue<int>() != coordinate.OriginalOwnedOrdinal ||
                row["shardLocalDecisionOrdinal"]?.GetValue<int>() != coordinate.ShardLocalDecisionOrdinal ||
                shard.Request.OwnedAliases.Count != MaxOwnedPerShard ||
                shard.Request.Request.ResponseBounds.MaxDecisions != MaxOwnedPerShard)
                return Fail($"p5k: rebuilt shard does not reproduce frozen P5J manifest:{spec.Role}");
            if (spec.AdditionalSubjectAlias is not null &&
                (!shard.Request.OwnedAliases.Contains(spec.AdditionalSubjectAlias, StringComparer.Ordinal) ||
                 IndexOf(shard.Request.OwnedAliases, spec.AdditionalSubjectAlias) <= coordinate.ShardLocalDecisionOrdinal))
                return Fail($"p5k: multipart subject is not representable in frozen shard:{spec.Role}");
            if (spec.RelationTargetAlias is not null &&
                !shard.Request.Packet.ContextOnlyEvidence.Any(node => node.SourceAlias == spec.RelationTargetAlias))
                return Fail($"p5k: cross-shard relation target is not context-visible:{spec.Role}");
            resolved.Add(new Resolved(spec, shard, original.PdfPath, row));
        }
        if (resolved.Count != 4) return Fail("p5k: resolved-call-count-not-4");

        Console.WriteLine("p5k parity PASS: exactly four P5J bodies reproduce byte-for-byte; result guard is clear.");
        foreach (var item in resolved)
            Console.WriteLine($"  [{item.Spec.Role}] {item.Shard.Request.PackId} hash={item.Shard.Request.ProviderRequestHash}");
        if (!authorized)
        {
            Console.WriteLine($"Not authorized. providerCalls=0, goldRead=false. Pass --confirm-p5k-v3-sharded-canary={ConfirmSentinel} to execute.");
            return 0;
        }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")))
            return Fail("p5k: OPENROUTER_API_KEY is not set. Refusing to run.");
        P5KCanaryGate.Authorize(resolved.Count, authorized);

        var options = RemoteInferenceOptions.FromEnvironment();
        options.Model = envelope.Model;
        options.OpenRouterProviderRoute = envelope.Provider;
        options.OpenRouterReasoningEffort = envelope.Reasoning;
        options.TransientRequestRetries = 0;
        options.Validate();

        Directory.CreateDirectory(artifactDirectory);
        var results = new List<object>();
        var allExecutionQualified = true;
        foreach (var item in resolved)
        {
            OpenRouterExecutionObservation? observation = null;
            string? transportError = null;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var client = OpenRouterHeaderExtractor.CreateOwned(options);
                observation = await client.ExecuteObservedAsync(item.Shard.Request.ProviderBody, item.Shard.Request.MaxCompletionTokens,
                    V5SystemPromptV2_1.Text, item.Shard.Request.Request.Prompt);
            }
            catch (Exception ex)
            {
                transportError = ex.Message;
            }
            stopwatch.Stop();
            var rawBytes = observation is null ? 0 : Encoding.UTF8.GetByteCount(observation.Content);
            var analysis = Analyze(observation?.Content, transportError, observation?.FinishReason, item, contract,
                V5PdfPreflightBuilder.LoadAtoms(item.PdfPath), rawBytes);
            allExecutionQualified &= analysis.ExecutionQualified;
            results.Add(new
            {
                role = item.Spec.Role,
                documentId = item.Spec.DocumentId,
                originalPackId = item.Shard.OriginalPackId,
                packId = item.Shard.Request.PackId,
                semanticRequestHash = item.Shard.Request.Request.RequestHash,
                providerRequestHash = item.Shard.Request.ProviderRequestHash,
                decisionCountExpected = item.Shard.Request.OwnedAliases.Count,
                maxResponseUtf8Bytes = item.Shard.Request.Request.ResponseBounds.MaxResponseUtf8Bytes,
                maxCompletionTokens = item.Shard.Request.MaxCompletionTokens,
                transportError,
                finishReason = observation?.FinishReason,
                usage = observation?.Usage,
                retryCount = observation?.RetryCount ?? 0,
                latencyMs = stopwatch.Elapsed.TotalMilliseconds,
                sseEventCount = observation?.SseEventCount ?? 0,
                sseRawSha256 = observation is null ? null : Sha256(observation.RawSse),
                sseRaw = observation?.RawSse,
                rawResponseSha256 = observation is null ? null : Sha256(observation.Content),
                rawResponseBytes = rawBytes,
                rawResponse = observation?.Content,
                response = analysis,
            });
            Console.WriteLine($"  -> [{item.Spec.Role}] {analysis.Classification} decisions={analysis.DecisionCountActual}/{item.Shard.Request.OwnedAliases.Count} executionQualified={analysis.ExecutionQualified} latencyMs={stopwatch.ElapsedMilliseconds}");
        }

        WriteJson(resultPath, new
        {
            schemaVersion = "v5-p5k-v3-sharded-provider-canary-result-v1",
            head = GitHead(root),
            sourceManifest = ManifestPath,
            policyId = V5DecisionShardingV3.PolicyId,
            model = envelope.Model,
            provider = envelope.Provider,
            reasoning = envelope.Reasoning,
            responseFormat = envelope.ResponseFormat,
            providerCalls = results.Count,
            maximumAuthorizedProviderCalls = 4,
            goldRead = false,
            semanticScore = "NOT_RUN",
            semanticRetries = 0,
            responseRepairApplied = false,
            fallbackProviderCalls = 0,
            providerExecutionAuthorized = true,
            stopGate = "CLOSED_AFTER_4_CALLS",
            results,
        });
        Console.WriteLine($"p5k: wrote {ArtifactRoot}/result.v1.json; gate closed after {results.Count} calls.");
        return allExecutionQualified ? 0 : 1;
    }

    private sealed record Analysis(string Classification, int? DecisionCountActual, bool JsonComplete, bool ParserAccepted,
        bool BinderAccepted, bool ExecutionQualified, int ClaimsProduced, int BoundCount, int BindingRefusalCount,
        object RoleSpecific, string? Error);

    private static Analysis Analyze(string? raw, string? transportError, string? finishReason, Resolved item,
        DocumentTaskContract contract, IReadOnlyList<SemanticSourceAtom> atoms, int rawBytes)
    {
        if (transportError is not null || raw is null)
            return new("TRANSPORT_ERROR", null, false, false, false, false, 0, 0, 0,
                new { status = "NOT_OBSERVED" }, transportError);
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var actual = root.TryGetProperty("decisions", out var decisions) && decisions.ValueKind == JsonValueKind.Array
                ? decisions.GetArrayLength() : (int?)null;
            V5SemanticDecisionResponseV3 response;
            try
            {
                response = V5SemanticDecisionContractV3.Parse(root, contract, item.Shard.Request.OwnedAliases.Count,
                    item.Shard.Request.Packet.ContextOnlyEvidence.Count);
            }
            catch (Exception ex)
            {
                return new("CONTRACT_INVALID", actual, true, false, false, false, 0, 0, 0,
                    new { status = "NOT_EVALUATED" }, ex.Message);
            }
            var binding = V5DecisionShardingV3.Bind(item.Shard, response, contract, atoms);
            var parserAccepted = true;
            var executionQualified = !string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase) &&
                actual == item.Shard.Request.OwnedAliases.Count && rawBytes <= item.Shard.Request.Request.ResponseBounds.MaxResponseUtf8Bytes;
            var roleSpecific = RoleSpecific(item, response, binding);
            return new(executionQualified ? "EXECUTION_VALID" : "EXECUTION_INVALID", actual, true, parserAccepted,
                binding.Binding is not null, executionQualified, response.Decisions.Sum(decision => decision.Claims.Count),
                binding.Bound.Count, binding.Refusals.Count, roleSpecific, null);
        }
        catch (JsonException ex)
        {
            return new("JSON_INVALID", null, false, false, false, false, 0, 0, 0,
                new { status = "NOT_EVALUATED" }, ex.Message);
        }
    }

    private static object RoleSpecific(Resolved item, V5SemanticDecisionResponseV3 response, V5DecisionBindingResultV3 binding)
    {
        var local = item.Shard.Coordinates.Single(coordinate => coordinate.SourceAlias == item.Spec.SubjectAlias).ShardLocalDecisionOrdinal;
        var decision = response.Decisions[local];
        return item.Spec.Role switch
        {
            "MAX_OWNED_AND_MULTIPART" => new
            {
                decisionPresent = true,
                multipartClaims = decision.Claims.Count(claim => claim.AdditionalSubjectParts?.Count > 0),
                multipartSubjectBindsCorrectly = binding.Bound.Any(bound => bound.Claim.Subject.Parts.Select(part => part.Alias)
                    .SequenceEqual(new[] { item.Spec.SubjectAlias, item.Spec.AdditionalSubjectAlias! })),
            },
            "L1472_OWNER_OMISSION" => new { decisionPresent = true, localDecisionOrdinal = local, claimsInDecision = decision.Claims.Count },
            "L1710_RETYPING" => new
            {
                decisionPresent = true,
                claimsInDecision = decision.Claims.Count,
                wholeAtomSourceSelectionValid = binding.Refusals.All(refusal =>
                    !string.Equals(refusal.Value, "whole-atom-must-omit-verbatim-text", StringComparison.Ordinal)),
            },
            "MULTIPART_RELATION" => new
            {
                decisionPresent = true,
                contextOnlyTargetIndex = item.Shard.Request.Packet.ContextOnlyEvidence.ToList()
                    .FindIndex(node => node.SourceAlias == item.Spec.RelationTargetAlias),
                relationTargetResolved = binding.Bound.Any(bound => bound.Claim.Object?.Parts.SingleOrDefault()?.Alias == item.Spec.RelationTargetAlias),
            },
            _ => throw new InvalidOperationException($"p5k-unrecognized-role:{item.Spec.Role}"),
        };
    }

    private static IReadOnlyList<(string DocumentId, string PdfPath, V5PackedDecisionRequestV3 Pack)> BuildOriginals(
        string root, DocumentTaskContract contract, V5ProviderEnvelope envelope)
    {
        var documents = new[] { ("SRC-089", Src089), ("SRC-095", Src095) };
        return documents.SelectMany(document =>
        {
            var pdfPath = Path.Combine(root, document.Item2.Replace('/', Path.DirectorySeparatorChar));
            return V5PdfPreflightBuilder.BuildV3(pdfPath, document.Item1, contract,
                V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, envelope).Select(pack => (document.Item1, pdfPath, pack));
        }).ToArray();
    }

    private static int IndexOf(IReadOnlyList<string> values, string value)
    {
        for (var index = 0; index < values.Count; index++)
            if (string.Equals(values[index], value, StringComparison.Ordinal)) return index;
        return -1;
    }

    private static void WriteJson(string path, object value) => File.WriteAllText(path,
        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, new UTF8Encoding(false));

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string Sha256(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static int Fail(string message) { Console.Error.WriteLine(message); return 1; }

    private static string? GitHead(string root)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD") { WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false });
            var output = process!.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : null;
        }
        catch { return null; }
    }
}

internal static class P5KCanaryGate
{
    public static void Authorize(int requestCount, bool authorized)
    {
        if (requestCount != 4) throw new InvalidOperationException($"p5k-canary-request-count-must-be-exactly-4:{requestCount}");
        if (!authorized) throw new InvalidOperationException("p5k-provider-execution-not-authorized");
    }
}
