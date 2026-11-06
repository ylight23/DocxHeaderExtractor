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
    public async Task Editing_parent_creates_unreviewed_version_and_rejects_stale_edit()
    {
        var root = Path.Combine(Path.GetTempPath(), "dhx-parent-api-" + Guid.NewGuid().ToString("N"));
        try
        {
            static DocxHeaderExtractor.DocumentProcessing.Output.HeadingRecord Bound(int i, int level, string text) => new()
            {
                Index = i, Level = level, Text = text, StableId = $"body[1]/p[{i + 1}]", HeadingSpan = new(0, text.Length),
                SourceParts = [new($"body[1]/p[{i + 1}]", i, new(0, text.Length), text)], ValidatedSourcePartCount = 1,
            };
            var hs = new[] { Bound(0, 1, "Kế hoạch"), Bound(1, 2, "Công tác xã hội"), Bound(2, 2, "Duy trì") };
            var store = new DocxHeaderExtractor.DocumentProcessing.Gather.OutlineStore(root);
            var paragraphs = hs.Select(h => new DocxHeaderExtractor.DocumentProcessing.Gather.StoredParagraph(h.StableId!, h.Index, "sig")).ToArray();
            store.Store(new(DocxHeaderExtractor.DocumentProcessing.Gather.OutlineStore.Version, "sha", "config", "f.docx", DateTimeOffset.UtcNow, paragraphs, [], hs));
            var old = store.Find("sha", "config")!;
            using var app = new WebApplicationFactory<WebProgram>().WithWebHostBuilder(b => b.ConfigureTestServices(s =>
                s.AddSingleton(new WebApp::DocxHeaderExtractor.Web.Endpoints.OutlineRoot(root))));
            using var client = app.CreateClient();
            var payload = JsonSerializer.Serialize(new { sourceSha256 = "sha", configKey = "config",
                outlineSha256 = DocxHeaderExtractor.DocumentProcessing.Gather.OutlineApprovals.OutlineHash(old), location = hs[2].StableId, parentLocation = hs[1].StableId, reviewer = "reviewer" });
            StringContent Body() => new(payload, System.Text.Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("/api/admin/outline/parent", Body());
            response.EnsureSuccessStatusCode();
            var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("UNREVIEWED", result.GetProperty("state").GetString());
            var current = store.Find("sha", "config")!;
            Assert.Equal(hs[1].StableId, DocxHeaderExtractor.DocumentProcessing.Gather.OutlineDiff.Parents(current.Headings)[hs[2].StableId!]);
            using var stale = await client.PostAsync("/api/admin/outline/parent", Body());
            Assert.Equal(System.Net.HttpStatusCode.Conflict, stale.StatusCode);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

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
        var admin = File.ReadAllText(TestRepository.Path("src/DocxHeaderExtractor.Web/wwwroot/admin.html"));
        Assert.Contains("file sửa sẽ trích lại", admin, StringComparison.Ordinal);
    }

    static string P(string text) => $"<w:p><w:r><w:t xml:space=\"preserve\">{text}</w:t></w:r></w:p>";

    static string Outline(string file, params (int Index, int Level, string Text)[] headings) => JsonSerializer.Serialize(new
    {
        file,
        headings = headings.Select(h => new { index = h.Index, level = h.Level, text = h.Text, stableId = $"body[1]/p[{h.Index + 1}]" }),
    });

    static void Add(MultipartFormDataContent form, string field, byte[] content, string name) => form.Add(new ByteArrayContent(content), field, name);

    /// <summary>The text of the downloaded DOCX, one paragraph per line.</summary>
    static async Task<string> BodyText(HttpClient client, string url)
    {
        var bytes = await client.GetByteArrayAsync(url);
        using var stream = new MemoryStream(bytes);
        using var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(stream, false);
        return string.Join("\n", doc.MainDocumentPart!.Document!.Body!.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>().Select(p => p.InnerText));
    }

    static JsonElement[] Events(string body) =>
        body.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToArray();

    [Fact]
    public async Task Uploaded_outline_without_explicit_coverage_attestation_is_not_auto_placed()
    {
        var target = RawDocxFixtureTests.Build(P("Mục A") + P("Đích"));
        var source = RawDocxFixtureTests.Build(P("Mục A") + P("Thông tin nguồn"));
        try
        {
            using var app = new WebApplicationFactory<WebProgram>().WithWebHostBuilder(b => b.ConfigureTestServices(s =>
                s.AddSingleton<ModelProviderFactory>(_ => () => new OpenRouterProvider(null))));
            using var client = app.CreateClient();
            using var form = new MultipartFormDataContent();
            Add(form, "target", File.ReadAllBytes(target), "target.docx");
            Add(form, "sources", File.ReadAllBytes(source), "source.docx");
            Add(form, "outlines", System.Text.Encoding.UTF8.GetBytes(Outline("target.docx", (0, 1, "Mục A"))), "target.json");
            Add(form, "outlines", System.Text.Encoding.UTF8.GetBytes(Outline("source.docx", (0, 1, "Mục A"))), "source.json");
            form.Add(new StringContent("1"), "noLlm");
            using var response = await client.PostAsync("/api/admin/gather", form);
            var result = Events(await response.Content.ReadAsStringAsync()).Single(e => e.GetProperty("type").GetString() == "result");
            Assert.Empty(result.GetProperty("merged").EnumerateArray());
            Assert.Equal("TARGET_OUTLINE_INCOMPLETE", Assert.Single(result.GetProperty("uncertain").EnumerateArray()).GetProperty("kind").GetString());
            var body = await BodyText(client, result.GetProperty("download").GetString()!);
            var appendix = body.IndexOf("PHỤ LỤC — NỘI DUNG CHƯA GHÉP ĐƯỢC", StringComparison.Ordinal);
            Assert.True(appendix >= 0 && body.IndexOf("Thông tin nguồn", StringComparison.Ordinal) > appendix);
        }
        finally { File.Delete(target); File.Delete(source); }
    }

    [Fact]
    public async Task A_source_without_headings_is_kept_in_the_appendix_on_export_and_apply()
    {
        var target = RawDocxFixtureTests.Build(P("Mục A") + P("Đích"));
        var source = RawDocxFixtureTests.Build(P("Thông tin nguồn không có heading"));
        try
        {
            using (var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(source, true))
            {
                var footer = doc.MainDocumentPart!.AddNewPart<DocumentFormat.OpenXml.Packaging.FooterPart>();
                footer.Footer = new DocumentFormat.OpenXml.Wordprocessing.Footer(new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                    new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text("Nội dung chân trang"))));
            }
            using var app = new WebApplicationFactory<WebProgram>().WithWebHostBuilder(b => b.ConfigureTestServices(s =>
                s.AddSingleton<ModelProviderFactory>(_ => () => new OpenRouterProvider(null))));
            using var client = app.CreateClient();
            using var form = new MultipartFormDataContent();
            Add(form, "target", File.ReadAllBytes(target), "target.docx");
            Add(form, "sources", File.ReadAllBytes(source), "source.docx");
            Add(form, "outlines", System.Text.Encoding.UTF8.GetBytes(Outline("target.docx", (0, 1, "Mục A"))), "target.json");
            Add(form, "outlines", System.Text.Encoding.UTF8.GetBytes(Outline("source.docx")), "source.json");
            form.Add(new StringContent("1"), "noLlm"); form.Add(new StringContent("1"), "outlineCoverageVerified");
            using var response = await client.PostAsync("/api/admin/gather", form);
            var events = Events(await response.Content.ReadAsStringAsync());
            Assert.DoesNotContain(events, e => e.GetProperty("type").GetString() == "error");
            var result = events.Single(e => e.GetProperty("type").GetString() == "result");
            var held = Assert.Single(result.GetProperty("uncertain").EnumerateArray());
            Assert.Equal("UNCONFIRMED_HIERARCHY", held.GetProperty("kind").GetString());
            Assert.Contains(held.GetProperty("facts").EnumerateArray(), f => f.GetProperty("text").GetString() == "Thông tin nguồn không có heading");
            async Task CheckBody(string url)
            {
                var body = await BodyText(client, url);
                var appendix = body.IndexOf("PHỤ LỤC — NỘI DUNG CHƯA GHÉP ĐƯỢC", StringComparison.Ordinal);
                Assert.True(appendix >= 0);
                Assert.True(body.IndexOf("Thông tin nguồn không có heading", StringComparison.Ordinal) > appendix);
                Assert.Contains("Chân trang chưa được đọc đầy đủ", body);
            }
            await CheckBody(result.GetProperty("download").GetString()!);
            using var apply = await client.PostAsync($"/api/admin/gather/{result.GetProperty("runId").GetString()}/apply",
                new StringContent("""{"decisions":[]}""", System.Text.Encoding.UTF8, "application/json"));
            apply.EnsureSuccessStatusCode();
            await CheckBody(JsonDocument.Parse(await apply.Content.ReadAsStringAsync()).RootElement.GetProperty("download").GetString()!);
        }
        finally { File.Delete(target); File.Delete(source); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Incomplete_cached_selection_never_places_source_in_body(bool targetIncomplete)
    {
        var target = RawDocxFixtureTests.Build(P("Mục A") + P("Đích"));
        var source = RawDocxFixtureTests.Build(P("Mục A") + P("Nội dung nguồn"));
        var root = Path.Combine(Path.GetTempPath(), "dhx-coverage-web-" + Guid.NewGuid().ToString("N"));
        try
        {
            var provider = new OpenRouterProvider(null);
            var options = RequestOptions.Build(new Microsoft.AspNetCore.Http.FormCollection(
                new Dictionary<string, Microsoft.Extensions.Primitives.StringValues> { ["noLlm"] = "1" }), provider, out _);
            var key = OutlineReuse.ConfigKey(options);
            var store = new DocxHeaderExtractor.DocumentProcessing.Gather.OutlineStore(root);
            void Save(string path, string name, bool complete)
            {
                var version = DocxHeaderExtractor.Sources.Docx.Changes.DocxVersionReader.Read(path);
                var heading = new DocxHeaderExtractor.DocumentProcessing.Output.HeadingRecord
                    { Index = 0, Level = 1, Text = "Mục A", StableId = "body[1]/p[1]" };
                store.Store(new(DocxHeaderExtractor.DocumentProcessing.Gather.OutlineStore.Version, version.FileSha256, key, name,
                    DateTimeOffset.UtcNow, DocxHeaderExtractor.DocumentProcessing.Gather.OutlineStore.Paragraphs(version), [], [heading],
                    SelectionComplete: complete));
            }
            Save(target, "target.docx", !targetIncomplete);
            Save(source, "source.docx", targetIncomplete);
            using var app = new WebApplicationFactory<WebProgram>().WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            {
                s.AddSingleton(new WebApp::DocxHeaderExtractor.Web.Endpoints.OutlineRoot(root));
                s.AddSingleton<ModelProviderFactory>(_ => () => provider);
            }));
            using var client = app.CreateClient();
            using var form = new MultipartFormDataContent();
            Add(form, "target", File.ReadAllBytes(target), "target.docx");
            Add(form, "sources", File.ReadAllBytes(source), "source.docx");
            form.Add(new StringContent("1"), "noLlm"); form.Add(new StringContent("1"), "outlineCoverageVerified");
            form.Add(new StringContent("1"), "outlineCache");
            using var response = await client.PostAsync("/api/admin/gather", form);
            var events = Events(await response.Content.ReadAsStringAsync());
            Assert.DoesNotContain(events, e => e.GetProperty("type").GetString() == "error");
            var result = events.Single(e => e.GetProperty("type").GetString() == "result");
            Assert.Empty(result.GetProperty("merged").EnumerateArray());
            var held = Assert.Single(result.GetProperty("uncertain").EnumerateArray());
            Assert.Equal(targetIncomplete ? "TARGET_OUTLINE_INCOMPLETE" : "UNCONFIRMED_HIERARCHY", held.GetProperty("kind").GetString());
            var body = await BodyText(client, result.GetProperty("download").GetString()!);
            var appendix = body.IndexOf("PHỤ LỤC — NỘI DUNG CHƯA GHÉP ĐƯỢC", StringComparison.Ordinal);
            Assert.True(appendix >= 0 && body.IndexOf("Nội dung nguồn", StringComparison.Ordinal) > appendix);
            Assert.Contains("Chưa kiểm tra hết các đoạn để xác định mục", body);
        }
        finally { File.Delete(target); File.Delete(source); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Export_preserves_unread_gap_even_when_no_section_has_facts()
    {
        var target = RawDocxFixtureTests.Build(P("Văn bản đích"));
        var output = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".docx");
        try
        {
            DocxHeaderExtractor.DocumentProcessing.Gather.GatherWriteback.Export(target, output, [], [], DateTime.UtcNow,
                gaps: [new("nguồn A", "body[1]/tbl[1]", "merged_cells")]);
            using var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(output, false);
            var text = doc.MainDocumentPart!.Document!.Body!.InnerText;
            Assert.Contains("PHỤ LỤC — NỘI DUNG CHƯA GHÉP ĐƯỢC", text);
            Assert.Contains("nguồn A body[1]/tbl[1]", text);
            Assert.Contains("Ô bảng gộp chưa được đọc đầy đủ", text);
            Assert.DoesNotContain("merged_cells", text);
            await Task.CompletedTask;
        }
        finally { File.Delete(target); File.Delete(output); }
    }

    [Fact]
    public async Task Source_text_before_first_heading_is_in_export_and_reexport()
    {
        var target = RawDocxFixtureTests.Build(P("Mục A") + P("Nội dung đích"));
        var source = RawDocxFixtureTests.Build(P("Đoạn mở đầu nguồn") + P("Mục A") + P("Nội dung nguồn"));
        try
        {
            using var app = new WebApplicationFactory<WebProgram>().WithWebHostBuilder(b => b.ConfigureTestServices(s =>
                s.AddSingleton<ModelProviderFactory>(_ => () => new OpenRouterProvider(null))));
            using var client = app.CreateClient();
            using var form = new MultipartFormDataContent();
            Add(form, "target", File.ReadAllBytes(target), "target.docx");
            Add(form, "sources", File.ReadAllBytes(source), "source.docx");
            Add(form, "outlines", System.Text.Encoding.UTF8.GetBytes(Outline("target.docx", (0, 1, "Mục A"))), "target.json");
            Add(form, "outlines", System.Text.Encoding.UTF8.GetBytes(Outline("source.docx", (1, 1, "Mục A"))), "source.json");
            form.Add(new StringContent("1"), "noLlm"); form.Add(new StringContent("1"), "outlineCoverageVerified");
            using var response = await client.PostAsync("/api/admin/gather", form);
            var events = Events(await response.Content.ReadAsStringAsync());
            Assert.DoesNotContain(events, e => e.GetProperty("type").GetString() == "error");
            var result = events.Single(e => e.GetProperty("type").GetString() == "result");
            async Task Check(string url)
            {
                var body = await BodyText(client, url);
                var appendix = body.IndexOf("PHỤ LỤC — NỘI DUNG CHƯA GHÉP ĐƯỢC", StringComparison.Ordinal);
                Assert.True(appendix >= 0);
                Assert.True(body.IndexOf("Đoạn mở đầu nguồn", StringComparison.Ordinal) > appendix);
            }
            await Check(result.GetProperty("download").GetString()!);
            using var apply = await client.PostAsync($"/api/admin/gather/{result.GetProperty("runId").GetString()}/apply",
                new StringContent("""{"decisions":[]}""", System.Text.Encoding.UTF8, "application/json"));
            apply.EnsureSuccessStatusCode();
            await Check(JsonDocument.Parse(await apply.Content.ReadAsStringAsync()).RootElement.GetProperty("download").GetString()!);
        }
        finally { File.Delete(target); File.Delete(source); }
    }

    [Fact]
    public async Task New_source_heading_is_held_with_its_text_instead_of_automatic_output()
    {
        var target = RawDocxFixtureTests.Build(P("Kế hoạch") + P("Nội dung đích"));
        var source = RawDocxFixtureTests.Build(P("Kế hoạch") + P("Dinh dưỡng") + P("Nội dung nguồn"));
        try
        {
            using var app = new WebApplicationFactory<WebProgram>().WithWebHostBuilder(b => b.ConfigureTestServices(s =>
                s.AddSingleton<ModelProviderFactory>(_ => () => new OpenRouterProvider(null))));
            using var client = app.CreateClient(); using var form = new MultipartFormDataContent();
            Add(form, "target", File.ReadAllBytes(target), "target.docx"); Add(form, "sources", File.ReadAllBytes(source), "source.docx");
            Add(form, "outlines", System.Text.Encoding.UTF8.GetBytes(Outline("target.docx", (0, 1, "Kế hoạch"))), "target.json");
            Add(form, "outlines", System.Text.Encoding.UTF8.GetBytes(Outline("source.docx", (0, 1, "Kế hoạch"), (1, 2, "Dinh dưỡng"))), "source.json");
            form.Add(new StringContent("1"), "noLlm"); form.Add(new StringContent("1"), "outlineCoverageVerified");
            using var response = await client.PostAsync("/api/admin/gather", form);
            var events = Events(await response.Content.ReadAsStringAsync());
            Assert.DoesNotContain(events, x => x.GetProperty("type").GetString() == "error");
            var result = events.Single(x => x.GetProperty("type").GetString() == "result");
            Assert.Empty(result.GetProperty("added").EnumerateArray());
            var held = Assert.Single(result.GetProperty("uncertain").EnumerateArray());
            Assert.Equal("NEW_HEADING_REVIEW", held.GetProperty("kind").GetString());
            Assert.Equal("Mục này chưa có trong văn bản đích", held.GetProperty("plainReason").GetString());
            Assert.Contains(held.GetProperty("facts").EnumerateArray(), x => x.GetProperty("text").GetString() == "Nội dung nguồn");
            // The new heading is not written into the body: its text is in the appendix of the export.
            var body = await BodyText(client, result.GetProperty("download").GetString()!);
            var appendix = body[body.IndexOf("PHỤ LỤC — NỘI DUNG CHƯA GHÉP ĐƯỢC", StringComparison.Ordinal)..];
            Assert.Contains("Nội dung nguồn", appendix);
            Assert.Contains("Dinh dưỡng", appendix);
        }
        finally { File.Delete(target); File.Delete(source); }
    }

    [Fact]
    public async Task Named_history_reports_new_evidence_and_changed_target_without_carrying_decisions()
    {
        var target = RawDocxFixtureTests.Build(P("Mục A") + P("Cũ"));
        var changedTarget = RawDocxFixtureTests.Build(P("Mục A") + P("Đích đổi"));
        var source = RawDocxFixtureTests.Build(P("Mục A") + P("Nguồn cũ"));
        var changedSource = RawDocxFixtureTests.Build(P("Mục A") + P("Nguồn cũ") + P("Nguồn mới"));
        var root = Path.Combine(Path.GetTempPath(), "dhx-impact-web-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var app = new WebApplicationFactory<WebProgram>().WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            {
                s.AddSingleton(new WebApp::DocxHeaderExtractor.Web.Endpoints.OutlineRoot(root));
                s.AddSingleton<ModelProviderFactory>(_ => () => new OpenRouterProvider(null));
            }));
            using var client = app.CreateClient();
            async Task<JsonElement> Run(string t, string s)
            {
                using var form = new MultipartFormDataContent();
                Add(form, "target", File.ReadAllBytes(t), "target.docx");
                Add(form, "sources", File.ReadAllBytes(s), "source.docx");
                Add(form, "outlines", System.Text.Encoding.UTF8.GetBytes(Outline("target.docx", (0, 1, "Mục A"))), "target.json");
                Add(form, "outlines", System.Text.Encoding.UTF8.GetBytes(Outline("source.docx", (0, 1, "Mục A"))), "source.json");
                form.Add(new StringContent("1"), "noLlm"); form.Add(new StringContent("1"), "outlineCoverageVerified"); form.Add(new StringContent("test-history"), "historyKey");
                using var response = await client.PostAsync("/api/admin/gather", form);
                var events = Events(await response.Content.ReadAsStringAsync());
                Assert.DoesNotContain(events, e => e.GetProperty("type").GetString() == "error");
                return events.Single(e => e.GetProperty("type").GetString() == "result");
            }
            var first = await Run(target, source);
            Assert.Equal("BASELINE_SAVED", first.GetProperty("history").GetProperty("status").GetString());
            var second = await Run(changedTarget, changedSource);
            var history = second.GetProperty("history");
            Assert.Equal("COMPARED", history.GetProperty("status").GetString());
            var impact = history.GetProperty("impact");
            Assert.True(impact.GetProperty("targetChanged").GetBoolean());
            Assert.Contains(impact.GetProperty("items").EnumerateArray(), i => i.GetProperty("kind").GetString() == "NEW_OR_UNMATCHED_EVIDENCE"
                && i.GetProperty("after").GetProperty("fact").GetProperty("parts")[0].GetProperty("text").GetString() == "Nguồn mới");
            Assert.False(second.TryGetProperty("targetReviewRequired", out _));
            Assert.NotNull(second.GetProperty("download").GetString());
        }
        finally
        {
            foreach (var p in new[] { target, changedTarget, source, changedSource }) File.Delete(p);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task The_admin_page_exports_at_once_and_keeps_every_unplaced_paragraph_in_the_appendix()
    {
        var target = RawDocxFixtureTests.Build(P("Mục A:") + P("Đã làm A.") + P("Mục con một") + P("Đích 66."));
        var source = RawDocxFixtureTests.Build(P("MỤC A:") + P("Đã làm B.") + P("Mục con mot") + P("Nguồn 50."));
        var targetBytes = File.ReadAllBytes(target);
        try
        {
            using var app = new WebApplicationFactory<WebProgram>().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                services.AddSingleton<ModelProviderFactory>(_ => () => new OpenRouterProvider(null))));
            using var client = app.CreateClient();
            var page = await client.GetStringAsync("/admin.html");
            Assert.Contains("Nội dung chưa rõ vị trí", page);
            Assert.DoesNotContain("targetReviewRequired", page);
            MultipartFormDataContent Form()
            {
                var form = new MultipartFormDataContent();
                Add(form, "target", targetBytes, "10.docx");
                Add(form, "sources", File.ReadAllBytes(source), "08.docx");
                Add(form, "outlines", System.Text.Encoding.UTF8.GetBytes(Outline("10.docx", (0, 1, "Mục A:"), (2, 2, "Mục con một"))), "10.json");
                Add(form, "outlines", System.Text.Encoding.UTF8.GetBytes(Outline("08.docx", (0, 1, "MỤC A:"), (2, 2, "Mục con mot"))), "08.json");
                form.Add(new StringContent("1"), "noLlm"); form.Add(new StringContent("1"), "outlineCoverageVerified");
                return form;
            }
            using var form = Form();
            using var response = await client.PostAsync("/api/admin/gather", form);
            var events = Events(await response.Content.ReadAsStringAsync());
            Assert.DoesNotContain(events, e => e.GetProperty("type").GetString() == "error");
            var result = events.Single(e => e.GetProperty("type").GetString() == "result");
            Assert.False(result.GetProperty("modelClassified").GetBoolean());

            // No outline review: the export exists at once. Each source paragraph is in the body, in the appendix, or listed as not added.
            var body = await BodyText(client, result.GetProperty("download").GetString()!);
            var excluded = result.GetProperty("excluded").EnumerateArray().Select(e => e.GetProperty("text").GetString()).ToHashSet();
            foreach (var paragraph in new[] { "Đã làm B.", "Nguồn 50." })
                Assert.True(body.Contains(paragraph, StringComparison.Ordinal) || excluded.Contains(paragraph), paragraph);
            var uncertain = result.GetProperty("uncertain").EnumerateArray().ToArray();
            Assert.NotEmpty(uncertain);
            var appendixAt = body.IndexOf("PHỤ LỤC — NỘI DUNG CHƯA GHÉP ĐƯỢC", StringComparison.Ordinal);
            Assert.True(appendixAt >= 0);
            foreach (var u in uncertain)
            {
                Assert.False(string.IsNullOrWhiteSpace(u.GetProperty("plainReason").GetString()));
                foreach (var fact in u.GetProperty("facts").EnumerateArray())
                {
                    var text = fact.GetProperty("text").GetString()!;
                    Assert.True(body.IndexOf(text, appendixAt, StringComparison.Ordinal) > appendixAt, text);
                    Assert.True(body.IndexOf(text, StringComparison.Ordinal) > appendixAt, "uncertain text written before the appendix: " + text);
                }
            }
            Assert.DoesNotContain("NEAR_NAME", body);

            // The user's choices: a skipped item stays in the appendix, labelled; an item placed by the user leaves the appendix.
            var runId = result.GetProperty("runId").GetString();
            using var skip = await client.PostAsync($"/api/admin/gather/{runId}/apply", new StringContent(
                """{"decisions":[{"index":0,"decision":"SKIP"}]}""", System.Text.Encoding.UTF8, "application/json"));
            skip.EnsureSuccessStatusCode();
            var skipped = await BodyText(client, JsonDocument.Parse(await skip.Content.ReadAsStringAsync()).RootElement.GetProperty("download").GetString()!);
            Assert.Contains("Người dùng chọn bỏ qua", skipped);
            if (uncertain[0].GetProperty("candidates").GetArrayLength() > 0)
            {
                using var merge = await client.PostAsync($"/api/admin/gather/{runId}/apply", new StringContent(
                    """{"decisions":[{"index":0,"decision":"MERGE","candidate":0}]}""", System.Text.Encoding.UTF8, "application/json"));
                merge.EnsureSuccessStatusCode();
                var merged = await BodyText(client, JsonDocument.Parse(await merge.Content.ReadAsStringAsync()).RootElement.GetProperty("download").GetString()!);
                var first = uncertain[0].GetProperty("facts")[0].GetProperty("text").GetString()!;
                var at = merged.IndexOf("PHỤ LỤC — NỘI DUNG CHƯA GHÉP ĐƯỢC", StringComparison.Ordinal);
                var placed = merged.IndexOf(first, StringComparison.Ordinal);
                Assert.True(placed >= 0 && (at < 0 || placed < at), first);
            }
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
            form.Add(new StringContent("1"), "noLlm"); form.Add(new StringContent("1"), "outlineCoverageVerified");

            using var response = await client.PostAsync("/api/admin/gather", form);
            var error = Events(await response.Content.ReadAsStringAsync()).Single(e => e.GetProperty("type").GetString() == "error");

            Assert.Contains("10.docx, 08.docx", error.GetProperty("message").GetString());
        }
        finally { File.Delete(target); }
    }

    [Fact]
    public async Task The_review_endpoint_records_verdicts_only_for_the_exact_stored_outline()
    {
        var root = Path.Combine(Path.GetTempPath(), "dhx-review-" + Guid.NewGuid().ToString("N"));
        try
        {
            static DocxHeaderExtractor.DocumentProcessing.Output.HeadingRecord Bound(int index, string text, int level) => new()
            {
                Index = index, Level = level, Text = text, StableId = $"body[1]/p[{index + 1}]", HeadingSpan = new(0, text.Length),
                SourceParts = [new($"body[1]/p[{index + 1}]", index, new(0, text.Length), text)], ValidatedSourcePartCount = 1,
            };
            var store = new DocxHeaderExtractor.DocumentProcessing.Gather.OutlineStore(root);
            var paragraphs = new[] { "A", "nội dung", "B" }.Select((s, i) => new DocxHeaderExtractor.DocumentProcessing.Gather.StoredParagraph($"body[1]/p[{i + 1}]", i, s)).ToArray();
            store.Store(new(DocxHeaderExtractor.DocumentProcessing.Gather.OutlineStore.Version, "sha-file", "k", "f.docx", DateTimeOffset.UtcNow, paragraphs, [],
                [Bound(0, "A", 1), Bound(2, "B", 2)]));
            var outline = store.Find("sha-file", "k")!;
            var hash = DocxHeaderExtractor.DocumentProcessing.Gather.OutlineApprovals.OutlineHash(outline);

            using var app = new WebApplicationFactory<WebProgram>().WithWebHostBuilder(b => b.ConfigureTestServices(s =>
                s.AddSingleton(new WebApp::DocxHeaderExtractor.Web.Endpoints.OutlineRoot(root))));
            using var client = app.CreateClient();
            Assert.Contains("Duyệt outline", await client.GetStringAsync("/admin.html"));
            StringContent Body(object o) => new(JsonSerializer.Serialize(o), System.Text.Encoding.UTF8, "application/json");

            using var stale = await client.PostAsync("/api/admin/outline/review", Body(new { sourceSha256 = "sha-file", configKey = "k",
                outlineSha256 = "other", reviewer = "r", items = Array.Empty<object>(), complete = true }));
            Assert.Equal(System.Net.HttpStatusCode.Conflict, stale.StatusCode);
            using var missing = await client.PostAsync("/api/admin/outline/review", Body(new { sourceSha256 = "none", configKey = "k",
                outlineSha256 = hash, reviewer = "r", items = Array.Empty<object>(), complete = false }));
            Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);

            using var partial = await client.PostAsync("/api/admin/outline/review", Body(new { sourceSha256 = "sha-file", configKey = "k",
                outlineSha256 = hash, reviewer = "r", items = new object[] { new { location = "body[1]/p[1]", kind = "HEADING" },
                    new { location = "body[1]/p[2]", kind = "HEADING" } }, complete = false }));
            var first = JsonDocument.Parse(await partial.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("PARTIAL", first.GetProperty("state").GetString());
            Assert.Equal("NOT_A_HEADING_IN_THIS_OUTLINE", Assert.Single(first.GetProperty("refusals").EnumerateArray()).GetProperty("reason").GetString());

            using var done = await client.PostAsync("/api/admin/outline/review", Body(new { sourceSha256 = "sha-file", configKey = "k",
                outlineSha256 = hash, reviewer = "r", items = new object[] { new { location = "body[1]/p[3]", kind = "HEADING" } }, complete = true }));
            Assert.Equal("REVIEWED", JsonDocument.Parse(await done.Content.ReadAsStringAsync()).RootElement.GetProperty("state").GetString());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Review_projection_keeps_every_heading_inside_a_level_shift_group()
    {
        var path = RawDocxFixtureTests.Build(P("A") + P("B") + P("C") + P("D"));
        var root = Path.Combine(Path.GetTempPath(), "dhx-review-group-" + Guid.NewGuid().ToString("N"));
        try
        {
            var version = DocxHeaderExtractor.Sources.Docx.Changes.DocxVersionReader.Read(path);
            var paragraphs = DocxHeaderExtractor.DocumentProcessing.Gather.OutlineStore.Paragraphs(version);
            DocxHeaderExtractor.DocumentProcessing.Output.HeadingRecord H(int i, int level) => new()
            {
                Index = i, Level = level, Text = version.Paragraphs[i].RawText, StableId = paragraphs[i].Location,
                HeadingSpan = new(0, version.Paragraphs[i].RawText.Length), ValidatedSourcePartCount = 1,
                SourceParts = [new(paragraphs[i].Location, i, new(0, version.Paragraphs[i].RawText.Length), version.Paragraphs[i].RawText)]
            };
            var previous = new DocxHeaderExtractor.DocumentProcessing.Gather.StoredOutline("OUTLINE_STORE_V1", "sha", "k", "f.docx", DateTimeOffset.UtcNow,
                paragraphs, [], [H(0, 1), H(1, 2), H(2, 2), H(3, 2)]);
            var current = previous with { Headings = [H(0, 1), H(1, 3), H(2, 3), H(3, 3)] };
            var reuse = new OutlineReuseResult(current.Headings, "STORED", null, []) { Current = current, Previous = previous };
            var type = typeof(WebProgram).Assembly.GetType("DocxHeaderExtractor.Web.Endpoints.AdminOutlineReview", throwOnError: true)!;
            var projected = type.GetMethod("Project")!.Invoke(null, [reuse, path, new DocxHeaderExtractor.DocumentProcessing.Gather.OutlineApprovals(root)]);
            var json = JsonSerializer.SerializeToElement(projected, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var group = Assert.Single(json.GetProperty("differences").EnumerateArray(), d => d.GetProperty("kind").GetString() == "LEVEL_SHIFT");
            Assert.True(group.GetProperty("uncertain").GetBoolean());
            var details = group.GetProperty("details").EnumerateArray().ToArray();
            Assert.Equal(3, details.Length);
            Assert.All(details, d => { Assert.True(d.GetProperty("newIsHeading").GetBoolean()); Assert.NotEqual(JsonValueKind.Null, d.GetProperty("newLocation").ValueKind); });
            var store = new DocxHeaderExtractor.DocumentProcessing.Gather.OutlineStore(root);
            Assert.NotNull(store.Store(current));
            using var app = new WebApplicationFactory<WebProgram>().WithWebHostBuilder(b => b.ConfigureTestServices(s =>
                s.AddSingleton(new WebApp::DocxHeaderExtractor.Web.Endpoints.OutlineRoot(root))));
            using var client = app.CreateClient();
            var html = await client.GetStringAsync("/admin.html");
            Assert.Contains("x.details", html, StringComparison.Ordinal);
            var body = JsonSerializer.Serialize(new { sourceSha256 = "sha", configKey = "k", outlineSha256 = json.GetProperty("outlineSha256").GetString(),
                reviewer = "reviewer", items = new[] { new { location = details[0].GetProperty("newLocation").GetString(), kind = "HEADING" } }, complete = false });
            using var response = await client.PostAsync("/api/admin/outline/review", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
            response.EnsureSuccessStatusCode();
            var recorded = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(1, recorded.GetProperty("recorded").GetInt32());
            Assert.Empty(recorded.GetProperty("refusals").EnumerateArray());
            Assert.Equal("PARTIAL", recorded.GetProperty("state").GetString());
        }
        finally { File.Delete(path); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
