using System.Security.Cryptography;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Semantics.HeadingAuthority;
using DocxHeaderExtractor.DocumentProcessing.Semantics.HeadingAuthority.Protocols;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Gated, bounded provider run: the production F1 -> G2A -> H2-C V2 authority over SRC-089's font-independent universe,
/// PACK_001 only, through a transport that persists every raw exchange and refuses any call outside the authorization.
/// <para>
/// Authorization (user, 2026-10-07): the sentinel <c>APPROVE_PROVIDER_SRC089_V2_PACK001_MAX9</c> in
/// <c>A99_PROVIDER_SENTINEL</c> - at most 9 outbound calls: one F1 for PACK_001, one G2A only if F1 was accepted, at most
/// seven H2-C only from accepted G2A HAS anchors. No retry, no other pack, no Gold read, no overwrite of v1 captures. A call
/// outside that shape stops the run before the network. Without the sentinel and OPENROUTER_API_KEY this test does nothing.
/// </para>
/// </summary>
public sealed class V5P6TSrc089PdfUniverseV2RequalificationRun
{
    private const string Sentinel = "APPROVE_PROVIDER_SRC089_V2_PACK001_MAX9";
    private const string OutputRoot = "artifacts/v5-p6t-function-membership/p6t-src089-pdf-universe-v2-requalification-20261007";
    private const string Pack001 = "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_001";
    private const string FrozenF1BodySha256 = "85aca595db7e174b3f89ebea803a9d1e4a317d524378d3a5c91c22d9b42f989f";
    private const int MaxCalls = 9;
    private const int MaxH2 = 7;

    [Fact]
    public async Task Run_the_authorized_pack001_requalification()
    {
        if (Environment.GetEnvironmentVariable("A99_PROVIDER_SENTINEL") != Sentinel) return;
        Assert.False(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")), "OPENROUTER_API_KEY is not set");

        var directory = Path.Combine(TestRepository.Root(), OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Assert.False(Directory.Exists(directory), "the capture directory already exists; captures are immutable");
        Directory.CreateDirectory(directory);

        var path = TestRepository.Path(SourcePdfCorpus.Src089);
        var built = PdfSourceAdapter.BuildWithDetails(path);
        var snapshot = JsonSerializer.Deserialize<PdfCanonicalSourceSnapshotV1>(File.ReadAllText(TestRepository.Path(
            $"eval/a99-closed-loop/pdf-canonical-source-v2/{built.Snapshot.SourceSha256}.json")), FreezeArtifact.Json)!.Rehydrate();
        Assert.Equal(snapshot.SourceAliasUniverseSha256, built.Snapshot.SourceAliasUniverseHash);

        var options = RemoteInferenceOptions.FromEnvironment();
        options.Model = "qwen/qwen3.7-flash";
        options.OpenRouterProviderRoute = "alibaba";
        options.OpenRouterReasoningEffort = "none";
        options.RequireJsonObjectResponse = true;
        options.TransientRequestRetries = 0;
        options.ProviderTransportTimeoutSeconds = 300;
        options.Validate();

        using var inner = OpenRouterInferenceTransport.CreateOwned(options);
        var transport = new BoundedCapturingTransport(inner, directory);
        var authority = new FunctionAnchorExtentHeadingAuthority(transport, new OpenRouterQwen37InferenceRequestComposer(), built.Details.LayoutBlockByAtom, () => { });

        string outcome;
        try
        {
            await authority.DecideAsync(built.Snapshot, CancellationToken.None);
            outcome = "COMPLETED";
        }
        catch (BoundedCapturingTransport.StopRun stop)
        {
            outcome = "STOPPED_BEFORE_NETWORK: " + stop.Message;
        }
        catch (Exception ex)
        {
            outcome = "FAILED: " + ex.GetType().Name + ": " + ex.Message;
        }

        File.WriteAllText(Path.Combine(directory, "run-summary.v1.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = "v5-p6t-src089-pdf-universe-v2-requalification-run-v1",
            sentinel = Sentinel,
            sourceUniverseSha256 = built.Snapshot.SourceAliasUniverseHash,
            pack = Pack001,
            outcome,
            outboundCalls = transport.Calls.Count,
            maximumOutboundCalls = MaxCalls,
            f1 = transport.Calls.Count(call => call.Stage == "F1"),
            g2a = transport.Calls.Count(call => call.Stage == "G2A"),
            h2c = transport.Calls.Count(call => call.Stage == "H2C"),
            goldRead = false,
            goldMutation = "NONE",
            calls = transport.Calls,
        }, new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n"), new UTF8Encoding(false));
    }

    /// <summary>Provider-free: the first request the authority would send is the frozen PACK_001 F1 body, and nothing else is sent first.</summary>
    [Fact]
    public async Task The_first_request_is_the_frozen_pack001_F1_body_and_the_guard_refuses_a_second_F1()
    {
        var built = PdfSourceAdapter.BuildWithDetails(TestRepository.Path(SourcePdfCorpus.Src089));
        var directory = Path.Combine(Path.GetTempPath(), "a99-src089-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var fake = new NetworkRefusingTransport();
            var transport = new BoundedCapturingTransport(fake, directory);
            var authority = new FunctionAnchorExtentHeadingAuthority(transport, new OpenRouterQwen37InferenceRequestComposer(), built.Details.LayoutBlockByAtom, () => { });
            await Assert.ThrowsAsync<NetworkRefusingTransport.WouldHaveSent>(() => authority.DecideAsync(built.Snapshot, CancellationToken.None));
            Assert.Equal(1, fake.Attempts);
            Assert.Empty(transport.Calls);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class NetworkRefusingTransport : IFrozenInferenceTransport
    {
        public sealed class WouldHaveSent : Exception;
        public int Attempts { get; private set; }
        public string ModelName => "";
        public int ContextSize => 0;
        public string RuntimeDescription => "";
        public int SharedPrefixTokens => 0;
        public void Dispose() { }
        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default, int expectedItemCount = 0) => throw new WouldHaveSent();
        public Task<FrozenInferenceResult> ExecuteFrozenRequestAsync(byte[] providerBody, int maxTokens, string systemPrompt, string userMessage, CancellationToken cancellationToken = default)
        {
            Attempts++;
            throw new WouldHaveSent();
        }
    }

    private sealed class BoundedCapturingTransport(IFrozenInferenceTransport inner, string directory) : IFrozenInferenceTransport
    {
        public sealed class StopRun(string message) : Exception(message);

        public sealed record CallRow(int Ordinal, string Stage, string RequestBodySha256, int RequestBytes, string? FinishReason, int ResponseBytes, string ResponseSha256, int? PromptTokens, int? CompletionTokens);

        public List<CallRow> Calls { get; } = [];
        private int _hasAnchors = -1;

        public string ModelName => inner.ModelName;
        public int ContextSize => inner.ContextSize;
        public string RuntimeDescription => inner.RuntimeDescription;
        public int SharedPrefixTokens => inner.SharedPrefixTokens;
        public void Dispose() { }

        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default, int expectedItemCount = 0) =>
            throw new StopRun("only frozen requests are authorized");

        public async Task<FrozenInferenceResult> ExecuteFrozenRequestAsync(
            byte[] providerBody, int maxTokens, string systemPrompt, string userMessage, CancellationToken cancellationToken = default)
        {
            var stage = string.Equals(systemPrompt, HeadingAnchorProtocolV1.SystemPrompt, StringComparison.Ordinal) ? "G2A"
                : string.Equals(systemPrompt, HeadingExtentProtocolV2.SystemPrompt, StringComparison.Ordinal) ? "H2C" : "F1";
            var bodySha = Convert.ToHexStringLower(SHA256.HashData(providerBody));

            if (Calls.Count >= MaxCalls) throw new StopRun("a tenth call");
            if (stage == "F1")
            {
                if (Calls.Any(call => call.Stage == "F1")) throw new StopRun("a second F1 call (only PACK_001 is authorized, no retry)");
                if (bodySha != FrozenF1BodySha256) throw new StopRun("the F1 request is not the frozen PACK_001 body");
            }
            else if (stage == "G2A")
            {
                if (Calls.Any(call => call.Stage == "G2A")) throw new StopRun("a second G2A call");
            }
            else
            {
                if (_hasAnchors < 0) throw new StopRun("H2-C before an accepted G2A");
                if (_hasAnchors > MaxH2) throw new StopRun($"G2A produced {_hasAnchors} HAS anchors, above the authorized {MaxH2}");
                if (Calls.Count(call => call.Stage == "H2C") >= MaxH2) throw new StopRun("an eighth H2-C call");
            }

            var ordinal = Calls.Count + 1;
            var stem = Path.Combine(directory, $"call-{ordinal:00}-{stage}");
            File.WriteAllBytes(stem + ".request-body.json", providerBody);

            var result = await inner.ExecuteFrozenRequestAsync(providerBody, maxTokens, systemPrompt, userMessage, cancellationToken).ConfigureAwait(false);

            int? Usage(string name) => result.Usage is { ValueKind: JsonValueKind.Object } usage && usage.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;
            var row = new CallRow(ordinal, stage, bodySha, providerBody.Length, result.FinishReason, Encoding.UTF8.GetByteCount(result.Content),
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(result.Content))), Usage("prompt_tokens"), Usage("completion_tokens"));
            Calls.Add(row);
            File.WriteAllText(stem + ".raw-capture.v1.json", JsonSerializer.Serialize(new
            {
                schemaVersion = "v5-p6t-src089-pdf-universe-v2-raw-capture-v1",
                call = row,
                maxTokens,
                content = result.Content,
                finishReason = result.FinishReason,
                usage = result.Usage,
                rawSse = result.RawSse,
                sseEventCount = result.SseEventCount,
                retryCount = result.RetryCount,
                goldRead = false,
            }, new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n"), new UTF8Encoding(false));

            if (stage == "G2A")
                _hasAnchors = result.Content.Split("HAS_STRUCTURAL_EXTENT").Length - 1;
            return result;
        }
    }
}
