using System.IO.Compression;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxHeaderExtractor.DocumentProcessing.Gather;
using DocxHeaderExtractor.DocumentProcessing.Output;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The whole chain with a stand-in model: each heading of a target, its goal, the batches over two sources, the checks, and the tracked result in a
/// copy of the target. The stand-in is deterministic and says nothing about quality: it keeps each unit that reads "Label: value", as the two parts.
/// </summary>
public sealed class GatherDocumentTests
{
    static string P(string text, string style = "") =>
        $"<w:p>{(style.Length == 0 ? "" : $"<w:pPr><w:pStyle w:val=\"{style}\"/></w:pPr>")}<w:r><w:t xml:space=\"preserve\">{text}</w:t></w:r></w:p>";
    static string Hash(string path) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

    static HeadingRecord Heading(int index, int? level, string text, string stableId) => new() { Index = index, Level = level, Text = text, StableId = stableId };

    sealed class LabelValueModel(params string[] failingPrefixes) : IGatherModel
    {
        public Task<string> CompleteAsync(string system, string user, CancellationToken ct)
        {
            var json = user[user.IndexOf("{\"units\"", StringComparison.Ordinal)..];
            using var doc = JsonDocument.Parse(json);
            var facts = new List<object>();
            foreach (var unit in doc.RootElement.GetProperty("units").EnumerateArray())
            {
                var id = unit.GetProperty("unitId").GetString()!;
                var text = unit.GetProperty("text").GetString()!;
                if (failingPrefixes.Any(f => id.StartsWith(f, StringComparison.Ordinal))) throw new InvalidOperationException("stand-in failure");
                var colon = text.IndexOf(':');
                if (colon <= 0 || colon > 40) continue;
                var value = text[(colon + 1)..].Trim();
                facts.Add(new
                {
                    parts = new[]
                    {
                        new { unitId = id, quote = text[..colon], role = "label" },
                        new { unitId = id, quote = value, role = "value" },
                    },
                });
            }
            return Task.FromResult(JsonSerializer.Serialize(new { facts }));
        }
    }

    static string Make(string body)
    {
        var path = RawDocxFixtureTests.Build(body);
        return path;
    }

    static string Copy(string path)
    {
        var copy = Path.Combine(Path.GetTempPath(), "dhx-gather-" + Guid.NewGuid().ToString("N") + ".docx");
        File.Copy(path, copy);
        return copy;
    }

    static IReadOnlyList<string> InsertedTexts(string path)
    {
        using var document = WordprocessingDocument.Open(path, false);
        return document.MainDocumentPart!.Document!.Body!.Descendants<InsertedRun>().Select(r => r.InnerText).ToList();
    }

    static IReadOnlyList<string> PlainTexts(string path)
    {
        using var document = WordprocessingDocument.Open(path, false);
        return document.MainDocumentPart!.Document!.Body!.Elements<Paragraph>()
            .Where(p => p.Descendants<InsertedRun>().Count() == 0).Select(p => p.InnerText).ToList();
    }

    [Fact]
    public async Task Two_sources_give_their_values_under_the_heading_and_a_conflict_keeps_both_values()
    {
        var target = Make(P("Thông tin chung", "Heading1") + P("Ghi chú mở đầu."));
        var a = Make(P("Chủ trì: Sở A.") + P("Thời gian: 2027."));
        var b = Make(P("Chủ trì: Sở B.") + P("Địa điểm: Hà Nội."));
        var outline = new[] { Heading(0, 1, "Thông tin chung", "body[1]/p[1]") };
        try
        {
            var sections = await GatherDocument.RunAsync(target, outline,
                [new GatherSourceFile("A", a, null), new GatherSourceFile("B", b, null)], new LabelValueModel());

            var section = Assert.Single(sections);
            Assert.True(section.Run.Complete);
            Assert.Equal(4, section.Run.Facts.Count);
            var conflict = Assert.Single(section.Conflicts);
            Assert.Equal("Chủ trì", conflict.Label);
            Assert.Equal(["Sở A.", "Sở B."], conflict.Values);
        }
        finally
        {
            foreach (var f in new[] { target, a, b }) File.Delete(f);
        }
    }

    [Fact]
    public async Task The_written_copy_has_tracked_insertions_after_the_heading_and_the_source_is_unchanged()
    {
        var target = Make(P("Thông tin chung", "Heading1") + P("Ghi chú mở đầu."));
        var a = Make(P("Chủ trì: Sở A.") + P("Thời gian: 2027."));
        var b = Make(P("Chủ trì: Sở B."));
        var outline = new[] { Heading(0, 1, "Thông tin chung", "body[1]/p[1]") };
        var output = Path.Combine(Path.GetTempPath(), "dhx-gather-out-" + Guid.NewGuid().ToString("N") + ".docx");
        var before = File.ReadAllBytes(target);
        try
        {
            var sections = await GatherDocument.RunAsync(target, outline,
                [new GatherSourceFile("A", a, null), new GatherSourceFile("B", b, null)], new LabelValueModel());

            var result = GatherWriteback.Apply(target, output, sections, new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc), targetOutlineCoverageVerified: true, verifiedTargetSha256: Hash(target));

            Assert.Empty(result.SkippedHeadings);
            Assert.Equal(File.ReadAllBytes(target), before);
            var inserted = InsertedTexts(output);
            Assert.Contains(inserted, t => t.StartsWith("- Chủ trì: Sở A.", StringComparison.Ordinal));
            Assert.Contains(inserted, t => t.StartsWith("[mâu thuẫn — Chủ trì: Sở A.", StringComparison.Ordinal));
            Assert.Equal(["Thông tin chung", "Ghi chú mở đầu."], PlainTexts(output));
        }
        finally
        {
            foreach (var f in new[] { target, a, b, output }) File.Delete(f);
        }
    }

    [Fact]
    public async Task A_failed_batch_is_written_as_an_incomplete_note_not_as_an_empty_section()
    {
        var target = Make(P("Thông tin chung", "Heading1") + P("Ghi chú mở đầu."));
        var a = Make(P("Chủ trì: Sở A."));
        var outline = new[] { Heading(0, 1, "Thông tin chung", "body[1]/p[1]") };
        var output = Path.Combine(Path.GetTempPath(), "dhx-gather-out-" + Guid.NewGuid().ToString("N") + ".docx");
        try
        {
            var sections = await GatherDocument.RunAsync(target, outline, [new GatherSourceFile("A", a, null)], new LabelValueModel("A|"));

            Assert.False(Assert.Single(sections).Run.Complete);
            GatherWriteback.Apply(target, output, sections, DateTime.UtcNow, targetOutlineCoverageVerified: true, verifiedTargetSha256: Hash(target));

            Assert.Contains(InsertedTexts(output), t => t.StartsWith("[chưa xét đủ:", StringComparison.Ordinal));
            Assert.DoesNotContain(InsertedTexts(output), t => t.StartsWith("[không có ý nào", StringComparison.Ordinal));
        }
        finally
        {
            foreach (var f in new[] { target, a, output }) File.Delete(f);
        }
    }

    [Fact]
    public void A_result_under_a_heading_split_over_two_paragraphs_goes_after_the_whole_heading()
    {
        var target = Make(P("Thông tin") + P("chung của dự án", "Heading1") + P("Đoạn cũ.") + P("Mục con", "Heading2"));
        var output = Path.Combine(Path.GetTempPath(), "dhx-gather-out-" + Guid.NewGuid().ToString("N") + ".docx");
        try
        {
            var fact = new GatheredFact([new SourcedPart("A|body[1]/p[1]", "A", "body[1]/p[1]", "text", "Sở A")], []);
            var section = new GatherSection("body[1]/p[1]", "Thông tin chung của dự án", "", new GatherRun([fact], [], [], [], [], [], 1), [], "body[1]/p[4]");

            GatherWriteback.Apply(target, output, [section], DateTime.UtcNow, targetOutlineCoverageVerified: true, verifiedTargetSha256: Hash(target));

            using var document = WordprocessingDocument.Open(output, false);
            var texts = document.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().Select(p => p.InnerText).ToList();
            Assert.Equal(["Thông tin", "chung của dự án", "Đoạn cũ.", "- Sở A (nguồn: A body[1]/p[1])", "Mục con"], texts);
        }
        finally
        {
            foreach (var f in new[] { target, output }) File.Delete(f);
        }
    }

    [Fact]
    public void A_heading_with_no_direct_content_gets_its_result_right_after_it_and_before_its_sub_heading()
    {
        var target = Make(P("Thông tin chung", "Heading1") + P("Mục con", "Heading2") + P("Nội dung con."));
        var output = Path.Combine(Path.GetTempPath(), "dhx-gather-out-" + Guid.NewGuid().ToString("N") + ".docx");
        try
        {
            var fact = new GatheredFact([new SourcedPart("A|body[1]/p[9]", "A", "body[1]/p[9]", "text", "Sở A")], []);
            var section = new GatherSection("body[1]/p[1]", "Thông tin chung", "", new GatherRun([fact], [], [], [], [], [], 1), [], "body[1]/p[2]");

            GatherWriteback.Apply(target, output, [section], DateTime.UtcNow, targetOutlineCoverageVerified: true, verifiedTargetSha256: Hash(target));

            using var document = WordprocessingDocument.Open(output, false);
            var texts = document.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().Select(p => p.InnerText).ToList();
            Assert.Equal(["Thông tin chung", "- Sở A (nguồn: A body[1]/p[9])", "Mục con", "Nội dung con."], texts);
        }
        finally
        {
            foreach (var f in new[] { target, output }) File.Delete(f);
        }
    }

    [Fact]
    public void A_heading_with_no_later_heading_gets_its_result_at_the_end_of_the_document()
    {
        var target = Make(P("Thông tin chung", "Heading1") + P("Đoạn một.") + P("Đoạn hai."));
        var output = Path.Combine(Path.GetTempPath(), "dhx-gather-out-" + Guid.NewGuid().ToString("N") + ".docx");
        try
        {
            var fact = new GatheredFact([new SourcedPart("A|body[1]/p[9]", "A", "body[1]/p[9]", "text", "Sở A")], []);
            var section = new GatherSection("body[1]/p[1]", "Thông tin chung", "", new GatherRun([fact], [], [], [], [], [], 1), []);

            GatherWriteback.Apply(target, output, [section], DateTime.UtcNow, targetOutlineCoverageVerified: true, verifiedTargetSha256: Hash(target));

            using var document = WordprocessingDocument.Open(output, false);
            var texts = document.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().Select(p => p.InnerText).ToList();
            Assert.Equal(["Thông tin chung", "Đoạn một.", "Đoạn hai.", "- Sở A (nguồn: A body[1]/p[9])"], texts);
        }
        finally
        {
            foreach (var f in new[] { target, output }) File.Delete(f);
        }
    }

    [Fact]
    public void Duplicate_fact_keeps_both_source_citations_without_repeating_the_text()
    {
        var target = Make(P("Thông tin chung", "Heading1") + P("Nội dung cũ."));
        var output = Path.Combine(Path.GetTempPath(), "dhx-gather-out-" + Guid.NewGuid().ToString("N") + ".docx");
        try
        {
            var fact = new GatheredFact([new SourcedPart("T8|body[1]/p[1]", "T8", "body[1]/p[1]", "text", "Thông tin")], [])
            {
                AlsoFrom = [new SourcedPart("T9|body[1]/p[4]", "T9", "body[1]/p[4]", "text", "Thông tin")],
            };
            var section = new GatherSection("body[1]/p[1]", "Thông tin chung", "", new GatherRun([fact], [], [], [], [], [], 1), []);

            GatherWriteback.Apply(target, output, [section], DateTime.UtcNow, targetOutlineCoverageVerified: true, verifiedTargetSha256: Hash(target));

            var line = Assert.Single(InsertedTexts(output));
            Assert.Equal("- Thông tin (nguồn: T8 body[1]/p[1]; T9 body[1]/p[4])", line);
        }
        finally
        {
            foreach (var f in new[] { target, output }) File.Delete(f);
        }
    }

    static string Tbl(params string[] cells) =>
        "<w:tbl><w:tblPr/><w:tblGrid><w:gridCol w:w=\"2000\"/><w:gridCol w:w=\"2000\"/></w:tblGrid><w:tr>"
        + string.Concat(cells.Select(c => $"<w:tc>{c}</w:tc>")) + "</w:tr></w:tbl>";

    static (IReadOnlyList<string> Skipped, OpenXmlElement[] Children) WriteOne(string body, GatherSection section)
    {
        var target = Make(body);
        var output = Path.Combine(Path.GetTempPath(), "dhx-gather-out-" + Guid.NewGuid().ToString("N") + ".docx");
        try
        {
            var result = GatherWriteback.Apply(target, output, [section], DateTime.UtcNow, targetOutlineCoverageVerified: true, verifiedTargetSha256: Hash(target));
            using var document = WordprocessingDocument.Open(output, false);
            return (result.SkippedHeadings, document.MainDocumentPart!.Document!.Body!.ChildElements.ToArray());
        }
        finally
        {
            foreach (var f in new[] { target, output }) File.Delete(f);
        }
    }

    static GatheredFact Fact(string text) => new([new SourcedPart("A|x", "A", "x", "text", text)], []);

    [Fact]
    public void A_heading_inside_a_table_is_reported_as_a_place_that_is_not_known_safely()
    {
        var cell = "<w:p><w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr><w:r><w:t>Thông tin chung</w:t></w:r></w:p>";
        var section = new GatherSection("body[1]/tbl[1]/tr[1]/tc[1]/p[1]", "Thông tin chung", "", new GatherRun([Fact("Sở A")], [], [], [], [], [], 1), [], null);

        var (skipped, _) = WriteOne(Tbl(cell), section);

        Assert.Equal(["Thông tin chung (vị trí trong bảng, không xác định an toàn)"], skipped);
    }

    [Fact]
    public void A_next_heading_inside_the_same_table_as_the_direct_content_is_reported_not_guessed()
    {
        var heading = P("Thông tin chung", "Heading1");
        var table = Tbl("<w:p><w:r><w:t>Nội dung trong bảng</w:t></w:r></w:p>",
            "<w:p><w:pPr><w:pStyle w:val=\"Heading2\"/></w:pPr><w:r><w:t>Mục con</w:t></w:r></w:p>");
        var section = new GatherSection("body[1]/p[1]", "Thông tin chung", "", new GatherRun([Fact("Sở A")], [], [], [], [], [], 1), [],
            "body[1]/tbl[1]/tr[1]/tc[2]/p[1]");

        var (skipped, _) = WriteOne(heading + table, section);

        Assert.Single(skipped);
        Assert.Contains("vị trí trong bảng", skipped[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_table_that_is_the_direct_content_gets_the_result_after_it_when_the_next_heading_is_outside()
    {
        var heading = P("Thông tin chung", "Heading1");
        var table = Tbl("<w:p><w:r><w:t>Ô một</w:t></w:r></w:p>", "<w:p><w:r><w:t>Ô hai</w:t></w:r></w:p>");
        var next = P("Mục sau", "Heading1");
        var section = new GatherSection("body[1]/p[1]", "Thông tin chung", "", new GatherRun([Fact("Sở A")], [], [], [], [], [], 1), [], "body[1]/p[2]");

        var (skipped, children) = WriteOne(heading + table + next, section);

        Assert.Empty(skipped);
        Assert.Equal(["p", "tbl", "p", "p"], children.Select(c => c.LocalName));
        Assert.True(children[2].Descendants<InsertedRun>().Any());
    }

    [Fact]
    public void Rejecting_every_insertion_removes_the_new_paragraphs_and_leaves_the_original_text()
    {
        var target = Make(P("Thông tin chung", "Heading1") + P("Ghi chú mở đầu.") + P("Đoạn cuối."));
        var output = Path.Combine(Path.GetTempPath(), "dhx-gather-out-" + Guid.NewGuid().ToString("N") + ".docx");
        try
        {
            var section = new GatherSection("body[1]/p[1]", "Thông tin chung", "", new GatherRun([Fact("Sở A"), Fact("Hà Nội")], [], [], [], [], [], 1), []);
            GatherWriteback.Apply(target, output, [section], DateTime.UtcNow, targetOutlineCoverageVerified: true, verifiedTargetSha256: Hash(target));

            using var document = WordprocessingDocument.Open(output, true);
            var body = document.MainDocumentPart!.Document!.Body!;
            var added = body.Elements<Paragraph>().Where(p => p.ParagraphProperties?.ParagraphMarkRunProperties?.GetFirstChild<Inserted>() is not null).ToList();
            Assert.Equal(2, added.Count);
            // Reject all: remove the paragraphs whose mark is an insertion, and the insertions in the rest.
            foreach (var paragraph in added) paragraph.Remove();
            foreach (var ins in body.Descendants<InsertedRun>().ToList()) ins.Remove();
            Assert.Equal(["Thông tin chung", "Ghi chú mở đầu.", "Đoạn cuối."], body.Elements<Paragraph>().Select(p => p.InnerText).ToList());
        }
        finally
        {
            foreach (var f in new[] { target, output }) File.Delete(f);
        }
    }

    [Fact]
    public async Task Two_headings_with_the_same_text_each_get_only_the_text_under_them()
    {
        var target = Make(P("Thông tin chung", "Heading1") + P("Nội dung A.") + P("Thông tin chung", "Heading1") + P("Nội dung B."));
        var source = Make(P("Đoạn nguồn."));
        var outline = new[] { Heading(0, 1, "Thông tin chung", "body[1]/p[1]"), Heading(2, 1, "Thông tin chung", "body[1]/p[3]") };
        try
        {
            var sections = await GatherDocument.RunAsync(target, outline, [new GatherSourceFile("A", source, null)], new LabelValueModel());

            Assert.Equal("Thông tin chung\nNội dung A.", sections[0].Goal);
            Assert.Equal("Thông tin chung\nNội dung B.", sections[1].Goal);
            Assert.Equal("body[1]/p[3]", sections[1].TargetLocation);
        }
        finally
        {
            foreach (var f in new[] { target, source }) File.Delete(f);
        }
    }

    [Fact]
    public void A_section_with_an_unread_source_region_is_written_as_partial_even_when_every_batch_succeeded()
    {
        var target = Make(P("Thông tin chung", "Heading1"));
        var output = Path.Combine(Path.GetTempPath(), "dhx-gather-out-" + Guid.NewGuid().ToString("N") + ".docx");
        try
        {
            var gaps = new[] { new DocxHeaderExtractor.DocumentProcessing.Gather.GatherGap("A", "", "drawing_or_image") };
            var run = new GatherRun([Fact("Sở A")], [], [], [], [], gaps, 1);
            Assert.True(run.BatchFailures.Count == 0);
            Assert.False(run.Complete);

            GatherWriteback.Apply(target, output, [new GatherSection("body[1]/p[1]", "Thông tin chung", "", run, [], null)], DateTime.UtcNow, targetOutlineCoverageVerified: true, verifiedTargetSha256: Hash(target));

            using var document = WordprocessingDocument.Open(output, false);
            var inserted = document.MainDocumentPart!.Document!.Body!.Descendants<InsertedRun>().Select(r => r.InnerText).ToList();
            Assert.Contains(inserted, t => t.StartsWith("[nguồn chưa đọc đủ: A drawing_or_image", StringComparison.Ordinal));
        }
        finally
        {
            foreach (var f in new[] { target, output }) File.Delete(f);
        }
    }

    [Fact]
    public void The_result_is_never_written_over_the_source()
    {
        var target = Make(P("Thông tin chung", "Heading1"));
        try
        {
            Assert.Throws<ArgumentException>(() => GatherWriteback.Apply(target, target, [], DateTime.UtcNow));
        }
        finally
        {
            File.Delete(target);
        }
    }

    [Fact]
    public void Unverified_target_outline_coverage_does_not_write_any_fact()
    {
        var target = Make(P("Mục đầu") + P("Nội dung") + P("Mục bị bỏ sót") + P("Mục sau"));
        var output = Path.Combine(Path.GetTempPath(), "dhx-coverage-" + Guid.NewGuid().ToString("N") + ".docx");
        try
        {
            var section = new GatherSection("body[1]/p[1]", "Mục đầu", "", new GatherRun([Fact("Nguồn")], [], [], [], [], [], 1), [], "body[1]/p[4]");
            var result = GatherWriteback.Apply(target, output, [section], DateTime.UtcNow);
            Assert.Contains("độ phủ heading", Assert.Single(result.SkippedHeadings), StringComparison.Ordinal);
            Assert.Empty(InsertedTexts(output));
        }
        finally { File.Delete(target); File.Delete(output); }
    }

    [Fact]
    public void Coverage_verified_for_another_snapshot_cannot_authorize_writeback()
    {
        var target = Make(P("Mục đầu") + P("Nội dung"));
        var output = Path.Combine(Path.GetTempPath(), "dhx-stale-hash-" + Guid.NewGuid().ToString("N") + ".docx");
        try
        {
            var section = new GatherSection("body[1]/p[1]", "Mục đầu", "", new GatherRun([Fact("Nguồn")], [], [], [], [], [], 1), []);
            var result = GatherWriteback.Apply(target, output, [section], DateTime.UtcNow, targetOutlineCoverageVerified: true, verifiedTargetSha256: new string('0', 64));
            Assert.Contains("độ phủ heading", Assert.Single(result.SkippedHeadings), StringComparison.Ordinal);
            Assert.Empty(InsertedTexts(output));
        }
        finally { File.Delete(target); File.Delete(output); }
    }
}
