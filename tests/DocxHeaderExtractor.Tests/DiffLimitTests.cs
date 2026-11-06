using DocxHeaderExtractor.DocumentProcessing.Changes;
using DocxHeaderExtractor.DocumentProcessing.Gather;
using DocxHeaderExtractor.DocumentProcessing.Output;
using DocxHeaderExtractor.Sources.Docx.Changes;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Every caller of Myers, with the diff limit forced low for this test only: an alignment that stopped is reported as such and nothing in it is
/// read as unchanged or placed by a guess.
/// </summary>
public sealed class DiffLimitTests
{
    static string P(string text) => $"<w:p><w:r><w:t xml:space=\"preserve\">{text}</w:t></w:r></w:p>";

    static T Limited<T>(Func<T> run)
    {
        Myers.LimitOverride.Value = 1;
        try { return run(); }
        finally { Myers.LimitOverride.Value = null; }
    }

    static StoredOutline Stored(string[] signatures, string? version = OutlineStore.SignatureVersion) =>
        new(OutlineStore.Version, "sha", "k", "f.docx", DateTimeOffset.UtcNow,
            signatures.Select((s, i) => new StoredParagraph($"body[1]/p[{i + 1}]", i, s)).ToArray(), [],
            [new HeadingRecord { Index = 0, StableId = "body[1]/p[1]", Text = "A", Level = 1 }], SignatureVersion: version);

    static readonly string[] Old = ["a", "b", "c", "d", "e"];
    static readonly string[] New = ["a", "x", "y", "z", "e"];

    [Fact]
    public void Document_comparison_marks_every_change_for_review_and_says_it_is_partial()
    {
        var template = RawDocxFixtureTests.Build(P("Một") + P("Hai") + P("Ba") + P("Bốn"));
        var document = RawDocxFixtureTests.Build(P("Một") + P("Năm") + P("Sáu") + P("Bốn"));
        try
        {
            var (a, b) = (DocxVersionReader.Read(template), DocxVersionReader.Read(document));
            var result = Limited(() => DocxComparer.Compare(a, b));
            Assert.Contains("alignment_incomplete_diff_limit_exceeded", result.Scope);
            Assert.NotEmpty(result.Changes);
            Assert.All(result.Changes, c => Assert.True(c.Uncertain));
            Assert.DoesNotContain("alignment_incomplete", DocxComparer.Compare(a, b).Scope);
        }
        finally { File.Delete(template); File.Delete(document); }
    }

    [Fact]
    public void Text_diff_marks_its_hunks_coarse()
    {
        var hunks = Limited(() => TextDiff.Compare("một hai ba bốn", "năm sáu bảy tám"));
        Assert.All(hunks, h => Assert.True(h.Coarse));
        Assert.All(TextDiff.Compare("một hai ba bốn", "một hai bảy bốn"), h => Assert.False(h.Coarse));
    }

    [Fact]
    public void Outline_diff_reports_an_incomplete_comparison_first()
    {
        var result = Limited(() => OutlineDiff.Compare(Stored(Old), Stored(New)));
        Assert.Equal("COMPARISON_INCOMPLETE", result.Differences[0].Kind);
        Assert.True(result.Differences[0].Uncertain);
        Assert.NotEqual("COMPARISON_INCOMPLETE", OutlineDiff.Compare(Stored(Old), Stored(New)).Differences.FirstOrDefault()?.Kind);
    }

    [Fact]
    public void Outline_mapping_and_region_planning_extract_again()
    {
        var current = New.Select((s, i) => new StoredParagraph($"body[1]/p[{i + 1}]", i, s)).ToArray();
        Assert.Equal("DIFF_LIMIT_EXCEEDED", Limited(() => OutlineMap.Map(Stored(Old), current, [])).Reason);
        var plan = Limited(() => OutlineRegion.Plan(Stored(Old), current, []));
        Assert.Equal("FULL", plan.Status);
        Assert.Equal("DIFF_LIMIT_EXCEEDED", plan.Reason);
    }

    [Fact]
    public void Signatures_of_another_version_are_never_compared()
    {
        var current = Old.Select((s, i) => new StoredParagraph($"body[1]/p[{i + 1}]", i, s)).ToArray();
        Assert.Equal("SIGNATURE_VERSION_CHANGED", OutlineMap.Map(Stored(Old, version: null), current, []).Reason);
        Assert.Equal("SIGNATURE_VERSION_CHANGED", OutlineRegion.Plan(Stored(Old, version: null), current, []).Reason);
        Assert.Equal("COMPARISON_INCOMPLETE", OutlineDiff.Compare(Stored(Old, version: null), Stored(Old)).Differences[0].Kind);
    }

    [Fact]
    public void Order_matching_places_nothing_it_could_not_align()
    {
        var target = RawDocxFixtureTests.Build(P("Một") + P("Nội dung đích") + P("Hai") + P("Ba"));
        var source = RawDocxFixtureTests.Build(P("Bốn") + P("Nội dung nguồn") + P("Năm") + P("Sáu"));
        static HeadingRecord H(int index, string text) => new() { Index = index, StableId = $"body[1]/p[{index + 1}]", Text = text, Level = 1 };
        try
        {
            var plan = Limited(() => GatherByHeading.Build(target, [H(0, "Một"), H(2, "Hai"), H(3, "Ba")],
                [new GatherSourceFile("S", source, [H(0, "Bốn"), H(2, "Năm"), H(3, "Sáu")])], addMissingHeadings: true, matching: GatherMatching.Order));
            Assert.All(plan.Sections, s => Assert.Empty(s.Run.Facts));
            Assert.DoesNotContain(plan.Sections, s => s.NewHeadingDepth is not null);
            Assert.Contains(plan.Excluded, e => e.Fact.Render() == "Nội dung nguồn" && e.Reason.Contains("vượt giới hạn", StringComparison.Ordinal));
        }
        finally { File.Delete(target); File.Delete(source); }
    }

    static DocxComparer.Context NewContext() => new([], [], [], new ComparerOptions(), null);

    static T WithLimit<T>(int limit, Func<T> run)
    {
        Myers.LimitOverride.Value = limit;
        try { return run(); }
        finally { Myers.LimitOverride.Value = null; }
    }

    [Fact]
    public void A_complete_paragraph_alignment_with_an_incomplete_similarity_is_still_reported_partial()
    {
        // The paragraph alignment needs 2 steps (one paragraph each side) and completes within the limit of 2; the token diff of the two long
        // paragraphs, used to pair them, needs more and stops. "Not compared in full" must not read as "compared and different".
        var template = RawDocxFixtureTests.Build(P("Đầu") + P("một hai ba bốn năm sáu bảy tám") + P("Cuối"));
        var document = RawDocxFixtureTests.Build(P("Đầu") + P("một chín ba mười năm mươi bảy trăm") + P("Cuối"));
        try
        {
            var (a, b) = (DocxVersionReader.Read(template), DocxVersionReader.Read(document));
            var result = WithLimit(2, () => DocxComparer.Compare(a, b));
            Assert.Contains("alignment_incomplete_diff_limit_exceeded", result.Scope);
            Assert.All(result.Changes, c => Assert.True(c.Uncertain));
            var (score, done) = WithLimit(2, () => (DocxComparer.Dice(["a", "b", "c", "d"], ["a", "x", "c", "y"], out var complete), complete));
            Assert.False(done);
            Assert.Equal(0, score);
        }
        finally { File.Delete(template); File.Delete(document); }
    }

    static string Table(params string[] rows) =>
        "<w:tbl>" + string.Concat(rows.Select(r => $"<w:tr><w:tc>{P(r)}</w:tc></w:tr>")) + "</w:tbl>";

    [Fact]
    public void The_row_alignment_of_two_tables_reports_an_incomplete_alignment()
    {
        // Three different one-word rows each side: the rows need 6 steps; pairing two one-word rows needs at most 2.
        var template = RawDocxFixtureTests.Build(Table("r1", "r2", "r3"));
        var document = RawDocxFixtureTests.Build(Table("s1", "s2", "s3"));
        try
        {
            var t = TableDiff.Units(DocxVersionReader.Read(template).Paragraphs).OfType<TableShape>().Single();
            var d = TableDiff.Units(DocxVersionReader.Read(document).Paragraphs).OfType<TableShape>().Single();
            var limited = NewContext();
            WithLimit(2, () => { TableDiff.CompareTables(t, d, "paired_by_similarity", false, limited); return 0; });
            Assert.True(limited.Alignment.Incomplete);
            var full = NewContext();
            TableDiff.CompareTables(t, d, "paired_by_similarity", false, full);
            Assert.False(full.Alignment.Incomplete);
        }
        finally { File.Delete(template); File.Delete(document); }
    }

    [Fact]
    public void The_paragraphs_inside_a_cell_report_an_incomplete_alignment()
    {
        var a = DocxVersionReader.Read(RawDocxFixtureTests.Build(P("x1") + P("x2") + P("x3")));
        var b = DocxVersionReader.Read(RawDocxFixtureTests.Build(P("y1") + P("y2") + P("y3")));
        var limited = NewContext();
        WithLimit(2, () => { DocxComparer.CompareSequence(a.Paragraphs, b.Paragraphs, limited); return 0; });
        Assert.True(limited.Alignment.Incomplete);
        var full = NewContext();
        DocxComparer.CompareSequence(a.Paragraphs, b.Paragraphs, full);
        Assert.False(full.Alignment.Incomplete);
    }

    static GatheredFact Fact(string text) => new([new SourcedPart("u1", "S", "body[1]/p[9]", "text", text)], []);

    [Fact]
    public void A_conflict_check_that_stopped_flags_the_fact_instead_of_clearing_it()
    {
        // The source line differs from the target line only by its number: a full check flags POSSIBLE_CONFLICT; a check that stopped at the limit
        // must not read as "no conflict".
        var section = new GatherSection("body[1]/p[1]", "Mục A", "Mục A" + "\n" + "Tiếp đón 66 lượt người bệnh tại quầy thông tin",
            new GatherRun([Fact("Tiếp đón 50 lượt người bệnh tại quầy thông tin")], [], [], [], [], [], 1), []);
        var full = GatherReviewApply.RemoveAlreadyInTarget(section).Section.Run.Facts.Single();
        Assert.Contains(full.Flags, f => f.StartsWith("POSSIBLE_CONFLICT", StringComparison.Ordinal));
        Assert.DoesNotContain(GatherReviewApply.UncheckedFlag, full.Flags);
        var limited = WithLimit(1, () => GatherReviewApply.RemoveAlreadyInTarget(section)).Section.Run.Facts.Single();
        Assert.Contains(GatherReviewApply.UncheckedFlag, limited.Flags);
    }

    [Fact]
    public void The_gather_flags_an_unchecked_conflict_and_holds_an_unchecked_near_name()
    {
        // Same heading: the source fact is compared with the target's lines. Under "R" the source also has a heading the target lacks, whose name
        // is compared with the target's headings for a near name.
        var target = RawDocxFixtureTests.Build(P("R") + P("Mục A") + P("Tiếp đón 66 lượt người bệnh tại quầy") + P("Công tác tiếp nhận hỗ trợ"));
        var source = RawDocxFixtureTests.Build(P("R") + P("Mục A") + P("Tiếp đón 50 lượt người bệnh tại quầy") + P("Tiếp nhận hỗ trợ khách hàng") + P("Nội dung mới"));
        static HeadingRecord H(int index, string text, int level) => new() { Index = index, StableId = $"body[1]/p[{index + 1}]", Text = text, Level = level };
        try
        {
            GatherHeadingPlan Plan() => GatherByHeading.Build(target, [H(0, "R", 1), H(1, "Mục A", 2), H(3, "Công tác tiếp nhận hỗ trợ", 2)],
                [new GatherSourceFile("S", source, [H(0, "R", 1), H(1, "Mục A", 2), H(3, "Tiếp nhận hỗ trợ khách hàng", 2)])], addMissingHeadings: true);
            var plan = WithLimit(1, Plan);
            var fact = plan.Sections.Single(s => s.Heading == "Mục A").Run.Facts.Single();
            Assert.Contains(GatherReviewApply.UncheckedFlag, fact.Flags);
            var held = Assert.Single(plan.Uncertain, u => u.SourceHeading == "Tiếp nhận hỗ trợ khách hàng");
            Assert.Equal("NEAR_NAME", held.Kind);
            Assert.Contains("chưa so được đầy đủ", held.Reason);
            Assert.DoesNotContain(plan.Sections, s => s.NewHeadingDepth is not null);

            var full = Plan();
            Assert.DoesNotContain(GatherReviewApply.UncheckedFlag, full.Sections.Single(s => s.Heading == "Mục A").Run.Facts.Single().Flags);
            Assert.DoesNotContain(full.Uncertain, u => u.Reason.Contains("chưa so được đầy đủ", StringComparison.Ordinal));
        }
        finally { File.Delete(target); File.Delete(source); }
    }
}
