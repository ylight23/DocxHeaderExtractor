using DocxHeaderExtractor.DocumentProcessing.Gather;
using DocxHeaderExtractor.DocumentProcessing.Output;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Comparing two outlines without a model, and the per-item review state. Paragraphs are given by their signature (the text stands for it);
/// headings start at the given paragraph index.
/// </summary>
public sealed class OutlineDiffTests
{
    static readonly string[] Paragraphs = ["BÁO CÁO", "Hoạt động tháng 9", "Truyền thông:", "Đăng 5 bài.", "Công tác xã hội:", "Hỗ trợ 10 người.", "Quỹ:", "Thu 2 triệu."];

    static HeadingRecord H(int index, string text, int level) => new() { Index = index, Level = level, Text = text, StableId = $"body[1]/p[{index + 1}]" };

    static StoredOutline Outline(IEnumerable<string> paragraphs, string sha, params HeadingRecord[] headings) =>
        new(OutlineStore.Version, sha, "k", "f.docx", DateTimeOffset.UtcNow,
            paragraphs.Select((s, i) => new StoredParagraph($"body[1]/p[{i + 1}]", i, s)).ToArray(), [], headings);

    static readonly HeadingRecord[] Base = [H(0, "BÁO CÁO Hoạt động tháng 9", 1), H(2, "Truyền thông:", 2), H(4, "Công tác xã hội:", 2), H(6, "Quỹ:", 2)];

    [Fact]
    public void The_same_outline_has_no_difference()
    {
        var result = OutlineDiff.Compare(Outline(Paragraphs, "a", Base), Outline(Paragraphs, "a", Base));
        Assert.Empty(result.Differences);
        Assert.Equal(4, result.SameHeadings);
        Assert.Equal(0, result.ChangedParagraphs);
    }

    [Fact]
    public void Two_runs_on_the_same_file_list_added_removed_level_and_parent_without_any_changed_text()
    {
        HeadingRecord[] other = [H(0, "BÁO CÁO Hoạt động tháng 9", 1), H(2, "Truyền thông:", 2), H(3, "Đăng 5 bài.", 3), H(4, "Công tác xã hội:", 1)];
        var result = OutlineDiff.Compare(Outline(Paragraphs, "a", Base), Outline(Paragraphs, "a", other));
        Assert.Contains(result.Differences, d => d.Kind == "ADDED" && d.NewText == "Đăng 5 bài.");
        Assert.Contains(result.Differences, d => d.Kind == "REMOVED" && d.OldText == "Quỹ:");
        Assert.Contains(result.Differences, d => d.Kind == "LEVEL" && d.OldText == "Công tác xã hội:");
        Assert.Contains(result.Differences, d => d.Kind == "PARENT" && d.OldText == "Công tác xã hội:");
        Assert.All(result.Differences, d => Assert.False(d.ContextChanged));
    }

    [Fact]
    public void A_title_split_over_two_headings_is_reported_as_a_split()
    {
        HeadingRecord[] split = [H(0, "BÁO CÁO", 1), H(1, "Hoạt động tháng 9", 1), H(2, "Truyền thông:", 2), H(4, "Công tác xã hội:", 2), H(6, "Quỹ:", 2)];
        var result = OutlineDiff.Compare(Outline(Paragraphs, "a", Base), Outline(Paragraphs, "a", split));
        Assert.Contains(result.Differences, d => d.Kind == "SPLIT" && d.NewText == "BÁO CÁO");
        Assert.Contains(result.Differences, d => d.Kind == "SPLIT" && d.NewText == "Hoạt động tháng 9");
        Assert.DoesNotContain(result.Differences, d => d.Kind is "ADDED" or "REMOVED");
    }

    [Fact]
    public void An_edited_heading_paragraph_is_source_changed_on_the_old_side_and_on_changed_text_on_the_new_side()
    {
        var edited = Paragraphs.Select(p => p == "Truyền thông:" ? "Truyền thông nội bộ:" : p).ToArray();
        HeadingRecord[] next = [H(0, "BÁO CÁO Hoạt động tháng 9", 1), H(2, "Truyền thông nội bộ:", 2), H(4, "Công tác xã hội:", 2), H(6, "Quỹ:", 2)];
        var result = OutlineDiff.Compare(Outline(Paragraphs, "a", Base), Outline(edited, "b", next));
        Assert.Contains(result.Differences, d => d.Kind == "SOURCE_CHANGED" && d.OldText == "Truyền thông:");
        var added = Assert.Single(result.Differences, d => d.Kind == "ON_CHANGED_TEXT");
        Assert.True(added.ContextChanged);
        Assert.Equal(1, result.ChangedParagraphs);
        Assert.Equal(3, result.SameHeadings);
    }

    [Fact]
    public void A_heading_on_a_repeated_paragraph_is_marked_uncertain()
    {
        var repeated = Paragraphs.Append("Quỹ:").Append("Thu thêm.").ToArray();
        var old = Outline(repeated, "a", Base);
        var next = Outline(repeated, "a", [.. Base.Take(3), H(8, "Quỹ:", 2)]);
        var result = OutlineDiff.Compare(old, next);
        Assert.All(result.Differences.Where(d => d.OldText == "Quỹ:" || d.NewText == "Quỹ:"), d => Assert.True(d.Uncertain));
        Assert.NotEmpty(result.Differences);
    }

    [Fact]
    public void Review_state_is_partial_until_every_heading_and_the_completeness_are_reviewed()
    {
        var root = Path.Combine(Path.GetTempPath(), "dhx-approvals-" + Guid.NewGuid().ToString("N"));
        var store = new OutlineApprovals(root);
        var outline = Outline(Paragraphs, "sha", Base);
        var parents = OutlineDiff.Parents(Base);
        OutlineDecision Approve(HeadingRecord h) => new("sha", "k", h.StableId!, OutlineApprovals.Heading, h.Text, h.Level,
            parents[h.StableId!], "reviewer", DateTimeOffset.UtcNow);
        try
        {
            Assert.Equal("UNREVIEWED", OutlineApprovals.State(outline, store.Of("sha", "k")));
            store.Add([Approve(Base[0]), Approve(Base[1])]);
            Assert.Equal("PARTIAL", OutlineApprovals.State(outline, store.Of("sha", "k")));
            Assert.Equal([Base[0].StableId!, Base[1].StableId!], OutlineApprovals.ApprovedLocations(outline, store.Of("sha", "k")).Order());

            store.Add([Approve(Base[2]), Approve(Base[3])]);
            Assert.Equal("PARTIAL", OutlineApprovals.State(outline, store.Of("sha", "k")));
            store.Add([new("sha", "k", "", OutlineApprovals.OutlineComplete, null, null, null, "reviewer", DateTimeOffset.UtcNow)]);
            Assert.Equal("REVIEWED", OutlineApprovals.State(outline, store.Of("sha", "k")));

            // A decision on another version says nothing about this one.
            Assert.Empty(store.Of("other-sha", "k"));
            // The diff carries which old decisions were reviewed.
            var diff = OutlineDiff.Compare(outline, Outline(Paragraphs, "sha", [.. Base.Take(3)]),
                OutlineApprovals.ApprovedLocations(outline, store.Of("sha", "k")));
            Assert.True(Assert.Single(diff.Differences).OldApproved);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
