extern alias WebApp;

using System.Net;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.Infrastructure.AI;
using DocxHeaderExtractor.Infrastructure.Sources;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WebApp::DocxHeaderExtractor.Web;

namespace DocxHeaderExtractor.Tests;

public sealed class WebPipelineV2ApiTests
{
    private const string Pdf = "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";

    [Fact(Timeout = 180_000)]
    public async Task Pdf_upload_reaches_real_pipeline_and_retains_source_grounding_without_raw_transport()
    {
        var transport = new FakeTransport();
        await using var app = App(transport);
        var events = await Upload(app, TestRepository.Path(Pdf), noLlm: false);
        var result = Assert.Single(events, e => e.GetProperty("type").GetString() == "result");
        Assert.Equal("pdf", result.GetProperty("sourceType").GetString());
        var dto = result.GetProperty("pipeline");
        Assert.Equal("same-execution", dto.GetProperty("availability").GetString());
        var headings = dto.GetProperty("headings").EnumerateArray().ToArray();
        Assert.NotEmpty(headings);
        Assert.Equal(result.GetProperty("outline").GetProperty("headings").GetArrayLength(), headings.Length);
        foreach (var heading in headings)
        {
            var source = heading.GetProperty("sources")[0];
            var text = source.GetProperty("sourceText").GetString()!;
            var span = source.GetProperty("span");
            Assert.Equal(text[span.GetProperty("start").GetInt32()..span.GetProperty("end").GetInt32()],
                source.GetProperty("selectedText").GetString());
            Assert.Equal("source-catalog", source.GetProperty("availability").GetString());
        }
        Assert.True(transport.FrozenCalls >= 3);
        Assert.Contains("source-selection:completed", dto.GetProperty("checkpoints").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains(dto.GetProperty("stages").EnumerateArray(), e =>
            e.GetProperty("id").GetString() == "F1-semantic-function" && e.GetProperty("status").GetString() == "not-recorded");
        Assert.DoesNotContain("rawAnalystResponses", result.GetRawText());
        Assert.DoesNotContain("RawSse", result.GetRawText());
        Assert.DoesNotContain("reasoning", result.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("usage", result.GetRawText());
        Assert.DoesNotContain("private-path", result.GetRawText());
    }

    [Theory(Timeout = 180_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Docx_upload_uses_text_lane_and_empty_result_is_not_fake_rejection(bool noLlm)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dhx-web-v2-{Guid.NewGuid():N}.docx");
        try
        {
            SampleDocumentFactory.Create(path);
            var transport = new FakeTransport();
            await using var app = App(transport);
            var events = await Upload(app, path, noLlm);
            var result = Assert.Single(events, e => e.GetProperty("type").GetString() == "result");
            var dto = result.GetProperty("pipeline");
            Assert.Equal("docx", dto.GetProperty("sourceKind").GetString());
            Assert.Equal("same-execution", dto.GetProperty("availability").GetString());
            Assert.Empty(dto.GetProperty("headings").EnumerateArray());
            Assert.False(dto.GetProperty("summary").TryGetProperty("rejected", out _));
            Assert.Equal("not-recorded", dto.GetProperty("summary").GetProperty("rejectedAvailability").GetString());
            Assert.DoesNotContain(dto.GetProperty("stages").EnumerateArray(), e => e.GetProperty("id").GetString()!.StartsWith("F1"));
            Assert.Equal(0, transport.FrozenCalls);
            Assert.Equal(noLlm ? 0 : 1, transport.TextCalls);
            Assert.True(result.TryGetProperty("humanReview", out _));
        }
        finally { OfficeDocumentConverter.TryDelete(path); }
    }

    [Fact(Timeout = 180_000)]
    public async Task Failure_is_streamed_without_provider_error_payload_or_fake_result()
    {
        var transport = new FakeTransport { Fail = true };
        await using var app = App(transport);
        var events = await Upload(app, TestRepository.Path(Pdf), noLlm: false);
        Assert.DoesNotContain(events, e => e.GetProperty("type").GetString() == "result");
        var error = Assert.Single(events, e => e.GetProperty("type").GetString() == "error");
        Assert.Contains("Execution failed", error.GetProperty("message").GetString());
        Assert.DoesNotContain("SECRET-PROVIDER-PAYLOAD", JsonSerializer.Serialize(events));
        Assert.Equal("failed", error.GetProperty("pipeline").GetProperty("stages")[0].GetProperty("status").GetString());
        Assert.Equal(1, transport.FrozenCalls);
    }

    [Fact(Timeout = 180_000)]
    public async Task Docx_accepted_heading_reaches_inspector_from_the_same_execution()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dhx-web-heading-{Guid.NewGuid():N}.docx");
        try
        {
            SampleDocumentFactory.Create(path);
            var transport = new FakeTransport { TextHeading = true };
            await using var app = App(transport);
            var events = await Upload(app, path, noLlm: false);
            var result = Assert.Single(events, e => e.GetProperty("type").GetString() == "result");
            var heading = Assert.Single(result.GetProperty("pipeline").GetProperty("headings").EnumerateArray());
            var outlineHeading = Assert.Single(result.GetProperty("outline").GetProperty("headings").EnumerateArray());
            Assert.Equal(outlineHeading.GetProperty("stableId").GetString(), heading.GetProperty("stableId").GetString());
            Assert.Equal(outlineHeading.GetProperty("text").GetString(), heading.GetProperty("text").GetString());
            Assert.Equal("source-catalog", heading.GetProperty("sources")[0].GetProperty("availability").GetString());
            Assert.Equal(0, transport.FrozenCalls);
            Assert.True(transport.TextCalls > 0);
        }
        finally { OfficeDocumentConverter.TryDelete(path); }
    }

    [Fact(Timeout = 180_000)]
    public async Task Partial_pdf_withheld_extent_does_not_become_a_heading_or_fake_rejection_count()
    {
        var transport = new FakeTransport { WithholdFirstExtent = true };
        await using var app = App(transport);
        var events = await Upload(app, TestRepository.Path(Pdf), noLlm: false);
        var result = Assert.Single(events, e => e.GetProperty("type").GetString() == "result");
        Assert.True(transport.Withheld);
        var dto = result.GetProperty("pipeline");
        Assert.NotEmpty(dto.GetProperty("headings").EnumerateArray());
        Assert.Equal(result.GetProperty("outline").GetProperty("headings").GetArrayLength(), dto.GetProperty("headings").GetArrayLength());
        Assert.Equal("not-recorded", dto.GetProperty("summary").GetProperty("rejectedAvailability").GetString());
    }

    [Fact]
    public async Task Missing_upload_returns_clear_validation_error_without_transport()
    {
        var transport = new FakeTransport();
        await using var app = App(transport);
        using var client = app.CreateClient();
        using var response = await client.PostAsync("/api/extract", new MultipartFormDataContent());
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, transport.FrozenCalls + transport.TextCalls);
    }

    private static WebApplicationFactory<WebApp::Program> App(FakeTransport transport) =>
        new WebApplicationFactory<WebApp::Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<WebTransportFactoryResolver>();
            services.AddSingleton<WebTransportFactoryResolver>(_ => _ => new FakeFactory(transport));
        }));

    private static async Task<JsonElement[]> Upload(WebApplicationFactory<WebApp::Program> app, string path, bool noLlm)
    {
        using var client = app.CreateClient();
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(await File.ReadAllBytesAsync(path)), "file", Path.GetFileName(path));
        if (noLlm) form.Add(new StringContent("true"), "noLlm");
        else { form.Add(new StringContent("lmstudio"), "backend"); form.Add(new StringContent("test-model"), "lmStudioModel"); }
        form.Add(new StringContent("true"), "showRaw"); // even opt-in cannot leak payloads in production Web
        using var response = await client.PostAsync("/api/extract", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
    }

    private sealed class FakeFactory(FakeTransport transport) : IInferenceTransportFactory
    {
        public Task<IInferenceTransport> CreateAsync(CancellationToken ct = default) => Task.FromResult<IInferenceTransport>(transport);
        public Task<IInferenceTransport> CreatePdfProductionAsync(CancellationToken ct = default) => CreateAsync(ct);
    }

    // In-process transport only; no network client or provider credentials.
    private sealed class FakeTransport : IPdfProductionAuthorizedInferenceTransport
    {
        public int FrozenCalls { get; private set; }
        public int TextCalls { get; private set; }
        public bool Fail { get; init; }
        public bool TextHeading { get; init; }
        public bool WithholdFirstExtent { get; init; }
        public bool Withheld { get; private set; }
        public string ModelName => "test";
        public int ContextSize => 1 << 20;
        public string RuntimeDescription => "no provider";
        public int SharedPrefixTokens => 0;
        public string PdfProductionProvider => "test";
        public string PdfProductionModel => "test";
        public IFrozenInferenceRequestComposer RequestComposer => new OpenRouterQwen37InferenceRequestComposer();
        public void Dispose() { }
        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default, int expectedItemCount = 0)
        {
            TextCalls++;
            if (!TextHeading) return Task.FromResult("{\"headings\":[]}");
            // DOCX's frozen composer appends its schema after the JSON evidence packet.
            using var json = JsonDocument.Parse(userMessage.Split("\nSCHEMA=", 2)[0]);
            if (!json.RootElement.TryGetProperty("sourceEvidence", out var evidence)) return Task.FromResult("{\"headings\":[]}");
            var first = evidence.EnumerateArray().First(row => row.GetProperty("owned").GetBoolean());
            return Task.FromResult(JsonSerializer.Serialize(new { headings = new[] { new {
                sourceAlias = first.GetProperty("alias").GetString(), isHeading = true,
                verbatimText = first.GetProperty("text").GetString(), semanticRole = "SECTION",
                relationHints = new[] { "parent-node:ROOT" } } } }));
        }
        public Task<FrozenInferenceResponse> ExecuteFrozenRequestAsync(byte[] body, int maxTokens, string prompt, string user,
            CancellationToken cancellationToken = default)
        {
            FrozenCalls++;
            if (Fail) throw new InvalidOperationException("SECRET-PROVIDER-PAYLOAD");
            using var document = JsonDocument.Parse(user);
            var request = document.RootElement;
            var protocol = request.GetProperty("protocolVersion").GetString()!;
            object response;
            if (protocol.Contains("total-occurrence-function"))
            {
                var rows = request.GetProperty("occurrences").EnumerateArray().ToArray();
                response = new { decisions = rows.Select((row, i) => new
                    { occurrence = row.GetProperty("id").GetString(), function = i == 0 || WithholdFirstExtent && i == 1 ? "ESTABLISHES_STRUCTURE" : "OTHER" }) };
            }
            else if (protocol.Contains("anchor-existence"))
                response = new { decisions = request.GetProperty("occurrences").EnumerateArray().Select(row => new
                    { primary = row.GetProperty("primary").GetString(), anchor = "HAS_STRUCTURAL_EXTENT" }) };
            else
            {
                if (WithholdFirstExtent && !Withheld)
                { Withheld = true; return Task.FromResult(new FrozenInferenceResponse("{}", "length")); }
                var anchor = request.GetProperty("anchors")[0];
                var rows = anchor.GetProperty("occurrences").EnumerateArray().ToArray();
                var id = anchor.GetProperty("anchor").GetString();
                response = new { decisions = new[] { new { anchor = id, headingMembers = new[] { id }, endOccurrence = id,
                    firstOutsideOccurrence = rows.Length > 1 ? rows[1].GetProperty("occurrence").GetString() : null,
                    firstOutsideRole = rows.Length > 1 ? "BODY_CONTENT" : "NO_VISIBLE_SUCCESSOR" } } };
            }
            return Task.FromResult(new FrozenInferenceResponse(JsonSerializer.Serialize(response), "stop"));
        }
    }
}
