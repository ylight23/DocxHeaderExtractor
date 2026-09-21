extern alias WebApp;

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The Web host must decide what a file is once, and every endpoint must reach the same answer.
/// <para>
/// The extraction path was taught to route on the uploaded bytes. Two other paths in this host were
/// not: <c>/api/inspect</c> and the human-review snapshot inside <c>/api/extract</c> both went
/// through <c>LegacyDocConverter.EnsureDocx</c>, which refuses a PDF on its extension. So the file
/// picker accepted a PDF and the host answered "unsupported Word conversion" - the product
/// disagreeing with itself about what the user had just uploaded.
/// </para>
/// </summary>
public sealed class WebPdfRouteAgreementTests : IClassFixture<WebApplicationFactory<WebApp::Program>>
{
    private const string Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";

    private readonly WebApplicationFactory<WebApp::Program> _factory;

    public WebPdfRouteAgreementTests(WebApplicationFactory<WebApp::Program> factory) => _factory = factory;

    [Fact]
    public async Task Inspect_recognises_an_uploaded_pdf_instead_of_refusing_it()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsync("/api/inspect", Upload("minutes.pdf", "application/pdf"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("pdf", payload.GetProperty("sourceType").GetString());
        Assert.True(payload.GetProperty("supported").GetBoolean());
        // Absent, not empty - the same rule as tableDepth. A DOCX-shaped report with every number
        // at zero reads as "measured and found nothing", when the measurement does not exist for
        // this format at all.
        Assert.False(payload.TryGetProperty("report", out _));
        Assert.False(payload.TryGetProperty("suggestedRoute", out _));
        Assert.False(payload.GetProperty("canRunDeterministic").GetBoolean());
        Assert.True(payload.GetProperty("pages").GetInt32() > 0);
        Assert.True(payload.GetProperty("sourceOccurrences").GetInt32() > 0);
    }

    [Fact]
    public async Task Inspect_still_classifies_a_docx_exactly_as_before()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dhx-inspect-{Guid.NewGuid():N}.docx");
        DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer.SampleDocumentFactory.Create(path);
        try
        {
            using var client = _factory.CreateClient();
            using var content = new MultipartFormDataContent();
            using var file = new ByteArrayContent(await File.ReadAllBytesAsync(path));
            file.Headers.ContentType = new MediaTypeHeaderValue(
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
            content.Add(file, "file", "report.docx");

            using var response = await client.PostAsync("/api/inspect", content);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("docx", payload.GetProperty("sourceType").GetString());
            Assert.Equal(JsonValueKind.Object, payload.GetProperty("report").ValueKind);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_file_of_no_known_format_is_refused_by_name_rather_than_inside_a_parser()
    {
        using var client = _factory.CreateClient();
        using var content = new MultipartFormDataContent();
        using var file = new ByteArrayContent([0x00, 0x01, 0x02, 0x03]);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", "notes.docx");

        using var response = await client.PostAsync("/api/inspect", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Contains("notes.docx", payload.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Extract_runs_the_pdf_lane_from_the_web_host()
    {
        using var client = _factory.CreateClient();
        var content = Upload("minutes.pdf", "application/pdf");
        content.Add(new StringContent("true"), "noLlm");

        using var response = await client.PostAsync("/api/extract", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = LastResult(await response.Content.ReadAsStringAsync());
        Assert.Equal("pdf-canonical-vnext", result.GetProperty("outline").GetProperty("deterministicRoute").GetString());
    }

    [Fact]
    public async Task Both_endpoints_agree_about_what_the_same_bytes_are()
    {
        // The invariant. One upload has one source type, and no endpoint in this host may reach a
        // different conclusion about it than another.
        using var client = _factory.CreateClient();

        using var inspect = await client.PostAsync("/api/inspect", Upload("same.pdf", "application/pdf"));
        var extractContent = Upload("same.pdf", "application/pdf");
        extractContent.Add(new StringContent("true"), "noLlm");
        using var extract = await client.PostAsync("/api/extract", extractContent);

        var inspected = JsonDocument.Parse(await inspect.Content.ReadAsStringAsync()).RootElement
            .GetProperty("sourceType").GetString();
        var extracted = LastResult(await extract.Content.ReadAsStringAsync())
            .GetProperty("sourceType").GetString();

        Assert.Equal("pdf", inspected);
        Assert.Equal(inspected, extracted);
    }

    private static JsonElement LastResult(string ndjson)
    {
        var result = ndjson
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .LastOrDefault(node => node.TryGetProperty("type", out var type) && type.GetString() == "result");
        Assert.True(result.ValueKind != JsonValueKind.Undefined, $"no result event in:\n{ndjson}");
        return result;
    }

    private static MultipartFormDataContent Upload(string name, string contentType)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(File.ReadAllBytes(
            Path.Combine(TestRepository.Root(), Pdf.Replace('/', Path.DirectorySeparatorChar))));
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(file, "file", name);
        return content;
    }

}
