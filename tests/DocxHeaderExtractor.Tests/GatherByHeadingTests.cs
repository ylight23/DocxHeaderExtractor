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

    [Fact]
    public void Source_with_no_matched_heading_keeps_all_text_for_review()
    {
        var target = RawDocxFixtureTests.Build(P("Mục A") + P("Nội dung đích"));
        var source = RawDocxFixtureTests.Build(P("Dòng nguồn thứ nhất") + P("Dòng nguồn thứ hai"));
        try
        {
            var plan = GatherByHeading.Build(target, [H(0, "Mục A", "body[1]/p[1]")],
                [new GatherSourceFile("S", source, [H(5, "Heading không có", "body[1]/p[6]")])]);
            var held = Assert.Single(plan.Uncertain);
            Assert.Equal("UNCONFIRMED_HIERARCHY", held.Kind);
            Assert.Equal(new[] { "Dòng nguồn thứ nhất", "Dòng nguồn thứ hai" }, held.Facts.Select(f => f.Render()));
            Assert.Contains(plan.Gaps, g => g.SourceId == "S" && g.Reason.StartsWith("heading_unmatched", StringComparison.Ordinal));
        }
        finally { File.Delete(target); File.Delete(source); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Partial_selection_holds_affected_content_and_reports_coverage(bool targetIncomplete)
    {
        var target = RawDocxFixtureTests.Build(P("Mục A") + P("Nội dung đích"));
        var source = RawDocxFixtureTests.Build(P("Mục A") + P("Nội dung nguồn"));
        try
        {
            var heading = H(0, "Mục A", "body[1]/p[1]");
            var plan = GatherByHeading.Build(target, [heading],
                [new GatherSourceFile("S", source, [heading]) { SelectionComplete = targetIncomplete }],
                targetSelectionComplete: !targetIncomplete);
            Assert.All(plan.Sections, section => Assert.Empty(section.Run.Facts));
            var held = Assert.Single(plan.Uncertain);
            Assert.Equal(targetIncomplete ? "TARGET_OUTLINE_INCOMPLETE" : "UNCONFIRMED_HIERARCHY", held.Kind);
            Assert.Contains(held.Facts, f => f.Render() == "Nội dung nguồn");
            Assert.Contains(plan.Gaps, g => g.SourceId == (targetIncomplete ? "target" : "S") && g.Reason == "selection_incomplete");
            var decision = GatherDecisions.Apply(plan.Sections, plan.Uncertain, [new(0, "NEW")]);
            Assert.Equal("OUTLINE_REVIEW_REQUIRED", Assert.Single(decision.Outcomes).Error);
        }
        finally { File.Delete(target); File.Delete(source); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Leading_outside_tree_title_does_not_block_section_matching(bool onTarget)
    {
        var target = RawDocxFixtureTests.Build(P("BÁO CÁO") + P("Thông tin chung") + P("Nội dung đích"));
        var source = RawDocxFixtureTests.Build(P("BÁO CÁO") + P("Thông tin chung") + P("Nội dung nguồn"));
        try
        {
            var title = new HeadingRecord { Index = 0, Text = "BÁO CÁO", StableId = "body[1]/p[1]", Level = null, HierarchyResolution = "model-out-of-hierarchy" };
            var section = H(1, "Thông tin chung", "body[1]/p[2]");
            var withoutTitle = new[] { section };
            var withTitle = new[] { title, section };
            var plan = GatherByHeading.Build(target, onTarget ? withTitle : withoutTitle,
                [new GatherSourceFile("S", source, onTarget ? withoutTitle : withTitle)], true, GatherMatching.Order);
            Assert.DoesNotContain(plan.Uncertain, u => u.Kind == "UNCONFIRMED_HIERARCHY");
            Assert.Equal("Nội dung nguồn", Assert.Single(Assert.Single(plan.Sections).Run.Facts).Render());
            if (!onTarget) Assert.Contains(plan.Excluded, e => e.Fact.Render() == "BÁO CÁO");
        }
        finally { File.Delete(target); File.Delete(source); }
    }

    [Fact]
    public void Leading_outside_tree_titles_on_both_documents_do_not_absorb_content()
    {
        var target = RawDocxFixtureTests.Build(P("BÁO CÁO") + P("Thông tin chung") + P("Nội dung đích"));
        var source = RawDocxFixtureTests.Build(P("BÁO CÁO") + P("Phần mở đầu cần xem") + P("Thông tin chung") + P("Nội dung nguồn"));
        try
        {
            static HeadingRecord Title() => new() { Index = 0, Text = "BÁO CÁO", StableId = "body[1]/p[1]", Level = null, HierarchyResolution = "model-out-of-hierarchy" };
            var targetOutline = new[] { Title(), H(1, "Thông tin chung", "body[1]/p[2]") };
            var sourceOutline = new[] { Title(), H(2, "Thông tin chung", "body[1]/p[3]") };
            var plan = GatherByHeading.Build(target, targetOutline, [new GatherSourceFile("S", source, sourceOutline)], true, GatherMatching.Order);
            Assert.DoesNotContain(plan.Uncertain, u => u.Kind == "UNCONFIRMED_HIERARCHY");
            Assert.Equal("Nội dung nguồn", Assert.Single(Assert.Single(plan.Sections).Run.Facts).Render());
            Assert.Contains(plan.Excluded, e => e.Fact.Render() == "Phần mở đầu cần xem" && e.Reason.StartsWith("BEFORE_FIRST_HEADING"));
        }
        finally { File.Delete(target); File.Delete(source); }
    }

    [Fact]
    public void Outside_tree_heading_in_the_middle_is_held_until_its_boundary_is_safe()
    {
        var target = RawDocxFixtureTests.Build(P("Mục 1") + P("Nội dung"));
        var source = RawDocxFixtureTests.Build(P("Mục 1") + P("Nội dung") + P("Nhãn ngoài cây") + P("Đoạn sau nhãn"));
        try
        {
            var section = H(0, "Mục 1", "body[1]/p[1]");
            var outside = new HeadingRecord { Index = 2, Text = "Nhãn ngoài cây", StableId = "body[1]/p[3]", Level = null, HierarchyResolution = "model-out-of-hierarchy" };
            var plan = GatherByHeading.Build(target, [section], [new GatherSourceFile("S", source, [section, outside])]);
            Assert.Equal("UNCONFIRMED_HIERARCHY", Assert.Single(plan.Uncertain).Kind);
            Assert.Contains(Assert.Single(plan.Uncertain).Facts, f => f.Render() == "Đoạn sau nhãn");
        }
        finally { File.Delete(target); File.Delete(source); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Unknown_level_holds_source_text_and_refuses_a_guessed_insertion(bool targetUnknown)
    {
        var target = RawDocxFixtureTests.Build(P("Thông tin chung") + P("Nội dung đích"));
        var source = RawDocxFixtureTests.Build(P("Thông tin chung") + P("Nội dung nguồn"));
        try
        {
            var t = H(0, "Thông tin chung", "body[1]/p[1]");
            var s = H(0, "Thông tin chung", "body[1]/p[1]");
            if (targetUnknown) t.Level = null; else s.Level = null;
            var plan = GatherByHeading.Build(target, [t], [new GatherSourceFile("S", source, [s])], true, GatherMatching.Order);
            Assert.All(plan.Sections, section => Assert.Empty(section.Run.Facts));
            var held = Assert.Single(plan.Uncertain);
            Assert.Equal("UNCONFIRMED_HIERARCHY", held.Kind);
            Assert.Contains(held.Facts, f => f.Render() == "Thông tin chung");
            Assert.Contains(held.Facts, f => f.Render() == "Nội dung nguồn");
            var applied = GatherDecisions.Apply(plan.Sections, plan.Uncertain, [new(0, "NEW")]);
            Assert.Equal("OUTLINE_REVIEW_REQUIRED", Assert.Single(applied.Outcomes).Error);
            Assert.Contains(0, applied.Undecided);
            Assert.DoesNotContain(applied.Sections, section => section.NewHeadingDepth is not null);
        }
        finally { File.Delete(target); File.Delete(source); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Only_source_with_unknown_hierarchy_is_held(bool badFirst)
    {
        var target = RawDocxFixtureTests.Build(P("Thông tin chung") + P("Nội dung đích"));
        var good = RawDocxFixtureTests.Build(P("Thông tin chung") + P("Nội dung nguồn tốt"));
        var bad = RawDocxFixtureTests.Build(P("Thông tin chung") + P("Nội dung nguồn chưa rõ"));
        try
        {
            var heading = H(0, "Thông tin chung", "body[1]/p[1]");
            var unknown = H(0, "Thông tin chung", "body[1]/p[1]");
            unknown.Level = null;
            var goodSource = new GatherSourceFile("good", good, [heading]);
            var badSource = new GatherSourceFile("bad", bad, [unknown]);
            var sources = badFirst ? new[] { badSource, goodSource } : new[] { goodSource, badSource };
            var plan = GatherByHeading.Build(target, [heading], sources, true, GatherMatching.Order);
            var fact = Assert.Single(Assert.Single(plan.Sections).Run.Facts);
            Assert.Equal("Nội dung nguồn tốt", fact.Render());
            Assert.All(fact.Parts, p => Assert.Equal("good", p.SourceId));
            var held = Assert.Single(plan.Uncertain);
            Assert.Equal("UNCONFIRMED_HIERARCHY", held.Kind);
            Assert.Equal("bad", held.Source);
            Assert.Contains(held.Facts, f => f.Render() == "Nội dung nguồn chưa rõ");
            var applied = GatherDecisions.Apply(plan.Sections, plan.Uncertain, [new(0, "NEW")]);
            Assert.Equal("OUTLINE_REVIEW_REQUIRED", Assert.Single(applied.Outcomes).Error);
        }
        finally { File.Delete(target); File.Delete(good); File.Delete(bad); }
    }

    [Fact]
    public void Unknown_target_hierarchy_holds_all_sources()
    {
        var target = RawDocxFixtureTests.Build(P("Thông tin chung"));
        var first = RawDocxFixtureTests.Build(P("Thông tin chung") + P("Nguồn một"));
        var second = RawDocxFixtureTests.Build(P("Thông tin chung") + P("Nguồn hai"));
        try
        {
            var unknown = H(0, "Thông tin chung", "body[1]/p[1]");
            unknown.Level = null;
            var heading = H(0, "Thông tin chung", "body[1]/p[1]");
            var plan = GatherByHeading.Build(target, [unknown],
                [new GatherSourceFile("one", first, [heading]), new GatherSourceFile("two", second, [heading])]);
            Assert.All(plan.Sections, s => Assert.Empty(s.Run.Facts));
            Assert.Equal(new[] { "one", "two" }, plan.Uncertain.Select(u => u.Source));
            Assert.All(plan.Uncertain, u => Assert.Equal("UNCONFIRMED_HIERARCHY", u.Kind));
        }
        finally { File.Delete(target); File.Delete(first); File.Delete(second); }
    }

    [Fact]
    public void Unreviewed_source_is_held_without_suppressing_reviewed_source()
    {
        var target = RawDocxFixtureTests.Build(P("Thông tin chung") + P("Nội dung đích"));
        var reviewed = RawDocxFixtureTests.Build(P("Thông tin chung") + P("Nội dung đã duyệt"));
        var pending = RawDocxFixtureTests.Build(P("Thông tin chung") + P("Nội dung chưa duyệt"));
        try
        {
            var heading = H(0, "Thông tin chung", "body[1]/p[1]");
            var plan = GatherByHeading.Build(target, [heading], [
                new GatherSourceFile("pending", pending, [heading]) { OutlineReviewRequired = true },
                new GatherSourceFile("reviewed", reviewed, [heading])]);
            Assert.Equal("Nội dung đã duyệt", Assert.Single(Assert.Single(plan.Sections).Run.Facts).Render());
            var held = Assert.Single(plan.Uncertain);
            Assert.Equal("SOURCE_OUTLINE_REVIEW_REQUIRED", held.Kind);
            Assert.Equal("pending", held.Source);
            Assert.Contains(held.Facts, f => f.Render() == "Nội dung chưa duyệt");
            var applied = GatherDecisions.Apply(plan.Sections, plan.Uncertain, [new(0, "NEW")]);
            Assert.Equal("OUTLINE_REVIEW_REQUIRED", Assert.Single(applied.Outcomes).Error);
        }
        finally { File.Delete(target); File.Delete(reviewed); File.Delete(pending); }
    }

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

            // Tables are not gathered: the row stays one fact, listed as not added instead of being written.
            Assert.Equal(["Ông A, Bà B", "on"], sections[0].Run.Facts.Select(f => f.Render()));
            var table = Assert.Single(excluded);
            Assert.Equal("financed by the FIF.", table.Fact.Render());
            Assert.StartsWith("TABLE_NOT_GATHERED:", table.Reason, StringComparison.Ordinal);
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

    static string FormRow(params string[] cells) => "<w:tr>" + string.Concat(cells.Select(c => "<w:tc>" + P(c) + "</w:tc>")) + "</w:tr>";
    static string FormTable(params string[] rows) => "<w:tbl>" + string.Concat(rows) + "</w:tbl>";

    [Fact]
    public void Multi_paragraph_source_title_heading_with_changed_numbers_remains_in_appendix()
    {
        var target = RawDocxFixtureTests.Build(P("Báo cáo tháng 10") + P("Kế hoạch tháng 11") + P("Mục A") + P("Nội dung đích"));
        var source = RawDocxFixtureTests.Build(P("Báo cáo tháng 9") + P("Kế hoạch tháng 10") + P("Mục A") + P("Nội dung nguồn"));
        var output = Path.Combine(Path.GetTempPath(), "dhx-source-title-" + Guid.NewGuid().ToString("N") + ".docx");
        try
        {
            var plan = GatherByHeading.Build(target, [H(2, "Mục A", "body[1]/p[3]")],
                [new GatherSourceFile("S", source, [H(0, "Báo cáo tháng 9 Kế hoạch tháng 10", "body[1]/p[1]"), H(2, "Mục A", "body[1]/p[3]", 2)])]);
            var title = plan.Excluded.Where(e => e.Reason.StartsWith("FORM_HEADER_DIGITS", StringComparison.Ordinal)).ToList();
            Assert.Equal(new[] { "Báo cáo tháng 9", "Kế hoạch tháng 10" }, title.Select(e => e.Fact.Render()));
            GatherWriteback.Export(target, output, GatherByHeading.DecidedByChain(plan.Sections).Where(s => s.Run.Facts.Count > 0).ToList(),
                plan.Uncertain, DateTime.UtcNow, gaps: plan.Gaps, excluded: plan.Excluded);
            using var doc = WordprocessingDocument.Open(output, false);
            var text = doc.MainDocumentPart!.Document!.Body!.InnerText;
            Assert.Contains("Báo cáo tháng 9", text);
            Assert.Contains("Kế hoạch tháng 10", text);
            Assert.Contains("chỉ khác chữ số; cần xem số", text);
        }
        finally { File.Delete(target); File.Delete(source); if (File.Exists(output)) File.Delete(output); }
    }

    [Theory]
    [InlineData("Báo cáo tháng 9/2026", "FORM_HEADER", false)]
    [InlineData("Báo cáo tháng 10/2026", "FORM_HEADER_DIGITS", true)]
    [InlineData("Ghi chú: hoãn họp ngày 5", "BEFORE_FIRST_HEADING", true)]
    public void Only_an_exactly_matching_opening_paragraph_leaves_the_appendix_and_tables_are_not_gathered(string opening, string reason, bool inAppendix)
    {
        // The opening paragraph is in the same place in both files. Only the exact same text is the shared form. A change only in digits may be a
        // new period or a changed figure, so it stays in the appendix with its own label; other words stay as unplaced opening content.
        // Tables at the start and end are not gathered in any case.
        var target = RawDocxFixtureTests.Build(FormTable(FormRow("Đơn vị X", "Ngày 1")) + P("Báo cáo tháng 9/2026") + P("Mục A") + P("Nội dung đích")
            + FormTable(FormRow("Người lập: An")));
        var source = RawDocxFixtureTests.Build(FormTable(FormRow("Đơn vị X", "Ngày 2")) + P(opening)
            + P("Mục A") + P("Nội dung nguồn") + FormTable(FormRow("Tổng kinh phí", "150")));
        var output = Path.Combine(Path.GetTempPath(), "dhx-form-" + Guid.NewGuid().ToString("N") + ".docx");
        try
        {
            var heading = H(0, "Mục A", "body[1]/p[2]");
            var plan = GatherByHeading.Build(target, [heading], [new GatherSourceFile("S", source, [heading])]);
            Assert.Equal(["Nội dung nguồn"], plan.Sections.Single(s => s.Heading == "Mục A").Run.Facts.Select(f => f.Render()));
            string ReasonOf(string text) => plan.Excluded.Single(e => e.Fact.Render().Contains(text, StringComparison.Ordinal)).Reason.Split(':')[0];
            Assert.Equal("TABLE_NOT_GATHERED", ReasonOf("Ngày 2"));
            Assert.Equal("TABLE_NOT_GATHERED", ReasonOf("150"));
            Assert.Equal(reason, ReasonOf(opening));

            GatherWriteback.Export(target, output, GatherByHeading.DecidedByChain(plan.Sections).Where(s => s.Run.Facts.Count > 0).ToList(),
                plan.Uncertain, DateTime.UtcNow, gaps: plan.Gaps, excluded: plan.Excluded);
            using var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(output, false);
            var text = string.Join(" | ", doc.MainDocumentPart!.Document!.Body!.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>().Select(p => p.InnerText));
            var appendix = text.IndexOf(GatherWriteback.AppendixTitle, StringComparison.Ordinal);
            var tail = appendix < 0 ? "" : text[appendix..];
            Assert.DoesNotContain("Ngày 2", tail);
            Assert.DoesNotContain("150", tail);
            Assert.DoesNotContain("[Chưa ghép — ]", text);
            Assert.Equal(inAppendix, tail.Contains(opening, StringComparison.Ordinal));
            if (reason == "FORM_HEADER_DIGITS") Assert.Contains("chỉ khác chữ số", tail);
        }
        finally { File.Delete(target); File.Delete(source); File.Delete(output); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_held_source_keeps_its_paragraphs_for_review_but_not_its_tables(bool targetIncomplete)
    {
        var target = RawDocxFixtureTests.Build(P("Mục A") + P("Nội dung đích"));
        var source = RawDocxFixtureTests.Build(P("Mục A") + P("Nội dung nguồn") + FormTable(FormRow("Bảng số liệu", "120")));
        try
        {
            var heading = H(0, "Mục A", "body[1]/p[1]");
            var plan = GatherByHeading.Build(target, [heading],
                [new GatherSourceFile("S", source, [heading]) { SelectionComplete = targetIncomplete }], targetSelectionComplete: !targetIncomplete);
            var held = Assert.Single(plan.Uncertain);
            Assert.Contains(held.Facts, f => f.Render() == "Nội dung nguồn");
            Assert.DoesNotContain(held.Facts, f => f.Render().Contains("120", StringComparison.Ordinal));
            var table = Assert.Single(plan.Excluded, e => e.Fact.Render().Contains("120", StringComparison.Ordinal));
            Assert.StartsWith("TABLE_NOT_GATHERED:", table.Reason, StringComparison.Ordinal);
        }
        finally { File.Delete(target); File.Delete(source); }
    }

    [Fact]
    public void Text_already_under_a_same_named_candidate_is_shown_as_evidence_never_removed_and_a_merge_leaves_it_out()
    {
        // "Mục X" is directly under "R" in the source and under "R > A" in the target: the parent chain differs, so the candidate is not confirmed
        // as the same section (an outline may give a heading another level, or the two may be distinct sections).
        var target = RawDocxFixtureTests.Build(P("R") + P("A") + P("Mục X") + P("Đoạn chung"));
        var source = RawDocxFixtureTests.Build(P("R") + P("Mục X") + P("Đoạn chung") + P("Đoạn mới"));
        var output = Path.Combine(Path.GetTempPath(), "dhx-same-" + Guid.NewGuid().ToString("N") + ".docx");
        try
        {
            var plan = GatherByHeading.Build(target, [H(0, "R", "body[1]/p[1]"), H(1, "A", "body[1]/p[2]", 2), H(2, "Mục X", "body[1]/p[3]", 3)],
                [new GatherSourceFile("S", source, [H(0, "R", "body[1]/p[1]"), H(1, "Mục X", "body[1]/p[2]", 2)])]);
            var index = plan.Uncertain.ToList().FindIndex(u => u.SourceHeading == "Mục X");
            Assert.True(index >= 0);
            var held = plan.Uncertain[index];
            Assert.Equal("PATH_DIFFERS", held.Kind);
            Assert.Equal(["Đoạn chung", "Đoạn mới"], held.Facts.Select(f => f.Render()));
            var same = GatherDecisions.SameAsCandidate(held, 0);
            Assert.Equal([0], same);

            // Not confirmed: both facts stay in the appendix, with the evidence line.
            GatherWriteback.Export(target, output, GatherByHeading.DecidedByChain(plan.Sections).Where(s => s.Run.Facts.Count > 0).ToList(),
                plan.Uncertain, DateTime.UtcNow, gaps: plan.Gaps, excluded: plan.Excluded);
            using (var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(output, false))
            {
                var text = string.Join(" | ", doc.MainDocumentPart!.Document!.Body!.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>().Select(p => p.InnerText));
                var tail = text[text.IndexOf(GatherWriteback.AppendixTitle, StringComparison.Ordinal)..];
                Assert.Contains("Đoạn chung", tail);
                Assert.Contains("Đoạn mới", tail);
                Assert.Contains("1/2 đoạn trùng chữ với nội dung mục ứng viên “Mục X” (bỏ qua khác biệt khoảng trắng); vị trí ghép chưa được xác nhận", tail);
            }

            // The reader confirms the candidate: the duplicate is left out, the other fact is written.
            var merged = GatherDecisions.Apply(plan.Sections, plan.Uncertain, [new(index, "MERGE", 0)]);
            var outcome = Assert.Single(merged.Outcomes);
            Assert.Null(outcome.Error);
            Assert.Equal(1, outcome.Written);
            Assert.Equal(["Đoạn chung"], outcome.AlreadyThere);
        }
        finally { File.Delete(target); File.Delete(source); File.Delete(output); }
    }
}
