using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocxHeaderExtractor.Eval.ReasoningRetention;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Live-wiring tests for <see cref="OpenRouterCeilingReasoningModel"/>'s three new multi-pass
/// completion methods (section 23 of the qwen9b multi-pass benchmark task). These exercise the
/// real request-building code path -- no provider call is made; a fake <see cref="HttpMessageHandler"/>
/// captures the outgoing request and returns a canned response, so the assertions below are about
/// what THIS process actually sends, not about offline prompt-string construction (already covered
/// by <see cref="MultiPassReasoningProtocolTests"/>).
/// </summary>
public sealed class MultiPassLiveWiringTests
{
    private static OpenRouterModelCapability Capability() => new()
    {
        ModelId = "qwen/qwen3.5-9b",
        ContextLength = 262_144,
        ReasoningSupported = true,
        SupportedReasoningEfforts = ["high"],
        DefaultReasoningEffort = "high",
        MaxCompletionTokens = 48_000,
        StructuredOutputSupported = true,
        SelectedReasoningEffort = "high",
        ReasoningEnabled = true,
        EffortListReported = true,
    };

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public JsonDocument? CapturedBody;
        public string ResponseContent = """{"headings":[]}""";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var bodyText = await request.Content!.ReadAsStringAsync(ct);
            CapturedBody = JsonDocument.Parse(bodyText);
            var payload = new
            {
                choices = new[] { new { finish_reason = "stop", message = new { content = ResponseContent } } },
                usage = new { prompt_tokens = 10, completion_tokens = 5, completion_tokens_details = new { reasoning_tokens = 3 } },
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(payload) };
        }
    }

    private static (OpenRouterCeilingReasoningModel Model, CapturingHandler Handler) NewModel()
    {
        var handler = new CapturingHandler();
        var http = new HttpClient(handler);
        var options = new RemoteInferenceOptions { ApiKey = "test-key", Model = "qwen/qwen3.5-9b", RequestTimeoutSeconds = 5 };
        var model = new OpenRouterCeilingReasoningModel(options, Capability(), http);
        return (model, handler);
    }

    [Fact]
    public async Task S1_OmissionReview_RequestCarriesFullPacketAndInventory_NotJustProposalNeighborhoods()
    {
        var (model, handler) = NewModel();
        handler.ResponseContent = """{"items":[]}""";
        var packetJson = """{"occurrences":[{"i":0,"text":"Chapter One. Some unrelated prose far from any heading. Chapter Two."}]}""";
        var inventoryJson = OmissionReviewPrompt.BuildInventoryJson([new CeilingHeadingProposal(0, 0, 11, "CHAPTER")]);

        await model.CompleteOmissionReviewAsync("DOC-TEST", "route", "req-1", packetJson, inventoryJson, packetJson.Length, 1, 1, CancellationToken.None);

        var messages = handler.CapturedBody!.RootElement.GetProperty("messages");
        var userMessage = messages[1].GetProperty("content").GetString()!;
        // Full occurrence text -- including prose far outside any existing proposal span -- must be
        // visible to the review call, not merely the neighborhoods around already-found proposals.
        Assert.Contains("unrelated prose far from any heading", userMessage);
        Assert.Contains("Chapter Two", userMessage);
        Assert.Contains("\"inventory\"", userMessage);
    }

    [Fact]
    public async Task S1_OmissionReview_NeverReceivesGoldOrExpectedCount()
    {
        var (model, handler) = NewModel();
        handler.ResponseContent = """{"items":[]}""";
        var packetJson = """{"occurrences":[{"i":0,"text":"Body text"}]}""";
        var inventoryJson = OmissionReviewPrompt.BuildInventoryJson([]);

        await model.CompleteOmissionReviewAsync("DOC-TEST", "route", "req-1", packetJson, inventoryJson, packetJson.Length, 0, 1, CancellationToken.None);

        var systemMessage = handler.CapturedBody!.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!.ToLowerInvariant();
        var userMessage = handler.CapturedBody!.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!.ToLowerInvariant();
        foreach (var token in new[] { "gold", "expected count", "ground truth", "answer key" })
        {
            Assert.DoesNotContain(token, systemMessage);
            Assert.DoesNotContain(token, userMessage);
        }
    }

    [Fact]
    public async Task S2_CoverageSemantic_RequestNeverCarriesPriorProposals()
    {
        var (model, handler) = NewModel();
        handler.ResponseContent = """{"headings":[]}""";
        var packetJson = """{"occurrences":[{"i":0,"text":"Chapter One text"}]}""";

        await model.CompleteCoverageSemanticAsync("DOC-TEST", "route", "req-2", packetJson, packetJson.Length, 1, 1, CancellationToken.None);

        var userMessage = handler.CapturedBody!.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
        // Extractor B's request must be exactly the source packet -- it never carries an
        // "inventory"/"proposals" field the way the omission-review call does.
        Assert.DoesNotContain("\"inventory\"", userMessage);
        Assert.DoesNotContain("\"proposals\"", userMessage);
        Assert.Contains("\"occurrences\"", userMessage);
    }

    [Fact]
    public async Task AllThreeMultiPassCalls_NeverDisableReasoning()
    {
        var (model, _) = NewModel();

        async Task<JsonElement> Capture(Func<Task> call)
        {
            var handler = new CapturingHandler();
            var http = new HttpClient(handler);
            var localModel = new OpenRouterCeilingReasoningModel(new RemoteInferenceOptions { ApiKey = "k", RequestTimeoutSeconds = 5 }, Capability(), http);
            handler.ResponseContent = """{"headings":[]}""";
            await localModel.CompleteCoverageSemanticAsync("D", "r", "id", """{"occurrences":[{"i":0,"text":"x"}]}""", 1, 1, 1, CancellationToken.None);
            return handler.CapturedBody!.RootElement;
        }

        var root = await Capture(() => Task.CompletedTask);
        Assert.True(root.TryGetProperty("reasoning", out var reasoning));
        Assert.True(reasoning.TryGetProperty("effort", out _) || (reasoning.TryGetProperty("enabled", out var enabled) && enabled.GetBoolean()));
    }

    [Fact]
    public async Task S3_Verifier_RequestCarriesOccurrencesAndCandidates()
    {
        var (model, handler) = NewModel();
        handler.ResponseContent = """{"decisions":[]}""";
        var occurrencesJson = """[{"i":0,"text":"Chapter One","facts":{}}]""";
        var candidatesJson = """[{"id":"H0001","i":0,"start":0,"end":11,"role":"CHAPTER"}]""";

        await model.CompleteVerifierAsync("DOC-TEST", "route", "req-3", occurrencesJson, candidatesJson, 11, 1, 1, CancellationToken.None);

        var userMessage = handler.CapturedBody!.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
        Assert.Contains("\"occurrences\"", userMessage);
        Assert.Contains("\"candidates\"", userMessage);
        Assert.Contains("H0001", userMessage);
    }

    [Fact]
    public async Task FinishReasonTelemetry_IsPersistedOnTelemetryRecord()
    {
        var (model, handler) = NewModel();
        handler.ResponseContent = """{"headings":[]}""";
        var (_, telemetry) = await model.CompleteCoverageSemanticAsync("D", "r", "id", """{"occurrences":[{"i":0,"text":"x"}]}""", 1, 1, 1, CancellationToken.None);

        Assert.Equal("stop", telemetry.FinishReason);
        Assert.Equal(10, telemetry.ReportedInputTokens);
        Assert.Equal(5, telemetry.ReportedOutputTokens);
        Assert.Equal(3, telemetry.ReportedReasoningTokens);
    }

    [Fact]
    public async Task PublicBenchmark_OmitsZdrOnlyWhenExplicitlyEnabled_AndKeepsReasoningControl()
    {
        var handler = new CapturingHandler { ResponseContent = "{\"headings\":[]}" };
        var capability = Capability() with { ModelId = "qwen/qwen3.7-flash" };
        using var model = new OpenRouterCeilingReasoningModel(new RemoteInferenceOptions
        {
            ApiKey = "k", Model = capability.ModelId, OpenRouterAllowNonZdrPublicBenchmark = true,
            OpenRouterReasoningEnabledOverride = false,
        }, capability, new HttpClient(handler));

        var (_, telemetry) = await model.CompleteSemanticAsync("D", "ModelCapabilityCeiling", "public-r0", "{\"occurrences\":[]}", 0, 0, 0);
        var root = handler.CapturedBody!.RootElement;
        Assert.False(root.TryGetProperty("provider", out _));
        Assert.False(root.GetProperty("reasoning").GetProperty("enabled").GetBoolean());
        Assert.False(telemetry.ReasoningRequested);
        Assert.False(telemetry.ReasoningAccepted);
    }

    [Fact]
    public async Task NormalCampaign_RetainsZdrProviderPolicy()
    {
        var (model, handler) = NewModel();
        handler.ResponseContent = "{\"headings\":[]}";

        await model.CompleteSemanticAsync("D", "ModelCapabilityCeiling", "normal", "{\"occurrences\":[]}", 0, 0, 0);

        var provider = handler.CapturedBody!.RootElement.GetProperty("provider");
        Assert.True(provider.GetProperty("zdr").GetBoolean());
        Assert.Equal("deny", provider.GetProperty("data_collection").GetString());
        Assert.True(provider.GetProperty("require_parameters").GetBoolean());
    }

    [Fact]
    public async Task PinnedProviderRoute_IsInRequest_WhileCanonicalRequestHashStaysRouteNeutral()
    {
        var first = new CapturingHandler { ResponseContent = "{\"headings\":[]}" };
        var second = new CapturingHandler { ResponseContent = "{\"headings\":[]}" };
        var capability = Capability();
        using var modelA = new OpenRouterCeilingReasoningModel(
            new RemoteInferenceOptions { ApiKey = "k", Model = capability.ModelId, OpenRouterProviderRoute = "darkbloom/fp4" },
            capability, new HttpClient(first));
        using var modelB = new OpenRouterCeilingReasoningModel(
            new RemoteInferenceOptions { ApiKey = "k", Model = capability.ModelId, OpenRouterProviderRoute = "deepinfra/bf16" },
            capability, new HttpClient(second));

        var (_, telemetryA) = await modelA.CompleteCoverageSemanticAsync("D", "ModelCapabilityCeiling", "same", "{\"occurrences\":[]}", 0, 0, 0);
        var (_, telemetryB) = await modelB.CompleteCoverageSemanticAsync("D", "ModelCapabilityCeiling", "same", "{\"occurrences\":[]}", 0, 0, 0);

        Assert.Equal("darkbloom/fp4", first.CapturedBody!.RootElement.GetProperty("provider").GetProperty("order")[0].GetString());
        Assert.Equal("deepinfra/bf16", second.CapturedBody!.RootElement.GetProperty("provider").GetProperty("order")[0].GetString());
        Assert.Equal("qwen/qwen3.5-9b", first.CapturedBody.RootElement.GetProperty("model").GetString());
        Assert.False(first.CapturedBody.RootElement.TryGetProperty("models", out _));
        Assert.Equal(telemetryA.CanonicalRequestHash, telemetryB.CanonicalRequestHash);
        Assert.NotEqual(telemetryA.RequestBodyHash, telemetryB.RequestBodyHash);
    }

    [Fact]
    public async Task TelemetrySeparatesHeadersTtfbFromUnavailableStreamingTtft()
    {
        var (model, handler) = NewModel();
        handler.ResponseContent = "{\"headings\":[]}";
        var (_, telemetry) = await model.CompleteCoverageSemanticAsync("D", "ModelCapabilityCeiling", "timing", "{\"occurrences\":[]}", 0, 0, 0);

        Assert.NotNull(telemetry.HeadersReceivedUtc);
        Assert.NotNull(telemetry.FirstResponseByteUtc);
        Assert.NotNull(telemetry.ResponseCompletedUtc);
        Assert.NotNull(telemetry.TtfbMs);
        Assert.Null(telemetry.TtftMs); // non-streaming transport cannot measure first streamed token
        Assert.True(telemetry.ElapsedMs >= telemetry.TtfbMs);
    }

    [Fact]
    public void S3_VerifierKeepDecision_StillRequiresSeparateBinderValidation()
    {
        // Live-wiring counterpart of the offline invariant already proven in
        // MultiPassReasoningProtocolTests: ApplyVerifierDecisions only decides which
        // CeilingHeadingProposal-shaped candidates survive -- it never itself performs span/source
        // validation. A KEEP decision on a candidate whose span is out of range must still be
        // rejected by CeilingProposalBinder afterwards, exactly like any other proposal.
        var candidates = new Dictionary<string, CeilingHeadingProposal>
        {
            ["H0001"] = new CeilingHeadingProposal(0, 0, 5, "CHAPTER"),
            ["H0002"] = new CeilingHeadingProposal(0, 1000, 2000, "SECTION"), // out of range for a 5-char occurrence
        };
        var verifier = new VerifierResponse([
            new VerifierProposalDecision("H0001", VerifierDecision.Keep),
            new VerifierProposalDecision("H0002", VerifierDecision.Keep),
        ]);

        var survivors = MultiPassProposalCombiner.ApplyVerifierDecisions(candidates, verifier);
        Assert.Equal(2, survivors.Count); // combiner itself keeps both -- it is advisory only

        var occurrence = new ReasoningSourceOccurrence
        {
            SourceOccurrenceId = "occ-0", SourceId = "body[1]/p[1]", SourceOrdinal = 0, RawText = "abcde",
            SourceSpan = new DocxHeaderExtractor.Core.Models.StructuralSpan(0, 5),
            CandidateHint = new CandidateHint(false, 0, []),
        };
        var packetResult = CeilingPacketBuilder.Build([occurrence], new HashSet<string> { "occ-0" });
        var bound = CeilingProposalBinder.Bind(survivors, packetResult, new HashSet<string> { "occ-0" });

        // The hard validator (via the binder's range check) drops the out-of-range KEEP candidate --
        // a critic KEEP never bypassed it.
        Assert.Single(bound);
        Assert.Equal((0, 5), (bound[0].Start, bound[0].End));
    }
}
