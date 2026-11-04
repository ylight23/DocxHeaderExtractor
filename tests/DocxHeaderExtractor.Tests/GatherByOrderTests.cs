using DocxHeaderExtractor.DocumentProcessing.Gather;
using DocxHeaderExtractor.DocumentProcessing.Output;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Matching by order, without a model: the target's outline is the frame; the source's own levels and parents are not read. Each case builds the
/// documents and their outlines, and checks that the target's structure is kept, that nothing unplaceable is written, and that no fact is written twice.
/// </summary>
public sealed class GatherByOrderTests
{
    static string P(string text) => $"<w:p><w:r><w:t xml:space=\"preserve\">{text}</w:t></w:r></w:p>";

    static HeadingRecord H(int index, string text, int level) => new() { Index = index, Level = level, Text = text, StableId = $"body[1]/p[{index + 1}]" };

    static readonly string TargetBody = P("Hoạt động tháng 10/2026") + P("Truyền thông:") + P("Đã đăng 5 bài.") + P("Công tác xã hội:")
        + P("Hỗ trợ 10 người.") + P("Kế hoạch tháng 11/2026") + P("Truyền thông:") + P("Tiếp tục đăng bài.");

    static readonly HeadingRecord[] TargetOutline = [H(0, "Hoạt động tháng 10/2026", 1), H(1, "Truyền thông:", 2), H(3, "Công tác xã hội:", 2),
        H(5, "Kế hoạch tháng 11/2026", 1), H(6, "Truyền thông:", 2)];

    static GatherSection Named(IReadOnlyList<GatherSection> sections, string heading, int nth = 0) =>
        sections.Where(s => s.Heading == heading).ElementAt(nth);

    static IEnumerable<string> Written(GatherHeadingPlan plan) =>
        plan.Sections.SelectMany(s => s.Run.Facts).Select(f => f.Render());

    /// <summary>Builds the target and the sources, runs the order matching, and deletes the files.</summary>
    static void Run(string targetBody, HeadingRecord[] targetOutline, (string Body, HeadingRecord[] Outline)[] sources,
        Action<GatherHeadingPlan> check)
    {
        var target = RawDocxFixtureTests.Build(targetBody);
        var files = sources.Select(s => RawDocxFixtureTests.Build(s.Body)).ToList();
        try
        {
            var plan = GatherByHeading.Build(target, targetOutline,
                sources.Select((s, i) => new GatherSourceFile("S" + (i + 1), files[i], s.Outline)).ToList(),
                addMissingHeadings: true, matching: GatherMatching.Order);
            check(plan);
        }
        finally
        {
            File.Delete(target);
            foreach (var f in files) File.Delete(f);
        }
    }

    [Fact]
    public void Levels_and_parents_of_the_source_are_not_read() => Run(TargetBody, TargetOutline,
        [(P("Hoạt động tháng 10/2026") + P("Truyền thông:") + P("Đăng 3 bài.") + P("Công tác xã hội:") + P("Hỗ trợ 10 người.")
            + P("Kế hoạch tháng 11/2026") + P("Truyền thông:") + P("Đăng 4 bài."),
          new[] { H(0, "Hoạt động tháng 10/2026", 1), H(1, "Truyền thông:", 1), H(3, "Công tác xã hội:", 1), H(5, "Kế hoạch tháng 11/2026", 1),
            H(6, "Truyền thông:", 1) })],
        plan =>
        {
            Assert.Equal(["Đăng 3 bài."], Named(plan.Sections, "Truyền thông:", 0).Run.Facts.Select(f => f.Render()));
            Assert.Equal(["Đăng 4 bài."], Named(plan.Sections, "Truyền thông:", 1).Run.Facts.Select(f => f.Render()));
            Assert.Empty(plan.Uncertain);
            Assert.DoesNotContain(plan.Sections, s => s.NewHeadingDepth is not null);
        });

    [Fact]
    public void A_renamed_target_heading_is_held_as_near_name_and_not_written() => Run(
        P("Hoạt động tháng 10/2026") + P("Truyền thông:") + P("Đã đăng 5 bài.") + P("Công tác xã hội và từ thiện:") + P("Hỗ trợ 10 người.")
        + P("Kế hoạch tháng 11/2026") + P("Truyền thông:") + P("Tiếp tục đăng bài."),
        [H(0, "Hoạt động tháng 10/2026", 1), H(1, "Truyền thông:", 2), H(3, "Công tác xã hội và từ thiện:", 2), H(5, "Kế hoạch tháng 11/2026", 1),
            H(6, "Truyền thông:", 2)],
        [(P("Hoạt động tháng 10/2026") + P("Công tác xã hội:") + P("Hỗ trợ 9 người."), [H(0, "Hoạt động tháng 10/2026", 1), H(1, "Công tác xã hội:", 2)])],
        plan =>
        {
            var held = Assert.Single(plan.Uncertain, u => u.SourceHeading == "Công tác xã hội:");
            Assert.Equal("NEAR_NAME", held.Kind);
            Assert.Equal("Công tác xã hội và từ thiện:", Assert.Single(held.Candidates).Heading);
            Assert.DoesNotContain("Hỗ trợ 9 người.", Written(plan));
            Assert.Equal(["Hỗ trợ 9 người."], held.Facts.Select(f => f.Render()));
        });

    [Fact]
    public void A_heading_the_source_adds_becomes_a_new_heading_after_its_aligned_predecessor() => Run(TargetBody, TargetOutline,
        [(P("Hoạt động tháng 10/2026") + P("Truyền thông:") + P("Đã đăng 5 bài.") + P("Công tác xã hội:") + P("Hỗ trợ 10 người.")
            + P("Quỹ Pastb:") + P("Quyên góp 2 triệu.") + P("Kế hoạch tháng 11/2026") + P("Truyền thông:") + P("Tiếp tục đăng bài."),
          new[] { H(0, "Hoạt động tháng 10/2026", 1), H(1, "Truyền thông:", 2), H(3, "Công tác xã hội:", 2), H(5, "Quỹ Pastb:", 2),
            H(7, "Kế hoạch tháng 11/2026", 1), H(8, "Truyền thông:", 2) })],
        plan =>
        {
            var added = Assert.Single(plan.Sections, s => s.NewHeadingDepth is not null);
            Assert.Equal("Quỹ Pastb:", added.Heading);
            Assert.Equal("Công tác xã hội:", added.NewHeadingParent);
            Assert.Equal(["Quyên góp 2 triệu."], added.Run.Facts.Select(f => f.Render()));
            Assert.Empty(plan.Uncertain);
        });

    [Fact]
    public void A_heading_the_target_adds_takes_nothing_and_the_others_keep_their_place() => Run(
        P("Hoạt động tháng 10/2026") + P("Truyền thông:") + P("Đã đăng 5 bài.") + P("Đào tạo:") + P("Khóa 1.")
        + P("Công tác xã hội:") + P("Hỗ trợ 10 người.") + P("Kế hoạch tháng 11/2026") + P("Truyền thông:") + P("Tiếp tục đăng bài."),
        [H(0, "Hoạt động tháng 10/2026", 1), H(1, "Truyền thông:", 2), H(3, "Đào tạo:", 2), H(5, "Công tác xã hội:", 2),
            H(7, "Kế hoạch tháng 11/2026", 1), H(8, "Truyền thông:", 2)],
        [(P("Hoạt động tháng 10/2026") + P("Truyền thông:") + P("Đăng 3 bài.") + P("Công tác xã hội:") + P("Hỗ trợ 6 người."),
          new[] { H(0, "Hoạt động tháng 10/2026", 1), H(1, "Truyền thông:", 2), H(3, "Công tác xã hội:", 2) })],
        plan =>
        {
            Assert.Equal(["Đăng 3 bài."], Named(plan.Sections, "Truyền thông:", 0).Run.Facts.Select(f => f.Render()));
            Assert.Empty(Named(plan.Sections, "Đào tạo:").Run.Facts);
            Assert.Equal(["Hỗ trợ 6 người."], Named(plan.Sections, "Công tác xã hội:").Run.Facts.Select(f => f.Render()));
            Assert.Empty(plan.Uncertain);
        });

    [Fact]
    public void A_moved_heading_is_held_and_its_content_is_not_written_under_another_heading() => Run(TargetBody, TargetOutline,
        [(P("Hoạt động tháng 10/2026") + P("Truyền thông:") + P("Đã đăng 5 bài.") + P("Kế hoạch tháng 11/2026") + P("Truyền thông:")
            + P("Tiếp tục đăng bài.") + P("Công tác xã hội:") + P("Hỗ trợ 10 người."),
          new[] { H(0, "Hoạt động tháng 10/2026", 1), H(1, "Truyền thông:", 2), H(3, "Kế hoạch tháng 11/2026", 1), H(4, "Truyền thông:", 2),
            H(6, "Công tác xã hội:", 2) })],
        plan =>
        {
            // The moved heading may be aligned in either place; its content must sit under its own heading, or be held, never under another.
            foreach (var s in plan.Sections.Where(s => s.Heading != "Công tác xã hội:" && s.NewHeadingDepth is null))
                Assert.DoesNotContain("Hỗ trợ 10 người.", s.Run.Facts.Select(f => f.Render()));
            Assert.DoesNotContain(plan.Sections, s => s.NewHeadingDepth is not null && s.Heading == "Công tác xã hội:");
        });

    [Fact]
    public void Two_sources_adding_the_same_missing_heading_share_one_new_heading() => Run(TargetBody, TargetOutline,
        [(P("Hoạt động tháng 10/2026") + P("Truyền thông:") + P("Đã đăng 5 bài.") + P("Quỹ Pastb:") + P("Quyên góp 1 triệu.")
            + P("Kế hoạch tháng 11/2026") + P("Truyền thông:") + P("Tiếp tục đăng bài."),
          new[] { H(0, "Hoạt động tháng 10/2026", 1), H(1, "Truyền thông:", 2), H(3, "Quỹ Pastb:", 2), H(5, "Kế hoạch tháng 11/2026", 1), H(6, "Truyền thông:", 2) }),
         (P("Hoạt động tháng 9/2026") + P("Truyền thông:") + P("Đăng 2 bài.") + P("Quỹ Pastb:") + P("Quyên góp 3 triệu."),
          new[] { H(0, "Hoạt động tháng 9/2026", 1), H(1, "Truyền thông:", 2), H(3, "Quỹ Pastb:", 2) })],
        plan =>
        {
            var added = plan.Sections.Where(s => s.NewHeadingDepth is not null && s.Heading == "Quỹ Pastb:").ToList();
            Assert.Single(added);
            Assert.Equal(new[] { "Quyên góp 1 triệu.", "Quyên góp 3 triệu." }, added[0].Run.Facts.Select(f => f.Render()).Order());
        });

    [Fact]
    public void Every_written_fact_appears_once() => Run(TargetBody, TargetOutline,
        [(P("Hoạt động tháng 10/2026") + P("Truyền thông:") + P("Đã đăng 5 bài.") + P("Công tác xã hội:") + P("Hỗ trợ 10 người.")
            + P("Kế hoạch tháng 11/2026") + P("Truyền thông:") + P("Tiếp tục đăng bài."),
          new[] { H(0, "Hoạt động tháng 10/2026", 1), H(1, "Truyền thông:", 2), H(3, "Công tác xã hội:", 2), H(5, "Kế hoạch tháng 11/2026", 1),
            H(6, "Truyền thông:", 2) })],
        plan =>
        {
            var facts = Written(plan).ToList();
            Assert.Equal(facts.Distinct().Count(), facts.Count);
        });
}
