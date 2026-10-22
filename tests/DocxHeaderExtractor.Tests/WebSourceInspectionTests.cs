extern alias WebApp;

using System.Text.Json;
using DocxHeaderExtractor.App;
using DocxHeaderExtractor.Contracts.Provider;
using DocxHeaderExtractor.Sources.Detect;
using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.Contracts.Scan;
using DocxHeaderExtractor.Web;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using UglyToad.PdfPig.Writer;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using WebProgram = WebApp::Program;

namespace DocxHeaderExtractor.Tests;

[Trait("Category", "Slow")]
public sealed class WebSourceInspectionTests : IDisposable
{
    readonly string dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "dhx-web-inspect-test-" + Guid.NewGuid().ToString("N"))).FullName;
    static string ScanPath => TestRepository.Path("todo10_8/generated-docx/01_phap_quy/023_ND_23-2015_Chung_thuc.docx");

    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    static MultipartFormDataContent Upload(string path, string? name = null)
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(File.ReadAllBytes(path)), "file", name ?? Path.GetFileName(path));
        return form;
    }

    [Fact]
    public async Task Inspection_detects_scan_from_content_even_when_the_upload_claims_to_be_a_pdf()
    {
        using var app = new WebApplicationFactory<WebProgram>();
        using var client = app.CreateClient();
        using var form = Upload(ScanPath, "renamed.pdf");
        using var response = await client.PostAsync("/api/inspect", form);
        response.EnsureSuccessStatusCode();
        var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("docx", result.GetProperty("format").GetString());
        Assert.Equal("scan", result.GetProperty("route").GetString());
        Assert.True(result.GetProperty("requiresOcr").GetBoolean());
        Assert.True(result.GetProperty("supported").GetBoolean());
        Assert.False(result.GetProperty("canWriteback").GetBoolean());
        Assert.Equal(34, result.GetProperty("pageImages").GetInt32());
    }

    [Fact]
    public async Task Native_word_is_not_sent_to_ocr_and_the_scan_prompt_is_available()
    {
        var path = DocxTestDocument.Create(Path.Combine(dir, "native.docx"));
        using var app = new WebApplicationFactory<WebProgram>();
        using var client = app.CreateClient();
        using var form = Upload(path, "renamed.pdf");
        using var response = await client.PostAsync("/api/inspect", form);
        response.EnsureSuccessStatusCode();
        var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("docx", result.GetProperty("route").GetString());
        Assert.False(result.GetProperty("requiresOcr").GetBoolean());
        var prompts = JsonDocument.Parse(await client.GetStringAsync("/api/prompts")).RootElement;
        Assert.Equal(DocxHeaderExtractor.HeadingAgent.Protocol.PromptCatalog.Scan.SystemPrompt, prompts.GetProperty("scan").GetProperty("systemPrompt").GetString());
    }

    [Fact]
    public async Task Extraction_rechecks_the_source_and_refuses_a_model_run_without_a_key_before_any_call()
    {
        using var app = new WebApplicationFactory<WebProgram>().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<ModelProviderFactory>(_ => () => new OpenRouterProvider(null))));
        using var client = app.CreateClient();
        using var form = Upload(ScanPath);
        form.Add(new StringContent("docx"), "route"); // client cannot hide that this is a scan
        using var response = await client.PostAsync("/api/extract", form);
        var events = (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToArray();
        Assert.Equal("scan", events.Single(e => e.GetProperty("type").GetString() == "source").GetProperty("source").GetProperty("route").GetString());
        Assert.Contains("OPENROUTER_API_KEY", events.Single(e => e.GetProperty("type").GetString() == "error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task Offline_web_upload_reaches_the_scan_pipeline_without_asking_for_a_model_route()
    {
        var scan = new OfflineScan();
        using var app = new WebApplicationFactory<WebProgram>().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<ModelProviderFactory>(_ => () => new OfflineFactory(scan))));
        using var client = app.CreateClient();
        using var form = Upload(ScanPath);
        form.Add(new StringContent("1"), "noLlm");
        using var response = await client.PostAsync("/api/extract", form);
        var events = (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToArray();
        Assert.DoesNotContain(events, e => e.GetProperty("type").GetString() == "error");
        var result = events.Single(e => e.GetProperty("type").GetString() == "result");
        Assert.Equal("scan", result.GetProperty("sourceInspection").GetProperty("route").GetString());
        Assert.Equal("scan-v6-selected-lines", result.GetProperty("outline").GetProperty("deterministicRoute").GetString());
        Assert.Equal(1, scan.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_pdf_with_image_only_pages_is_identified_before_it_can_silently_run_as_text(bool hasFooter)
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(200, 300);
        page.AddPng(TestPng.Grey(40, 60), new PdfRectangle(0, 0, 200, 300));
        if (hasFooter) page.AddText("Page 1", 10, new PdfPoint(10, 10), builder.AddStandard14Font(Standard14Font.Helvetica));
        var path = Path.Combine(dir, "scan.pdf");
        File.WriteAllBytes(path, builder.Build());
        var scan = new OfflineScan();
        using var app = new WebApplicationFactory<WebProgram>().WithWebHostBuilder(host => host.ConfigureTestServices(services =>
            services.AddSingleton<ModelProviderFactory>(_ => () => new OfflineFactory(scan))));
        using var client = app.CreateClient();
        using var form = Upload(path, "renamed.docx");
        using var response = await client.PostAsync("/api/inspect", form);
        var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.True(result.GetProperty("requiresOcr").GetBoolean());
        Assert.True(result.GetProperty("supported").GetBoolean());
        Assert.Equal("scan", result.GetProperty("route").GetString());
        using var extractForm = Upload(path);
        extractForm.Add(new StringContent("1"), "noLlm");
        using var extract = await client.PostAsync("/api/extract", extractForm);
        var events = (await extract.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToArray();
        Assert.DoesNotContain(events, e => e.GetProperty("type").GetString() == "error");
        Assert.Equal("scan-v6-selected-lines", events.Single(e => e.GetProperty("type").GetString() == "result").GetProperty("outline").GetProperty("deterministicRoute").GetString());
        Assert.Equal(1, scan.Calls);
    }

    [Fact]
    public void A_text_pdf_with_a_small_image_uses_the_text_route()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(200, 300);
        page.AddPng(TestPng.Grey(10, 10), new PdfRectangle(0, 0, 20, 20));
        page.AddText("Native text", 12, new PdfPoint(10, 200), builder.AddStandard14Font(Standard14Font.Helvetica));
        var path = Path.Combine(dir, "native.pdf");
        File.WriteAllBytes(path, builder.Build());
        var source = SourceInspection.Read(path);
        Assert.Equal("pdf", source.Route);
        Assert.False(source.RequiresOcr);
        Assert.True(source.Supported);
    }

    sealed class OfflineFactory(OfflineScan scan) : IModelProvider
    {
        public IScanHeadingSelector CreateScanHeadingSelector() => scan;
        public ModelRoute CreateRoute() => throw new Exception("No model route may be asked for");
    }

    sealed class OfflineScan : IScanHeadingSelector
    {
        public int Calls;
        public string SelectorId => "web-test-scan";
        public Task<ScanDocumentSelection> PrepareAsync(string path, string? apiKey, CancellationToken ct)
        {
            Assert.Null(apiKey);
            Calls++;
            var line = new ScanSourceLine("P001-L001", "P001-ocr-original-L001", 1, new PixelBox(10, 10, 100, 20), 200, 300,
                "Body", "Body", "ocr-original", new Dictionary<string, string> { ["ocr-original"] = "Body" }, null, "{}");
            return Task.FromResult(new ScanDocumentSelection(SourceHash.Compute(path), [line],
                new HeadingSelection(new HashSet<string>(), 0, 0, [], [], 0, 0),
                new ScanRunDiagnostics(0, 0, 0, 0, 0, 0, new Dictionary<string, int>(), [], [], [], new Dictionary<string, double>())));
        }
    }
}
