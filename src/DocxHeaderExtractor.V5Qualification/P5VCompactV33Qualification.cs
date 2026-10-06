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
/// The deliberately non-production V3.3 qualification runner.  Its manifest is prepared by P5V;
/// while its response-volume gate is closed this command stops before creating a transport client.
/// </summary>
internal static class P5VCompactV33Qualification
{
    public const string ConfirmSentinel = "yes-i-authorize-p5v-v33-full31-frozen-calls";
    private const string Root = "artifacts/v5-p5v-v33-qualification";
    private sealed record Spec(string Id, string Pdf, int Packs);
    private sealed record Item(string DocumentId, int ParentOrdinal, V5PackedDecisionRequestV3 Pack,
        V5ComposedSemanticDecisionRequestV3 Request, byte[] Body, string Hash, int MaxTokens, IReadOnlyList<SemanticSourceAtom> Atoms);
    private static readonly Spec[] Documents =
    [
        new("SRC-089", "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf", 7),
        new("SRC-095", "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf", 24),
    ];

    public static async Task<int> RunAsync(string repo, string[] args)
    {
        var artifact = Path.Combine(repo, Root.Replace('/', Path.DirectorySeparatorChar));
        var manifestPath = Path.Combine(artifact, "execution-manifest.v1.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject()
            ?? throw new InvalidOperationException("p5v-v33-manifest-invalid");
        if (manifest["status"]?.GetValue<string>() != "PREPARED_NOT_AUTHORIZED" ||
            manifest["protocol"]?.GetValue<string>() != V5CompactDecisionComposerV3_3.Protocol ||
            manifest["composer"]?.GetValue<string>() != V5CompactDecisionComposerV3_3.Version ||
            manifest["providerCalls"]?.GetValue<int>() != 0 || manifest["goldRead"]?.GetValue<bool>() != false ||
            manifest["requests"]?.AsArray().Count != 31)
            return Fail("p5v-v33: frozen manifest gate invalid");

        var contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        var envelope = new V5ProviderEnvelope("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300)
        { UsageInclude = true, OpenRouterResponseCacheDisabled = true };
        var items = Resolve(repo, artifact, manifest, contract, envelope);
        Console.WriteLine("p5v-v33 parity PASS: 31 compact V3.3 bodies reproduce byte-for-byte.");

        // P5U is intentionally a hard gate, not an authorization flag the operator can bypass.
        if (manifest["blockedBy"]?.GetValue<string>() is { Length: > 0 } blocked)
            return Fail($"p5v-v33: stop before network; qualification is blocked by {blocked}");
        if (!args.Contains($"--confirm-p5v-v33-full31={ConfirmSentinel}"))
        {
            Console.WriteLine("p5v-v33: providerCalls=0; explicit authorization sentinel required.");
            return 0;
        }
        var resultPath = Path.Combine(artifact, "execution-result.v1.json");
        var checkpointPath = Path.Combine(artifact, "execution.in-progress.v1.json");
        if (File.Exists(resultPath) || File.Exists(checkpointPath)) return Fail("p5v-v33: result or checkpoint exists; stop before network");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"))) return Fail("p5v-v33: OPENROUTER_API_KEY is not set");

        var options = RemoteInferenceOptions.FromEnvironment();
        options.Model = envelope.Model; options.OpenRouterProviderRoute = envelope.Provider;
        options.OpenRouterReasoningEffort = envelope.Reasoning; options.TransientRequestRetries = 0; options.Validate();
        var rows = new List<object>();
        foreach (var item in items)
        {
            OpenRouterExecutionObservation? observed = null; string? transportError = null;
            var watch = Stopwatch.StartNew();
            try { using var client = OpenRouterQualificationTransport.CreateOwned(options); observed = await client.ExecuteObservedAsync(item.Body, item.MaxTokens, V5SystemPromptV2_1.Text, item.Request.Prompt); }
            catch (Exception ex) { transportError = ex.Message; }
            watch.Stop();
            var analysis = Analyze(observed?.Content, item, contract);
            rows.Add(new { documentId = item.DocumentId, parentOrdinal = item.ParentOrdinal, packId = item.Pack.PackId,
                semanticRequestHash = item.Request.RequestHash, providerRequestHash = item.Hash, transportError,
                finishReason = observed?.FinishReason, usage = observed?.Usage, retryCount = observed?.RetryCount ?? 0,
                latencyMs = watch.Elapsed.TotalMilliseconds, sseEventCount = observed?.SseEventCount ?? 0,
                rawSseSha256 = observed is null ? null : Hash(observed.RawSse), assembledContentSha256 = observed is null ? null : Hash(observed.Content),
                assembledContentUtf8Bytes = observed is null ? 0 : Encoding.UTF8.GetByteCount(observed.Content), assembledContent = observed?.Content, analysis });
            Write(checkpointPath, new { schemaVersion = "v5-p5v-v33-execution-checkpoint-v1", sourceManifest = $"{Root}/execution-manifest.v1.json", providerCallsAlreadySent = rows.Count, maximumAuthorizedProviderCalls = 31, stopBeforeNetworkOnSubsequentInvocation = true, rows });
        }
        Write(resultPath, new { schemaVersion = "v5-p5v-v33-execution-result-v1", sourceManifest = $"{Root}/execution-manifest.v1.json", protocol = V5CompactDecisionComposerV3_3.Protocol, composer = V5CompactDecisionComposerV3_3.Version, providerCalls = rows.Count, maximumAuthorizedProviderCalls = 31, semanticRetries = 0, responseRepairApplied = false, fallbackProviderCalls = 0, goldRead = false, goldMutation = "NONE", rows });
        File.Delete(checkpointPath);
        return 0;
    }

    private static IReadOnlyList<Item> Resolve(string repo, string artifact, JsonObject manifest, DocumentTaskContract contract, V5ProviderEnvelope envelope)
    {
        var items = new List<Item>();
        foreach (var document in Documents)
        {
            var path = Path.Combine(repo, document.Pdf.Replace('/', Path.DirectorySeparatorChar));
            var packs = V5PdfPreflightBuilder.BuildV3(path, document.Id, contract, V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, envelope);
            if (packs.Count != document.Packs) throw new InvalidOperationException($"p5v-v33-pack-count:{document.Id}");
            var atoms = V5PdfPreflightBuilder.LoadAtoms(path);
            foreach (var (pack, index) in packs.Select((pack, index) => (pack, index)))
            {
                var request = V5CompactDecisionComposerV3_3.Compose(contract, pack.Packet);
                var maxTokens = V5SemanticCompletionBudget.Compute(request.ResponseBounds.MaxDecisions, request.ResponseBounds.MaxDecisions, request.Utf8Bytes, V5SemanticDecisionResponseBoundsV3.ProviderCompletionCeiling);
                var body = OpenRouterQwen37JsonObjectCarrierV3.Build(request, maxTokens, envelope);
                var row = manifest["requests"]!.AsArray().Single(value => value!["documentId"]!.GetValue<string>() == document.Id && value["parentOrdinal"]!.GetValue<int>() == index + 1)!;
                var frozen = File.ReadAllBytes(Path.Combine(artifact, row["providerBodyFile"]!.GetValue<string>().Replace('/', Path.DirectorySeparatorChar)));
                if (!frozen.AsSpan().SequenceEqual(body.PayloadBytes) || row["semanticRequestHash"]!.GetValue<string>() != request.RequestHash || row["providerRequestHash"]!.GetValue<string>() != body.Hash || row["maxCompletionTokens"]!.GetValue<int>() != maxTokens)
                    throw new InvalidOperationException($"p5v-v33-parity-failed:{document.Id}:{index + 1}");
                items.Add(new Item(document.Id, index + 1, pack, request, frozen, body.Hash, maxTokens, atoms));
            }
        }
        if (items.Count != 31) throw new InvalidOperationException("p5v-v33-item-count-invalid");
        return items;
    }

    private static object Analyze(string? raw, Item item, DocumentTaskContract contract)
    {
        if (raw is null) return new { parserAccepted = false, binderExecuted = false, boundClaims = 0, refusedClaims = 0, usableClaims = 0 };
        try
        {
            using var json = JsonDocument.Parse(raw);
            var parsed = V5CompactDecisionContractV3_3.Parse(json.RootElement, contract, item.Pack.Packet.SubjectEvidence, item.Pack.Packet.ContextOnlyEvidence);
            var binding = V5CompactDecisionContractV3_3.Bind(item.Pack.PackId, parsed, contract, item.Pack.Packet.SubjectEvidence, item.Pack.Packet.ContextOnlyEvidence, item.Atoms, ClaimBindingScope.Create(item.Pack.OwnedAliases, item.Pack.VisibleAliases));
            return new { parserAccepted = true, binderExecuted = true, boundClaims = binding.Bound.Count, refusedClaims = binding.Refusals.Count, usableClaims = binding.Bound.Count, parseRefusals = parsed.ParseRefusals };
        }
        catch (Exception ex) { return new { parserAccepted = false, binderExecuted = false, boundClaims = 0, refusedClaims = 0, usableClaims = 0, parserError = ex.Message }; }
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, new UTF8Encoding(false));
    private static int Fail(string message) { Console.Error.WriteLine(message); return 1; }
}
