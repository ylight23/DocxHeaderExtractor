extern alias WebApp;

using System.Text.Json;
using DocxHeaderExtractor.App;
using DocxHeaderExtractor.Web;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using WebProgram = WebApp::Program;

namespace DocxHeaderExtractor.Tests;

/// <summary>The admin page's endpoint: gather by heading chain with uploaded outlines, uncertain sections reported and never written.</summary>
[Trait("Category", "Slow")]
public sealed class AdminGatherWebTests
{
    [Fact]
    public void Docx_cache_requires_an_explicit_form_opt_in()
    {
        var provider = new OpenRouterProvider(null);
        var fields = new Dictionary<string, Microsoft.Extensions.Primitives.StringValues> { ["noLlm"] = "1" };
        Assert.False(RequestOptions.Build(new Microsoft.AspNetCore.Http.FormCollection(fields), provider, out _).DocxReuseSelection);
        fields["docxReuseSelection"] = "1";
        Assert.True(RequestOptions.Build(new Microsoft.AspNetCore.Http.FormCollection(fields), provider, out _).DocxReuseSelection);
    }

    [Fact]
    public void Both_web_pages_offer_an_unchecked_cache_opt_in_and_submit_the_same_form_key()
    {
        foreach (var name in new[] { "index.html", "admin.html" })
        {
            var html = File.ReadAllText(TestRepository.Path("src/DocxHeaderExtractor.Web/wwwroot/" + name));
            Assert.Contains("id=\"docxReuseSelection\"", html, StringComparison.Ordinal);
            Assert.DoesNotContain("id=\"docxReuseSelection\" checked", html, StringComparison.Ordinal);
            Assert.Contains("fd.append('docxReuseSelection'", html, StringComparison.Ordinal);
        }
    }

    static string P(string text) => $"<w:p><w:r><w:t xml:space=\"preserve\">{text}</w:t></w:r></w:p>";

    static string Outline(string file, params (int Index, int Level, string Text)[] headings) => JsonSerializer.Serialize(new
    {
        file,
        headings = headings.Select(h => new { index = h.Index, level = h.Level, text = h.Text, stableId = $"body[1]/p[{h.Index + 1}]" }),
    });

    static void Add(MultipartFormDataContent form, string field, byte[] content, string name) => form.Add(new ByteArrayContent(content), field, name);

    static JsonElement[] Events(string body) =>
        body.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToArray();

    [Fact]
    public async Task The_admin_page_gathers_by_heading_chain_and_lists_the_uncertain_sections_without_a_model()
    {
        var target = RawDocxFixtureTests.Build(P("Công tác xã hội:") + P("Đã làm A.") + P("Công tác tiếp nhận và hỗ trợ từ thiện") + P("Tiếp đón 66 lượt."));
        var source = RawDocxFixtureTests.Build(P("CÔNG TÁC XÃ HỘI:") + P("Đã làm B.") + P("Tiếp nhận và hỗ trợ từ thiện") + P("Tiếp đón 50 lượt."));
        var targetBytes = File.ReadAllBytes(target);
        try
        {
            using var app = new WebApplicationFactory<WebProgram>().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                services.AddSingleton<ModelProviderFactory>(_ => () => new OpenRouterProvider(null))));
            using var client = app.CreateClient();
            Assert.Contains("Phần không chắc", await client.GetStringAsync("/admin.html"));
            using var form = new MultipartFormDataContent();
            Add(form, "target", targetBytes, "10.docx");
            Add(form, "sources", File.ReadAllBytes(source), "08.docx");
            Add(form, "outlines", System.Text.Encoding.UTF8.GetBytes(Outline("10.docx", (0, 1, "Công tác xã hội:"), (2, 2, "Công tác tiếp nhận và hỗ trợ từ thiện"))), "10.json");
            Add(form, "outlines", System.Text.Encoding.UTF8.GetBytes(Outline("08.docx", (0, 1, "CÔNG TÁC XÃ HỘI:"), (2, 2, "Tiếp nhận và hỗ trợ từ thiện"))), "08.json");
            form.Add(new StringContent("1"), "noLlm");

            using var response = await client.PostAsync("/api/admin/gather", form);
            var events = Events(await response.Content.ReadAsStringAsync());

            Assert.DoesNotContain(events, e => e.GetProperty("type").GetString() == "error");
            var result = events.Single(e => e.GetProperty("type").GetString() == "result");
            Assert.False(result.GetProperty("modelClassified").GetBoolean());
            var merged = Assert.Single(result.GetProperty("merged").EnumerateArray());
            Assert.Equal("Đã làm B.", Assert.Single(merged.GetProperty("facts").EnumerateArray()).GetProperty("text").GetString());
            Assert.Empty(result.GetProperty("added").EnumerateArray());
            var held = Assert.Single(result.GetProperty("uncertain").EnumerateArray());
            Assert.Equal("NEAR_NAME", held.GetProperty("kind").GetString());
            Assert.Equal("Công tác tiếp nhận và hỗ trợ từ thiện", Assert.Single(held.GetProperty("candidates").EnumerateArray()).GetProperty("heading").GetString());

            var download = await client.GetByteArrayAsync(result.GetProperty("download").GetString());
            var written = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".docx");
            try
            {
                File.WriteAllBytes(written, download);
                using var document = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(written, false);
                var text = document.MainDocumentPart!.Document!.Body!.InnerText;
                Assert.Contains("Đã làm B.", text);
                Assert.DoesNotContain("Tiếp đón 50 lượt.", text);
            }
            finally { File.Delete(written); }

            // The reviewer merges the uncertain section into the suggested heading; the summary is written again with it.
            var runId = result.GetProperty("runId").GetString();
            using var apply = await client.PostAsync($"/api/admin/gather/{runId}/apply", new StringContent(
                """{"decisions":[{"index":0,"decision":"MERGE","candidate":0}]}""", System.Text.Encoding.UTF8, "application/json"));
            apply.EnsureSuccessStatusCode();
            var applied = JsonDocument.Parse(await apply.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(1, Assert.Single(applied.GetProperty("outcomes").EnumerateArray()).GetProperty("written").GetInt32());
            Assert.Empty(applied.GetProperty("undecided").EnumerateArray());
            var reviewed = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".docx");
            try
            {
                File.WriteAllBytes(reviewed, await client.GetByteArrayAsync(applied.GetProperty("download").GetString()));
                using var document = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(reviewed, false);
                Assert.Contains("Tiếp đón 50 lượt.", document.MainDocumentPart!.Document!.Body!.InnerText);
            }
            finally { File.Delete(reviewed); }
            using var expired = await client.PostAsync($"/api/admin/gather/{Guid.NewGuid()}/apply",
                new StringContent("""{"decisions":[]}""", System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(System.Net.HttpStatusCode.NotFound, expired.StatusCode);
            Assert.Equal(targetBytes, File.ReadAllBytes(target));
        }
        finally { File.Delete(target); File.Delete(source); }
    }

    [Fact]
    public async Task Without_a_model_every_document_needs_an_outline()
    {
        var target = RawDocxFixtureTests.Build(P("A"));
        try
        {
            using var app = new WebApplicationFactory<WebProgram>();
            using var client = app.CreateClient();
            using var form = new MultipartFormDataContent();
            Add(form, "target", File.ReadAllBytes(target), "10.docx");
            Add(form, "sources", File.ReadAllBytes(target), "08.docx");
            form.Add(new StringContent("1"), "noLlm");

            using var response = await client.PostAsync("/api/admin/gather", form);
            var error = Events(await response.Content.ReadAsStringAsync()).Single(e => e.GetProperty("type").GetString() == "error");

            Assert.Contains("10.docx, 08.docx", error.GetProperty("message").GetString());
        }
        finally { File.Delete(target); }
    }
}
