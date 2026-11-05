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

    static JsonElement[] Events(string body) =>
        body.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToArray();

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
            form.Add(new StringContent("1"), "noLlm");
            using var response = await client.PostAsync("/api/admin/gather", form);
            var events = Events(await response.Content.ReadAsStringAsync());
            Assert.DoesNotContain(events, x => x.GetProperty("type").GetString() == "error");
            var result = events.Single(x => x.GetProperty("type").GetString() == "result");
            Assert.Empty(result.GetProperty("added").EnumerateArray());
            var held = Assert.Single(result.GetProperty("uncertain").EnumerateArray());
            Assert.Equal("NEW_HEADING_REVIEW", held.GetProperty("kind").GetString());
            Assert.Equal("Nội dung nguồn", Assert.Single(held.GetProperty("facts").EnumerateArray()).GetProperty("text").GetString());
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
                form.Add(new StringContent("1"), "noLlm"); form.Add(new StringContent("test-history"), "historyKey");
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
            Assert.True(second.GetProperty("targetReviewRequired").GetBoolean());
            Assert.False(second.TryGetProperty("download", out _));
        }
        finally
        {
            foreach (var p in new[] { target, changedTarget, source, changedSource }) File.Delete(p);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

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

            Assert.True(result.GetProperty("targetReviewRequired").GetBoolean());
            Assert.False(result.TryGetProperty("download", out _));

            // The separate demo is labelled, source-bound, and includes held content without approving or modifying the target.
            var demoUrl = result.GetProperty("demoDownload").GetString();
            Assert.NotNull(demoUrl);
            var demoBytes = await client.GetByteArrayAsync(demoUrl);
            using (var stream = new MemoryStream(demoBytes))
            using (var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(stream, false))
            {
                var body = doc.MainDocumentPart!.Document!.Body!;
                Assert.Contains("BẢN DEMO CHƯA DUYỆT", body.InnerText);
                Assert.Contains("Đã làm B.", body.InnerText);
                Assert.Contains("Tiếp đón 50 lượt.", body.InnerText);
                Assert.Contains("CHƯA XÁC ĐỊNH VỊ TRÍ", body.InnerText);
                Assert.Contains("nguồn:", body.InnerText);
                Assert.NotEmpty(body.Descendants<DocumentFormat.OpenXml.Wordprocessing.InsertedRun>());
            }

            // An unreviewed target remains a preview, even if the caller tries to apply an uncertain-section decision.
            var runId = result.GetProperty("runId").GetString();
            using var unapprovedDownload = await client.GetAsync($"/api/outline/{runId}.docx");
            Assert.Equal(System.Net.HttpStatusCode.NotFound, unapprovedDownload.StatusCode);
            using var apply = await client.PostAsync($"/api/admin/gather/{runId}/apply", new StringContent(
                """{"decisions":[{"index":0,"decision":"MERGE","candidate":0}]}""", System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(System.Net.HttpStatusCode.Conflict, apply.StatusCode);
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

    [Fact]
    public async Task Only_an_exact_previously_reviewed_target_can_be_written_and_revocation_blocks_apply()
    {
        var target = RawDocxFixtureTests.Build(P("Công tác xã hội:") + P("Đã làm A."));
        var source = RawDocxFixtureTests.Build(P("Công tác xã hội:") + P("Đã làm B."));
        var root = Path.Combine(Path.GetTempPath(), "dhx-approved-target-" + Guid.NewGuid().ToString("N"));
        try
        {
            var version = DocxHeaderExtractor.Sources.Docx.Changes.DocxVersionReader.Read(target);
            var paragraphs = DocxHeaderExtractor.DocumentProcessing.Gather.OutlineStore.Paragraphs(version);
            var text = version.Paragraphs[0].RawText;
            var heading = new DocxHeaderExtractor.DocumentProcessing.Output.HeadingRecord { Index = 0, Level = 1, Text = text,
                StableId = paragraphs[0].Location, HeadingSpan = new(0, text.Length), ValidatedSourcePartCount = 1,
                SourceParts = [new(paragraphs[0].Location, 0, new(0, text.Length), text)] };
            var provider = new OpenRouterProvider(null);
            var formFields = new Dictionary<string, Microsoft.Extensions.Primitives.StringValues> { ["noLlm"] = "1" };
            var options = RequestOptions.Build(new Microsoft.AspNetCore.Http.FormCollection(formFields), provider, out _);
            var key = OutlineReuse.ConfigKey(options);
            var store = new DocxHeaderExtractor.DocumentProcessing.Gather.OutlineStore(root);
            store.Store(new(DocxHeaderExtractor.DocumentProcessing.Gather.OutlineStore.Version, version.FileSha256, key, "10.docx", DateTimeOffset.UtcNow,
                paragraphs, [], [heading]));
            var saved = store.Find(version.FileSha256, key)!;
            var approvals = new DocxHeaderExtractor.DocumentProcessing.Gather.OutlineApprovals(root);

            using var app = new WebApplicationFactory<WebProgram>().WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            {
                s.AddSingleton(new WebApp::DocxHeaderExtractor.Web.Endpoints.OutlineRoot(root));
                s.AddSingleton<ModelProviderFactory>(_ => () => new OpenRouterProvider(null));
            }));
            using var client = app.CreateClient();
            MultipartFormDataContent Form()
            {
                var form = new MultipartFormDataContent();
                Add(form, "target", File.ReadAllBytes(target), "10.docx");
                Add(form, "sources", File.ReadAllBytes(source), "08.docx");
                Add(form, "outlines", System.Text.Encoding.UTF8.GetBytes(Outline("08.docx", (0, 1, text))), "08.json");
                form.Add(new StringContent("1"), "noLlm"); form.Add(new StringContent("1"), "outlineCache");
                return form;
            }
            using var previewForm = Form();
            using var previewResponse = await client.PostAsync("/api/admin/gather", previewForm);
            var previewEvents = Events(await previewResponse.Content.ReadAsStringAsync());
            Assert.DoesNotContain(previewEvents, e => e.GetProperty("type").GetString() == "error");
            var preview = previewEvents.Single(e => e.GetProperty("type").GetString() == "result");
            Assert.True(preview.GetProperty("targetReviewRequired").GetBoolean());
            Assert.False(preview.TryGetProperty("download", out _));
            var review = new { sourceSha256 = version.FileSha256, configKey = key,
                outlineSha256 = DocxHeaderExtractor.DocumentProcessing.Gather.OutlineApprovals.OutlineHash(saved), reviewer = "reviewer",
                items = new[] { new { location = heading.StableId, kind = "HEADING" } }, complete = true };
            using var approveResponse = await client.PostAsync("/api/admin/outline/review",
                new StringContent(JsonSerializer.Serialize(review), System.Text.Encoding.UTF8, "application/json"));
            approveResponse.EnsureSuccessStatusCode();
            Assert.Equal("REVIEWED", JsonDocument.Parse(await approveResponse.Content.ReadAsStringAsync()).RootElement.GetProperty("state").GetString());
            using var staleApply = await client.PostAsync($"/api/admin/gather/{preview.GetProperty("runId").GetString()}/apply",
                new StringContent("""{"decisions":[]}""", System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(System.Net.HttpStatusCode.Conflict, staleApply.StatusCode);

            using var form = Form();
            using var response = await client.PostAsync("/api/admin/gather", form);
            var events = Events(await response.Content.ReadAsStringAsync());
            Assert.DoesNotContain(events, e => e.GetProperty("type").GetString() == "error");
            var result = events.Single(e => e.GetProperty("type").GetString() == "result");
            Assert.False(result.GetProperty("targetReviewRequired").GetBoolean());
            var download = await client.GetByteArrayAsync(result.GetProperty("download").GetString());
            var written = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".docx");
            try { File.WriteAllBytes(written, download); using var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(written, false);
                Assert.Contains("Đã làm B.", doc.MainDocumentPart!.Document!.Body!.InnerText); }
            finally { File.Delete(written); }

            var contradictory = DocxHeaderExtractor.DocumentProcessing.Gather.OutlineApprovals.Decide(saved, "NOT_HEADING", heading.StableId, "reviewer", DateTimeOffset.UtcNow).Decision!;
            approvals.Add([contradictory]);
            using var apply = await client.PostAsync($"/api/admin/gather/{result.GetProperty("runId").GetString()}/apply",
                new StringContent("""{"decisions":[]}""", System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(System.Net.HttpStatusCode.Conflict, apply.StatusCode);
        }
        finally { File.Delete(target); File.Delete(source); if (Directory.Exists(root)) Directory.Delete(root, true); }
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
