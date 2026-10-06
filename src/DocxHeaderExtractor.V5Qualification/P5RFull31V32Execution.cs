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

/// <summary>Executes exactly the 31 V3.2 bodies frozen by P5R. Raw transport evidence is frozen before any Gold work.</summary>
internal static class P5RFull31V32Execution
{
    public const string ConfirmSentinel = "yes-i-authorize-p5r-v32-full31-frozen-calls";
    private const string Root = "artifacts/v5-full31-v32";
    private sealed record DocumentSpec(string Id, string Pdf, int ExpectedPacks);
    private sealed record Item(string DocumentId, int ParentOrdinal, V5PackedDecisionRequestV3 Pack,
        V5ComposedSemanticDecisionRequestV3 Request, byte[] Body, string BodyHash, int MaxCompletionTokens);

    private static readonly DocumentSpec[] Documents =
    [
        new("SRC-089", "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf", 7),
        new("SRC-095", "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf", 24),
    ];

    public static async Task<int> RunAsync(string repo, string[] args)
    {
        var authorized = args.Contains($"--confirm-p5r-full31={ConfirmSentinel}");
        var artifact = Path.Combine(repo, Root.Replace('/', Path.DirectorySeparatorChar));
        var resultPath = Path.Combine(artifact, "execution-result.v1.json");
        var checkpointPath = Path.Combine(artifact, "execution.in-progress.v1.json");
        if (File.Exists(resultPath) || File.Exists(checkpointPath)) return Fail("p5r-full31: result or checkpoint exists; stop before network");

        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(artifact, "execution-manifest.v1.json")))?.AsObject()
            ?? throw new InvalidOperationException("p5r-full31-execution-manifest-invalid");
        if (manifest["status"]?.GetValue<string>() != "PREPARED_NOT_AUTHORIZED" ||
            manifest["protocol"]?.GetValue<string>() != V5Protocol.ClaimSchemaVersionV3_2 ||
            manifest["composer"]?.GetValue<string>() != V5SemanticSparseDecisionComposerV3_1.Version ||
            manifest["providerCalls"]?.GetValue<int>() != 0 || manifest["goldRead"]?.GetValue<bool>() != false ||
            manifest["requests"]?.AsArray().Count != 31)
            return Fail("p5r-full31: frozen manifest gate invalid");

        var contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        var envelope = new V5ProviderEnvelope("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300)
        {
            UsageInclude = true,
            OpenRouterResponseCacheDisabled = true,
        };
        var items = Resolve(repo, artifact, manifest, contract, envelope);
        Console.WriteLine("p5r-full31 parity PASS: 31 V3.2 bodies reproduce byte-for-byte.");
        if (!authorized)
        {
            Console.WriteLine("p5r-full31: providerCalls=0; explicit authorization sentinel required.");
            return 0;
        }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")))
            return Fail("p5r-full31: OPENROUTER_API_KEY is not set");

        var options = RemoteInferenceOptions.FromEnvironment();
        options.Model = envelope.Model;
        options.OpenRouterProviderRoute = envelope.Provider;
        options.OpenRouterReasoningEffort = envelope.Reasoning;
        options.TransientRequestRetries = 0;
        options.Validate();

        var rows = new List<object>();
        foreach (var item in items)
        {
            OpenRouterExecutionObservation? observed = null;
            string? transportError = null;
            var watch = Stopwatch.StartNew();
            try
            {
                using var client = OpenRouterQualificationTransport.CreateOwned(options);
                observed = await client.ExecuteObservedAsync(item.Body, item.MaxCompletionTokens,
                    V5SystemPromptV2_1.Text, item.Request.Prompt);
            }
            catch (Exception ex)
            {
                transportError = ex.Message;
            }
            watch.Stop();
            var classification = transportError is not null ? "TRANSPORT_ERROR"
                : string.Equals(observed?.FinishReason, "length", StringComparison.OrdinalIgnoreCase) ? "FINISH_LENGTH"
                : observed?.Content is null ? "NO_ASSEMBLED_CONTENT" : "TRANSPORT_ACCEPTED";
            rows.Add(new
            {
                documentId = item.DocumentId,
                parentOrdinal = item.ParentOrdinal,
                packId = item.Pack.PackId,
                semanticRequestHash = item.Request.RequestHash,
                providerRequestHash = item.BodyHash,
                maxCompletionTokens = item.MaxCompletionTokens,
                decisionCountExpected = item.Pack.OwnedAliases.Count,
                maxResponseUtf8Bytes = item.Request.ResponseBounds.MaxResponseUtf8Bytes,
                classification,
                transportError,
                finishReason = observed?.FinishReason,
                usage = observed?.Usage,
                retryCount = observed?.RetryCount ?? 0,
                latencyMs = watch.Elapsed.TotalMilliseconds,
                sseEventCount = observed?.SseEventCount ?? 0,
                rawSseSha256 = observed is null ? null : Hash(observed.RawSse),
                rawSse = observed?.RawSse,
                assembledContentSha256 = observed is null ? null : Hash(observed.Content),
                assembledContentUtf8Bytes = observed is null ? 0 : Encoding.UTF8.GetByteCount(observed.Content),
                assembledContent = observed?.Content,
            });
            Write(checkpointPath, new
            {
                schemaVersion = "v5-p5r-v32-full31-execution-checkpoint-v1",
                sourceManifest = $"{Root}/execution-manifest.v1.json",
                providerCallsAlreadySent = rows.Count,
                maximumAuthorizedProviderCalls = 31,
                stopBeforeNetworkOnSubsequentInvocation = true,
                rows,
            });
            Console.WriteLine($"[{item.DocumentId} parent {item.ParentOrdinal:D2}] {classification} finish={observed?.FinishReason ?? "none"}");
        }

        Write(resultPath, new
        {
            schemaVersion = "v5-p5r-v32-full31-execution-result-v1",
            head = GitHead(repo),
            sourceManifest = $"{Root}/execution-manifest.v1.json",
            protocol = V5Protocol.ClaimSchemaVersionV3_2,
            composer = V5SemanticSparseDecisionComposerV3_1.Version,
            model = envelope.Model,
            provider = envelope.Provider,
            temperature = 0,
            reasoning = envelope.Reasoning,
            fallbackProviderCalls = 0,
            semanticRetries = 0,
            responseRepairApplied = false,
            providerCalls = rows.Count,
            maximumAuthorizedProviderCalls = 31,
            goldRead = false,
            goldMutation = "NONE",
            status = "EXECUTION_FROZEN_PENDING_OFFLINE_AUDIT",
            rows,
        });
        File.Delete(checkpointPath);
        return 0;
    }

    private static IReadOnlyList<Item> Resolve(string repo, string artifact, JsonObject manifest,
        DocumentTaskContract contract, V5ProviderEnvelope envelope)
    {
        var result = new List<Item>();
        foreach (var document in Documents)
        {
            var packs = V5PdfPreflightBuilder.BuildV3(Path.Combine(repo, document.Pdf.Replace('/', Path.DirectorySeparatorChar)),
                document.Id, contract, V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, envelope);
            if (packs.Count != document.ExpectedPacks) throw new InvalidOperationException($"p5r-full31-pack-count:{document.Id}");
            for (var index = 0; index < packs.Count; index++)
            {
                var pack = packs[index];
                var parentOrdinal = index + 1;
                var request = V5SemanticSparseDecisionComposerV3_1.Compose(contract, pack.Packet);
                var maxTokens = V5SemanticCompletionBudget.Compute(request.ResponseBounds.MaxDecisions,
                    request.ResponseBounds.MaxDecisions, request.Utf8Bytes, V5SemanticDecisionResponseBoundsV3.ProviderCompletionCeiling);
                var body = OpenRouterQwen37JsonObjectCarrierV3.Build(request, maxTokens, envelope);
                var row = manifest["requests"]!.AsArray().Single(item =>
                    item!["documentId"]!.GetValue<string>() == document.Id && item["parentOrdinal"]!.GetValue<int>() == parentOrdinal);
                var bodyFile = row!["providerBodyFile"]!.GetValue<string>();
                var frozen = File.ReadAllBytes(Path.Combine(artifact, bodyFile.Replace('/', Path.DirectorySeparatorChar)));
                if (!frozen.AsSpan().SequenceEqual(body.PayloadBytes) || Hash(frozen) != body.Hash ||
                    row["semanticRequestHash"]!.GetValue<string>() != request.RequestHash ||
                    row["providerRequestHash"]!.GetValue<string>() != body.Hash ||
                    row["maxCompletionTokens"]!.GetValue<int>() != maxTokens)
                    throw new InvalidOperationException($"p5r-full31-parity-failed:{document.Id}:{parentOrdinal}");
                result.Add(new Item(document.Id, parentOrdinal, pack, request, frozen, body.Hash, maxTokens));
            }
        }
        if (result.Count != 31) throw new InvalidOperationException("p5r-full31-item-count-invalid");
        return result;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));
    private static void Write(string path, object value) => File.WriteAllText(path,
        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, new UTF8Encoding(false));
    private static int Fail(string message) { Console.Error.WriteLine(message); return 1; }
    private static string? GitHead(string repo)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD")
            { WorkingDirectory = repo, RedirectStandardOutput = true, UseShellExecute = false });
            var output = process!.StandardOutput.ReadToEnd().Trim(); process.WaitForExit();
            return process.ExitCode == 0 ? output : null;
        }
        catch { return null; }
    }
}
