extern alias WebApp;

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The Web host routes on the uploaded bytes: a PDF uploaded to <c>/api/extract</c> runs the PDF
/// lane instead of being refused by the DOCX converter on its extension.
/// </summary>
public sealed class WebPdfRouteAgreementTests : IClassFixture<WebApplicationFactory<WebApp::Program>>
{
    private const string Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";

    private readonly WebApplicationFactory<WebApp::Program> _factory;

    public WebPdfRouteAgreementTests(WebApplicationFactory<WebApp::Program> factory) => _factory = factory;

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
