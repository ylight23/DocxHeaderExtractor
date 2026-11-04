using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxHeaderExtractor.DocumentProcessing.Gather;
using DocxHeaderExtractor.DocumentProcessing.Output;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Content goes to the target heading with the same chain (parents and own heading, dates ignored), the same heading name under the matched parent
/// when the levels differ, or a new heading. The cases follow the monthly reports: "Truyền thông" is under both "đã triển khai" and "kế hoạch".
/// </summary>
public sealed class GatherByChainTests
{
    static string P(string text) => $"<w:p><w:r><w:t xml:space=\"preserve\">{text}</w:t></w:r></w:p>";

    static HeadingRecord H(int index, string text, int level) => new() { Index = index, Level = level, Text = text, StableId = $"body[1]/p[{index + 1}]" };

    static readonly string TargetBody = P("Hoạt động tháng 10/2026") + P("Truyền thông:") + P("Đã đăng 5 bài.") + P("Công tác xã hội:")
        + P("Công tác hỗ trợ người bệnh") + P("Hỗ trợ 10 người.") + P("Kế hoạch tháng 11/2026") + P("Truyền thông:") + P("Tiếp tục đăng bài.");

    static readonly HeadingRecord[] TargetOutline = [H(0, "Hoạt động tháng 10/2026", 1), H(1, "Truyền thông:", 2), H(3, "Công tác xã hội:", 2),
        H(4, "Công tác hỗ trợ người bệnh", 3), H(6, "Kế hoạch tháng 11/2026", 1), H(7, "Truyền thông:", 2)];

    static readonly string T8Body = P("Hoạt động tháng 8/2026") + P("TRUYỀN THÔNG:") + P("Đăng 3 bài về lao.") + P("Kết quả truyền thông")
        + P("Đạt 1 triệu lượt xem.") + P("Công tác hỗ trợ người bệnh") + P("Hỗ trợ 4 người.") + P("Kế hoạch tháng 9/2026") + P("TRUYỀN THÔNG:")
        + P("Tiếp tục đăng bài.") + P("Quỹ Pastb:") + P("Rà soát hồ sơ.");

    // "Công tác hỗ trợ người bệnh" is one level too high here, as in the outline of the September report.
    static readonly HeadingRecord[] T8Outline = [H(0, "Hoạt động tháng 8/2026", 1), H(1, "TRUYỀN THÔNG:", 2), H(3, "Kết quả truyền thông", 3),
        H(5, "Công tác hỗ trợ người bệnh", 2), H(7, "Kế hoạch tháng 9/2026", 1), H(8, "TRUYỀN THÔNG:", 2), H(10, "Quỹ Pastb:", 2)];

    static readonly string T9Body = P("Hoạt động tháng 9/2026") + P("Truyền thông:") + P("Kết quả truyền thông") + P("Đạt 1 triệu lượt xem.")
        + P("Đạt 2 triệu lượt xem.");

    static readonly HeadingRecord[] T9Outline = [H(0, "Hoạt động tháng 9/2026", 1), H(1, "Truyền thông:", 2), H(2, "Kết quả truyền thông", 3)];

    static void WithFiles(Action<string, string, string> test)
    {
        var target = RawDocxFixtureTests.Build(TargetBody);
        var t8 = RawDocxFixtureTests.Build(T8Body);
        var t9 = RawDocxFixtureTests.Build(T9Body);
        try { test(target, t8, t9); }
        finally { File.Delete(target); File.Delete(t8); File.Delete(t9); }
    }

    static GatherSection Named(IReadOnlyList<GatherSection> sections, string heading, int nth = 0) =>
        sections.Where(s => s.Heading == heading).ElementAt(nth);

    [Theory]
    [InlineData("Kế hoạch trọng tâm hoạt động tháng 9/2026", "KẾ HOẠCH TRỌNG TÂM HOẠT ĐỘNG THÁNG 11/2026")]
    [InlineData("Báo cáo năm 2025", "Báo cáo năm 2026")]
    [InlineData("Kết quả quý 3", "Kết quả quý 4")]
    public void Dates_do_not_change_the_key(string a, string b) => Assert.Equal(GatherByHeading.Key(a), GatherByHeading.Key(b));

    [Theory]
    [InlineData("Phụ lục 1", "Phụ lục 2")]
    [InlineData("Đợt 1", "Đợt 2")]
    public void Other_numbers_keep_headings_apart(string a, string b) => Assert.NotEqual(GatherByHeading.Key(a), GatherByHeading.Key(b));

    [Fact]
    public void Each_source_section_goes_to_the_target_section_with_the_same_chain_only() => WithFiles((target, t8, _) =>
    {
        var (sections, excluded) = GatherByHeading.Build(target, TargetOutline, [new GatherSourceFile("T8", t8, T8Outline)], addMissingHeadings: true);

        Assert.Equal(["Đăng 3 bài về lao."], Named(sections, "Truyền thông:", 0).Run.Facts.Select(f => f.Render()));
        Assert.Empty(Named(sections, "Truyền thông:", 1).Run.Facts);
        Assert.Contains(excluded, e => e.Fact.Render() == "Tiếp tục đăng bài." && e.Reason == "ALREADY_IN_TARGET_EXACT");
    });

    [Fact]
    public void A_heading_at_another_level_is_held_for_review_even_when_its_name_is_unique() => WithFiles((target, t8, _) =>
    {
        var plan = GatherByHeading.Build(target, TargetOutline, [new GatherSourceFile("T8", t8, T8Outline)], addMissingHeadings: true);
        Assert.Empty(Named(plan.Sections, "Công tác hỗ trợ người bệnh").Run.Facts);
        var held = Assert.Single(plan.Uncertain, u => u.Kind == "PATH_DIFFERS" && u.SourceHeading == "Công tác hỗ trợ người bệnh");
        Assert.Equal("Hỗ trợ 4 người.", Assert.Single(held.Facts).Render());
        Assert.Equal("Công tác hỗ trợ người bệnh", Assert.Single(held.Candidates).Heading);
        Assert.DoesNotContain(plan.Sections, s => s.NewHeadingDepth is not null && s.Heading == "Công tác hỗ trợ người bệnh");
    });

    [Fact]
    public void A_missing_heading_is_added_once_under_its_parent_and_a_repeat_from_another_source_is_listed() => WithFiles((target, t8, t9) =>
    {
        var (sections, excluded) = GatherByHeading.Build(target, TargetOutline,
            [new GatherSourceFile("T8", t8, T8Outline), new GatherSourceFile("T9", t9, T9Outline)], addMissingHeadings: true);

        var results = Assert.Single(sections, s => s.Heading == "Kết quả truyền thông");
        Assert.Equal(1, results.NewHeadingDepth);
        Assert.Equal("Truyền thông:", results.NewHeadingParent);
        Assert.Equal(["Đạt 1 triệu lượt xem.", "Đạt 2 triệu lượt xem."], results.Run.Facts.Select(f => f.Render()));
        Assert.Contains(excluded, e => e.Fact.Render() == "Đạt 1 triệu lượt xem." && e.Reason == "ALREADY_FROM_OTHER_SOURCE: T8");
        var fund = Assert.Single(sections, s => s.Heading == "Quỹ Pastb:");
        Assert.Equal("Kế hoạch tháng 11/2026", fund.NewHeadingParent);
        Assert.Null(fund.NextHeadingLocation);
    });

    [Fact]
    public void Without_adding_headings_the_content_of_a_missing_heading_is_listed_as_not_placed() => WithFiles((target, t8, _) =>
    {
        var (sections, excluded) = GatherByHeading.Build(target, TargetOutline, [new GatherSourceFile("T8", t8, T8Outline)]);

        Assert.DoesNotContain(sections, s => s.NewHeadingDepth is not null);
        Assert.Contains(excluded, e => e.Fact.Render() == "Đạt 1 triệu lượt xem." && e.Reason.StartsWith("UNPLACED:", StringComparison.Ordinal));
        Assert.Contains(excluded, e => e.Fact.Render() == "Rà soát hồ sơ." && e.Reason.StartsWith("UNPLACED:", StringComparison.Ordinal));
    });

    [Fact]
    public void A_heading_with_a_name_close_to_a_target_heading_is_held_as_uncertain_with_its_children_and_never_added()
    {
        var target = RawDocxFixtureTests.Build(P("Công tác xã hội:") + P("Công tác tiếp nhận và hỗ trợ từ thiện") + P("Tiếp đón 66 lượt."));
        var source = RawDocxFixtureTests.Build(P("Công tác xã hội:") + P("Tiếp nhận và hỗ trợ từ thiện") + P("Tiếp đón 50 lượt.")
            + P("Chuyến xe 0 đồng") + P("Đưa 3 người bệnh về nhà."));
        try
        {
            var plan = GatherByHeading.Build(target, [H(0, "Công tác xã hội:", 1), H(1, "Công tác tiếp nhận và hỗ trợ từ thiện", 2)],
                [new GatherSourceFile("T8", source, [H(0, "Công tác xã hội:", 1), H(1, "Tiếp nhận và hỗ trợ từ thiện", 2), H(3, "Chuyến xe 0 đồng", 3)])],
                addMissingHeadings: true);

            Assert.DoesNotContain(plan.Sections, s => s.NewHeadingDepth is not null);
            Assert.All(plan.Sections, s => Assert.Empty(s.Run.Facts));
            Assert.Equal(["NEAR_NAME", "PARENT_UNCERTAIN"], plan.Uncertain.Select(u => u.Kind));
            var near = plan.Uncertain[0];
            Assert.Equal("Công tác tiếp nhận và hỗ trợ từ thiện", Assert.Single(near.Candidates).Heading);
            Assert.Equal(["Tiếp đón 66 lượt."], near.Candidates[0].Content);
            Assert.Equal(["Tiếp đón 50 lượt."], near.Facts.Select(f => f.Render()));
            Assert.Equal(["Đưa 3 người bệnh về nhà."], plan.Uncertain[1].Facts.Select(f => f.Render()));
        }
        finally { File.Delete(target); File.Delete(source); }
    }

    [Fact]
    public void A_source_heading_matching_several_target_headings_is_uncertain_with_all_of_them()
    {
        var target = RawDocxFixtureTests.Build(P("Thông tin chung") + P("A") + P("Thông tin chung") + P("B"));
        var source = RawDocxFixtureTests.Build(P("Thông tin chung") + P("Nguồn"));
        try
        {
            var plan = GatherByHeading.Build(target, [H(0, "Thông tin chung", 1), H(2, "Thông tin chung", 1)],
                [new GatherSourceFile("S", source, [H(0, "Thông tin chung", 1)])]);

            var held = Assert.Single(plan.Uncertain);
            Assert.Equal("AMBIGUOUS", held.Kind);
            Assert.Equal(["body[1]/p[1]", "body[1]/p[3]"], held.Candidates.Select(c => c.Location));
            Assert.Equal(["Nguồn"], held.Facts.Select(f => f.Render()));
        }
        finally { File.Delete(target); File.Delete(source); }
    }

    static string EndTable(string text) =>
        $"<w:tbl><w:tblPr/><w:tblGrid><w:gridCol w:w=\"2000\"/></w:tblGrid><w:tr><w:tc><w:p><w:r><w:t>{text}</w:t></w:r></w:p></w:tc></w:tr></w:tbl>";

    [Fact]
    public void The_table_that_ends_a_document_is_neither_copied_nor_written_after()
    {
        var target = RawDocxFixtureTests.Build(P("Quỹ:") + P("Rà soát hồ sơ.") + EndTable("NGƯỜI TỔNG HỢP Vũ Thị Hồng Hoa"));
        var source = RawDocxFixtureTests.Build(P("Quỹ:") + P("Thu 16 triệu đồng.") + EndTable("PHÒNG A Nguyễn Văn A"));
        var output = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".docx");
        try
        {
            var (sections, excluded) = GatherByHeading.Build(target, [H(0, "Quỹ:", 1)], [new GatherSourceFile("T8", source, [H(0, "Quỹ:", 1)])]);

            Assert.Equal(["Thu 16 triệu đồng."], sections[0].Run.Facts.Select(f => f.Render()));
            Assert.StartsWith("DOCUMENT_END_TABLE:", Assert.Single(excluded).Reason, StringComparison.Ordinal);
            var reviewed = sections[0] with { Run = sections[0].Run with { Facts = sections[0].Run.Facts.Select(f => f with { Flags = [] }).ToList() } };
            var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(target)));
            Assert.Empty(GatherWriteback.Apply(target, output, [reviewed], DateTime.UtcNow, true, hash).SkippedHeadings);
            using var document = WordprocessingDocument.Open(output, false);
            var body = document.MainDocumentPart!.Document!.Body!;
            var last = body.ChildElements.Last(e => e is not SectionProperties);
            Assert.IsType<Table>(last);
            Assert.StartsWith("- Thu 16 triệu đồng.", Assert.IsType<Paragraph>(last.PreviousSibling()).InnerText, StringComparison.Ordinal);
        }
        finally { File.Delete(target); File.Delete(source); File.Delete(output); }
    }

    [Fact]
    public void New_headings_are_written_at_the_end_of_their_parent_and_the_original_is_unchanged() => WithFiles((target, t8, t9) =>
    {
        var output = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".docx");
        static string Hash(string path) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
        try
        {
            var before = Hash(target);
            var (sections, _) = GatherByHeading.Build(target, TargetOutline,
                [new GatherSourceFile("T8", t8, T8Outline), new GatherSourceFile("T9", t9, T9Outline)], addMissingHeadings: true);
            var reviewed = sections.Select(s => s with
            {
                Run = s.Run with { Facts = s.Run.Facts.Select(f => f with { Flags = f.Flags.Where(x => x != "SCOPE_UNREVIEWED").ToArray() }).ToList() },
            }).ToList();

            var result = GatherWriteback.Apply(target, output, reviewed, DateTime.UtcNow, true, before);

            Assert.Empty(result.SkippedHeadings);
            Assert.Equal(before, Hash(target));
            using var document = WordprocessingDocument.Open(output, false);
            var paragraphs = document.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().ToList();
            var text = paragraphs.Select(p => p.InnerText).ToList();
            int At(string start) => text.FindIndex(t => t.StartsWith(start, StringComparison.Ordinal));
            Assert.True(At("- Đăng 3 bài về lao.") > At("Đã đăng 5 bài."));
            Assert.True(At("Kết quả truyền thông") > At("- Đăng 3 bài về lao."));
            Assert.True(At("- Đạt 2 triệu lượt xem.") > At("Kết quả truyền thông"));
            Assert.True(At("- Đạt 2 triệu lượt xem.") < At("Công tác xã hội:"));
            Assert.Equal(text.Count - 2, At("Quỹ Pastb:"));
            Assert.Equal("- Rà soát hồ sơ.", text[^1][..16]);
            var heading = paragraphs[At("Kết quả truyền thông")];
            Assert.NotNull(heading.Descendants<Bold>().FirstOrDefault());
            Assert.NotNull(heading.Descendants<InsertedRun>().FirstOrDefault());
        }
        finally { File.Delete(output); }
    });

    // A September report whose outline gives the title as two headings over the whole report ("BÁO CÁO", then its subject line), as one run of
    // the model does; the October target has no title heading.
    static readonly string TitledBody = P("BÁO CÁO") + P("Hoạt động chính tháng 9/2026 và kế hoạch tháng 10/2026") + P("Phòng A")
        + P("Hoạt động tháng 9/2026") + P("Truyền thông:") + P("Đăng 7 bài.") + P("Kế hoạch tháng 10/2026") + P("Truyền thông:") + P("Viết thêm bài.");

    static readonly HeadingRecord[] TitledOutline = [H(0, "BÁO CÁO", 1), H(1, "Hoạt động chính tháng 9/2026 và kế hoạch tháng 10/2026", 1),
        H(3, "Hoạt động tháng 9/2026", 2), H(4, "Truyền thông:", 3), H(6, "Kế hoạch tháng 10/2026", 2), H(7, "Truyền thông:", 3)];

    static void WithTitled(Action<string, string> test)
    {
        var target = RawDocxFixtureTests.Build(TargetBody);
        var titled = RawDocxFixtureTests.Build(TitledBody);
        try { test(target, titled); }
        finally { File.Delete(target); File.Delete(titled); }
    }

    [Fact]
    public void A_title_only_the_source_has_is_left_out_of_its_chains() => WithTitled((target, titled) =>
    {
        var plan = GatherByHeading.Build(target, TargetOutline, [new GatherSourceFile("T9", titled, TitledOutline)], addMissingHeadings: true);

        Assert.Equal(["Đăng 7 bài."], Named(plan.Sections, "Truyền thông:", 0).Run.Facts.Select(f => f.Render()));
        Assert.Equal(["Viết thêm bài."], Named(plan.Sections, "Truyền thông:", 1).Run.Facts.Select(f => f.Render()));
        Assert.Empty(plan.Uncertain);
        Assert.DoesNotContain(plan.Sections, s => s.NewHeadingDepth is not null);
        // The line inside the title block is listed, not dropped.
        Assert.Contains(plan.Excluded, e => e.Fact.Render() == "Phòng A" && e.Reason.StartsWith("DOCUMENT_TITLE", StringComparison.Ordinal));
    });

    [Fact]
    public void A_title_only_the_target_has_is_left_out_of_its_chains() => WithTitled((_, titled) =>
    {
        // The roles reversed: the titled report is the target, the August report (no title heading) the source.
        var t8File = RawDocxFixtureTests.Build(T8Body);
        try
        {
            var plan = GatherByHeading.Build(titled, TitledOutline, [new GatherSourceFile("T8", t8File, T8Outline)], addMissingHeadings: true);
            Assert.Equal(["Đăng 3 bài về lao."], Named(plan.Sections, "Truyền thông:", 0).Run.Facts.Select(f => f.Render()));
            Assert.Empty(Named(plan.Sections, "BÁO CÁO").Run.Facts);
        }
        finally { File.Delete(t8File); }
    });

    [Fact]
    public void A_title_both_sides_have_stays_in_the_chain() => WithTitled((_, titled) =>
    {
        var copy = RawDocxFixtureTests.Build(TitledBody.Replace("Đăng 7 bài.", "Đăng 8 bài."));
        try
        {
            var plan = GatherByHeading.Build(copy, TitledOutline, [new GatherSourceFile("T9", titled, TitledOutline)], addMissingHeadings: true);
            Assert.Equal(["Đăng 7 bài."], Named(plan.Sections, "Truyền thông:", 0).Run.Facts.Select(f => f.Render()));
            Assert.DoesNotContain(plan.Excluded, e => e.Reason.StartsWith("DOCUMENT_TITLE", StringComparison.Ordinal));
        }
        finally { File.Delete(copy); }
    });

    [Fact]
    public void A_chapter_the_target_also_has_is_not_taken_for_a_title()
    {
        // The source has only the first chapter, so that chapter holds every heading of the source; the target has it too, so it is a real parent.
        var target = RawDocxFixtureTests.Build(P("Chương I") + P("Điều 1") + P("Nội dung một.") + P("Chương II") + P("Điều 1") + P("Nội dung hai."));
        var source = RawDocxFixtureTests.Build(P("Chương I") + P("Điều 1") + P("Nội dung mới."));
        try
        {
            var plan = GatherByHeading.Build(target, [H(0, "Chương I", 1), H(1, "Điều 1", 2), H(3, "Chương II", 1), H(4, "Điều 1", 2)],
                [new GatherSourceFile("S", source, [H(0, "Chương I", 1), H(1, "Điều 1", 2)])], addMissingHeadings: true);
            Assert.Equal(["Nội dung mới."], Named(plan.Sections, "Điều 1", 0).Run.Facts.Select(f => f.Render()));
            Assert.Empty(Named(plan.Sections, "Điều 1", 1).Run.Facts);
        }
        finally { File.Delete(target); File.Delete(source); }
    }
}
