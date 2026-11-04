using DocxHeaderExtractor.DocumentProcessing.Gather;
using DocxHeaderExtractor.DocumentProcessing.Output;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace DocxHeaderExtractor.Tests;

/// <summary>The contract: content moves only from a source heading to the original heading with the same name. No model is called.</summary>
public sealed class GatherByHeadingTests
{
    sealed class ScopeModel : IGatherModel
    {
        public Task<string> CompleteAsync(string system, string user, CancellationToken ct) => Task.FromResult(
            "{\"verdicts\":[{\"id\":0,\"decision\":\"KEEP\",\"reason\":\"matches\"},{\"id\":1,\"decision\":\"EXCLUDE\",\"reason\":\"out of scope\"}]}");
    }
    static string P(string text) => $"<w:p><w:r><w:t xml:space=\"preserve\">{text}</w:t></w:r></w:p>";

    static HeadingRecord H(int index, string text, string stableId, int level = 1) => new() { Index = index, Level = level, Text = text, StableId = stableId };

    [Theory]
    [InlineData("1. Thông tin chung", "3. THÔNG TIN CHUNG")]
    [InlineData("Thông tin chung:", "2.1 Thông tin chung")]
    [InlineData("Item 1: Document Approval.", "Document Approval")]
    [InlineData("Opening:", "OPENING")]
    public void Headings_with_the_same_name_match_whatever_their_numbering(string a, string b) =>
        Assert.Equal(GatherByHeading.Name(a), GatherByHeading.Name(b));

    [Theory]
    [InlineData("Opening:", "Opening remarks")]
    [InlineData("Thông tin chung", "Tổng quan dự án")]
    [InlineData("A Plan", "B Plan")]
    [InlineData("I am here", "am here")]
    public void Headings_with_different_names_do_not_match(string a, string b) =>
        Assert.NotEqual(GatherByHeading.Name(a), GatherByHeading.Name(b));

    [Fact]
    public void Content_moves_only_under_the_same_named_heading_and_a_line_already_there_is_not_copied()
    {
        var target = RawDocxFixtureTests.Build(
            P("Opening: The meeting was called to order on 21 November.") + P("Present:") + P("Trustee: Traci Phillips") + P("Item 9: Other business.") + P("Nothing."));
        var source = RawDocxFixtureTests.Build(
            P("2. OPENING") + P("The meeting was called to order on 27 November.") + P("Present") + P("Trustee: Traci Phillips") + P("Legal: Duncan Kiara")
            + P("Voting Rights") + P("The GC agreed on voting rights."));
        try
        {
            var targetOutline = new[] { H(0, "Opening:", "body[1]/p[1]"), H(1, "Present:", "body[1]/p[2]"), H(3, "Item 9: Other business.", "body[1]/p[4]") };
            var sourceOutline = new[] { H(0, "2. OPENING", "body[1]/p[1]"), H(2, "Present", "body[1]/p[3]"), H(5, "Voting Rights", "body[1]/p[6]") };

            var (sections, excluded) = GatherByHeading.Build(target, targetOutline, [new GatherSourceFile("S", source, sourceOutline)]);

            Assert.Equal(["The meeting was called to order on 27 November."], sections[0].Run.Facts.Select(f => f.Render()));
            Assert.Equal(["Legal: Duncan Kiara"], sections[1].Run.Facts.Select(f => f.Render()));
            Assert.Equal(2, excluded.Count);
            Assert.Equal("Trustee: Traci Phillips", Assert.Single(excluded, e => e.Reason.StartsWith("ALREADY_IN_TARGET", StringComparison.Ordinal)).Fact.Render());
            // Content under a heading the original does not have is not written, and it is listed.
            var unplaced = Assert.Single(excluded, e => e.Reason.StartsWith("UNPLACED: văn bản đích không có heading", StringComparison.Ordinal));
            Assert.Equal("The GC agreed on voting rights.", unplaced.Fact.Render());
            Assert.Empty(sections[2].Run.Facts);
            Assert.Equal("không có heading cùng tên trong văn bản nguồn", sections[2].Note);
            Assert.DoesNotContain(sections.SelectMany(s => s.Run.Facts), f => f.Render().Contains("voting rights", StringComparison.Ordinal));
            Assert.Equal("S|body[1]/p[2]", sections[0].Run.Facts[0].Parts[0].UnitId);
        }
        finally { File.Delete(target); File.Delete(source); }
    }

    static string Row(params string[] cells) =>
        "<w:tbl><w:tblPr/><w:tblGrid><w:gridCol w:w=\"2000\"/></w:tblGrid><w:tr>" + string.Concat(cells.Select(c => $"<w:tc><w:p><w:r><w:t>{c}</w:t></w:r></w:p></w:tc>")) + "</w:tr></w:tbl>";

    [Fact]
    public void The_separator_after_a_heading_is_not_copied_a_table_row_stays_one_fact_and_a_short_word_is_not_a_repeat()
    {
        var target = RawDocxFixtureTests.Build(P("Present:") + P("The minutes summarize the discussion on the agenda."));
        var source = RawDocxFixtureTests.Build(P("Present: Ông A, Bà B") + Row("financed", "by", "the", "FIF.") + P("on"));
        try
        {
            var (sections, excluded) = GatherByHeading.Build(target, [H(0, "Present:", "body[1]/p[1]")],
                [new GatherSourceFile("S", source, [H(0, "Present", "body[1]/p[1]")])]);

            Assert.Equal(["Ông A, Bà B", "financed by the FIF.", "on"], sections[0].Run.Facts.Select(f => f.Render()));
            Assert.Empty(excluded);
        }
        finally { File.Delete(target); File.Delete(source); }
    }

    [Fact]
    public void Same_named_sections_keep_inline_content_sign_and_do_not_discard_a_substring()
    {
        var target = RawDocxFixtureTests.Build(P("1. Thông tin chung") + P("Kế hoạch triển khai giai đoạn đầu đã được duyệt."));
        var source = RawDocxFixtureTests.Build(P("3. Thông tin chung -15 độ") + P("Kế hoạch triển khai giai đoạn đầu"));
        try
        {
            var (sections, excluded) = GatherByHeading.Build(target, [H(0, "1. Thông tin chung", "body[1]/p[1]")],
                [new GatherSourceFile("S", source, [H(0, "3. Thông tin chung", "body[1]/p[1]")])]);
            Assert.Equal(["-15 độ", "Kế hoạch triển khai giai đoạn đầu"], sections[0].Run.Facts.Select(f => f.Render()));
            Assert.Empty(excluded);
            var first = sections[0].Run.Facts[0].Parts[0];
            Assert.Equal("S|body[1]/p[1]", first.UnitId);
            Assert.Equal("-15 độ", first.Text);
        }
        finally { File.Delete(target); File.Delete(source); }
    }

    [Fact]
    public void Repeated_target_names_are_not_automatically_assigned_the_same_source_content()
    {
        var target = RawDocxFixtureTests.Build(P("Thông tin chung") + P("A") + P("Thông tin chung") + P("B"));
        var source = RawDocxFixtureTests.Build(P("Thông tin chung") + P("Nguồn"));
        try
        {
            var (sections, _) = GatherByHeading.Build(target,
                [H(0, "Thông tin chung", "body[1]/p[1]"), H(2, "Thông tin chung", "body[1]/p[3]")],
                [new GatherSourceFile("S", source, [H(0, "Thông tin chung", "body[1]/p[1]")])]);
            Assert.All(sections, section => { Assert.Empty(section.Run.Facts); Assert.False(section.Run.Complete); Assert.Contains("nhiều heading cùng tên", section.Note); });
        }
        finally { File.Delete(target); File.Delete(source); }
    }

    [Fact]
    public void Repeated_source_sections_are_preserved_with_ambiguity_flag()
    {
        var target = RawDocxFixtureTests.Build(P("Thông tin chung"));
        var source = RawDocxFixtureTests.Build(P("Thông tin chung") + P("A") + P("Thông tin chung") + P("B"));
        try
        {
            var (sections, _) = GatherByHeading.Build(target, [H(0, "Thông tin chung", "body[1]/p[1]")],
                [new GatherSourceFile("S", source, [H(0, "Thông tin chung", "body[1]/p[1]"), H(2, "Thông tin chung", "body[1]/p[3]")])]);
            Assert.Equal(["A", "B"], sections[0].Run.Facts.Select(f => f.Render()));
            Assert.All(sections[0].Run.Facts, fact => Assert.Contains("MULTIPLE_MATCHING_SOURCE_SECTIONS", fact.Flags));
        }
        finally { File.Delete(target); File.Delete(source); }
    }

    [Fact]
    public void Content_of_a_child_section_is_not_silently_copied_to_its_parent()
    {
        var target = RawDocxFixtureTests.Build(P("Thông tin chung") + P("Nhân sự"));
        var source = RawDocxFixtureTests.Build(P("Thông tin chung") + P("Nội dung chung") + P("Nhân sự") + P("Nội dung nhân sự"));
        try
        {
            var (sections, _) = GatherByHeading.Build(target,
                [H(0, "Thông tin chung", "body[1]/p[1]"), H(1, "Nhân sự", "body[1]/p[2]", 2)],
                [new GatherSourceFile("S", source, [H(0, "Thông tin chung", "body[1]/p[1]"), H(2, "Nhân sự", "body[1]/p[3]", 2)])]);
            Assert.Equal(["Nội dung chung"], sections[0].Run.Facts.Select(f => f.Render()));
            Assert.Equal(["Nội dung nhân sự"], sections[1].Run.Facts.Select(f => f.Render()));
        }
        finally { File.Delete(target); File.Delete(source); }
    }

    [Fact]
    public void Same_heading_from_two_sources_writes_under_the_correct_section_in_a_copy()
    {
        var target = RawDocxFixtureTests.Build(P("1. Thông tin chung") + P("Nội dung gốc") + P("Nhân sự") + P("Nhân sự gốc"));
        var a = RawDocxFixtureTests.Build(P("3. Thông tin chung") + P("Địa điểm: Hà Nội") + P("Khác") + P("Không chuyển"));
        var b = RawDocxFixtureTests.Build(P("2. Thông tin chung") + P("Địa điểm: Hải Phòng"));
        var output = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".docx");
        var blocked = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".docx");
        static string Hash(string path) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
        try
        {
            var targetHash = Hash(target);
            var (sections, _) = GatherByHeading.Build(target,
                [H(0, "1. Thông tin chung", "body[1]/p[1]"), H(2, "Nhân sự", "body[1]/p[3]")],
                [new GatherSourceFile("A", a, [H(0, "3. Thông tin chung", "body[1]/p[1]"), H(2, "Khác", "body[1]/p[3]")]),
                 new GatherSourceFile("B", b, [H(0, "2. Thông tin chung", "body[1]/p[1]")])]);
            Assert.Equal(["Địa điểm: Hà Nội", "Địa điểm: Hải Phòng"], sections[0].Run.Facts.Select(f => f.Render()));
            Assert.Equal(["A", "B"], sections[0].Run.Facts.Select(f => f.Parts[0].SourceId));
            var beforeReview = GatherWriteback.Apply(target, blocked, sections, DateTime.UtcNow, true, targetHash);
            Assert.Contains(beforeReview.SkippedHeadings, s => s.Contains("chưa được review"));
            var review = new GatherReviewResult([new(0, "KEEP", "test"), new(1, "KEEP", "test")], null, 0);
            var (reviewed, _) = GatherReviewApply.Apply(sections[0], review);
            var result = GatherWriteback.Apply(target, output, [reviewed, sections[1]], DateTime.UtcNow, true, targetHash);
            Assert.Empty(result.SkippedHeadings);
            Assert.Equal(targetHash, Hash(target));
            using var document = WordprocessingDocument.Open(output, false);
            var paragraphs = document.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().Select(p => p.InnerText).ToList();
            Assert.True(paragraphs.FindIndex(p => p.Contains("Địa điểm: Hà Nội")) > paragraphs.IndexOf("Nội dung gốc"));
            Assert.True(paragraphs.FindIndex(p => p.Contains("Địa điểm: Hải Phòng")) < paragraphs.IndexOf("Nhân sự"));
            Assert.DoesNotContain(paragraphs, p => p.Contains("Không chuyển"));
        }
        finally { File.Delete(target); File.Delete(a); File.Delete(b); File.Delete(output); File.Delete(blocked); }
    }

    [Fact]
    public void Source_ids_must_be_unique_to_keep_provenance_unambiguous()
    {
        var target = RawDocxFixtureTests.Build(P("Thông tin chung"));
        try
        {
            Assert.Throws<ArgumentException>(() => GatherByHeading.Build(target, [],
                [new GatherSourceFile("same", target, []), new GatherSourceFile("same", target, [])]));
        }
        finally { File.Delete(target); }
    }

    [Fact]
    public void An_unsure_or_failed_scope_review_cannot_authorize_writeback()
    {
        var target = RawDocxFixtureTests.Build(P("Opening:"));
        var source = RawDocxFixtureTests.Build(P("Opening:") + P("Nội dung cần xét"));
        try
        {
            var (sections, _) = GatherByHeading.Build(target, [H(0, "Opening:", "body[1]/p[1]")],
                [new GatherSourceFile("S", source, [H(0, "Opening:", "body[1]/p[1]")])]);
            var (unsure, _) = GatherReviewApply.Apply(sections[0], new GatherReviewResult([new(0, "UNSURE", "cần xem lại")], null, 0));
            var (failed, _) = GatherReviewApply.Apply(sections[0], new GatherReviewResult([], "failed", 0));
            Assert.Contains("SCOPE_UNREVIEWED", Assert.Single(unsure.Run.Facts).Flags);
            Assert.Contains("SCOPE_UNREVIEWED", Assert.Single(failed.Run.Facts).Flags);
        }
        finally { File.Delete(target); File.Delete(source); }
    }

    [Fact]
    public async Task Name_matched_candidates_must_pass_scope_review_before_writeback()
    {
        var target = RawDocxFixtureTests.Build(P("Present:"));
        var source = RawDocxFixtureTests.Build(P("Present:") + P("Người tham dự") + P("Nội dung khác"));
        var sourceOutline = new[] { H(0, "Present:", "body[1]/p[1]") };
        try
        {
            var (sections, _) = GatherByHeading.Build(target, [H(0, "Present:", "body[1]/p[1]")],
                [new GatherSourceFile("S", source, sourceOutline)]);
            var (reviewed, excluded, audits) = await GatherByHeading.ReviewAsync(sections,
                [GatherUnits.Read(source, "S", sourceOutline)], new ScopeModel());
            Assert.Equal("Người tham dự", Assert.Single(reviewed[0].Run.Facts).Render());
            Assert.DoesNotContain("SCOPE_UNREVIEWED", reviewed[0].Run.Facts[0].Flags);
            Assert.Equal("Nội dung khác", Assert.Single(excluded).Fact.Render());
            Assert.Null(Assert.Single(audits).Result.Error);
        }
        finally { File.Delete(target); File.Delete(source); }
    }
}
