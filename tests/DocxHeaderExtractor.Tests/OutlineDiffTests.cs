using DocxHeaderExtractor.DocumentProcessing.Gather;
using DocxHeaderExtractor.DocumentProcessing.Output;
using DocxHeaderExtractor.Contracts.Models;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Comparing two outlines without a model, and the per-item review state. Paragraphs are given by their signature (the text stands for it);
/// headings start at the given paragraph index.
/// </summary>
public sealed class OutlineDiffTests
{
    static readonly string[] Paragraphs = ["BÁO CÁO", "Hoạt động tháng 9", "Truyền thông:", "Đăng 5 bài.", "Công tác xã hội:", "Hỗ trợ 10 người.", "Quỹ:", "Thu 2 triệu."];

    static HeadingRecord H(int index, string text, int level) => new() { Index = index, Level = level, Text = text, StableId = $"body[1]/p[{index + 1}]", HeadingSpan = new TextOffsetSpan(0, text.Length),
        SourceParts = [new HeadingSourcePart($"body[1]/p[{index + 1}]", index, new TextOffsetSpan(0, text.Length), text)], ValidatedSourcePartCount = 1 };

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
            parents[h.StableId!], "reviewer", DateTimeOffset.UtcNow, OutlineApprovals.OutlineHash(outline), h.HeadingSpan, h.InlineBodySpan);
        try
        {
            Assert.Equal("UNREVIEWED", OutlineApprovals.State(outline, store.Of("sha", "k")));
            store.Add([Approve(Base[0]), Approve(Base[1])]);
            Assert.Equal("PARTIAL", OutlineApprovals.State(outline, store.Of("sha", "k")));
            Assert.Equal([Base[0].StableId!, Base[1].StableId!], OutlineApprovals.ApprovedLocations(outline, store.Of("sha", "k")).Order());

            store.Add([Approve(Base[2]), Approve(Base[3])]);
            Assert.Equal("PARTIAL", OutlineApprovals.State(outline, store.Of("sha", "k")));
            store.Add([new("sha", "k", "", OutlineApprovals.OutlineComplete, null, null, null, "reviewer", DateTimeOffset.UtcNow, OutlineApprovals.OutlineHash(outline))]);
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

    [Fact]
    public void Review_is_bound_to_exact_outline_source_config_and_spans()
    {
        var h = H(2, "Truyền thông:", 1); h.HeadingSpan = new TextOffsetSpan(0, 5);
        var outline = Outline(Paragraphs, "sha", h);
        var hash = OutlineApprovals.OutlineHash(outline);
        var decisions = new[] { new OutlineDecision("sha", "k", h.StableId!, OutlineApprovals.Heading, h.Text, 1, null, "reviewer", DateTimeOffset.UtcNow, hash, h.HeadingSpan),
            new OutlineDecision("sha", "k", "", OutlineApprovals.OutlineComplete, null, null, null, "reviewer", DateTimeOffset.UtcNow, hash) };
        Assert.Equal("REVIEWED", OutlineApprovals.State(outline, decisions));
        Assert.Equal("UNREVIEWED", OutlineApprovals.State(Outline(Paragraphs, "other", h), decisions));
        Assert.Equal("UNREVIEWED", OutlineApprovals.State(Outline(Paragraphs, "sha"), decisions));
        h.HeadingSpan = new TextOffsetSpan(1, 6);
        Assert.Equal("UNREVIEWED", OutlineApprovals.State(outline, decisions));
        Assert.Empty(OutlineApprovals.ApprovedLocations(outline, decisions));
        h.HeadingSpan = new TextOffsetSpan(0, 999);
        var badHash = OutlineApprovals.OutlineHash(outline);
        var bad = new[] { new OutlineDecision("sha", "k", h.StableId!, OutlineApprovals.Heading, h.Text, 1, null, "reviewer", DateTimeOffset.UtcNow, badHash, h.HeadingSpan),
            new OutlineDecision("sha", "k", "", OutlineApprovals.OutlineComplete, null, null, null, "reviewer", DateTimeOffset.UtcNow, badHash) };
        Assert.Equal("PARTIAL", OutlineApprovals.State(outline, bad));
    }

    [Fact]
    public void Boundary_changes_and_unbound_headings_are_reported()
    {
        var a = H(2, "Truyền thông:", 1); a.HeadingSpan = new(0, 5);
        var b = H(2, "Truyền thông:", 1); b.HeadingSpan = new(1, 6);
        var boundary = OutlineDiff.Compare(Outline(Paragraphs, "a", a), Outline(Paragraphs, "a", b));
        var d = Assert.Single(boundary.Differences, x => x.Kind == "BOUNDARY");
        Assert.Equal(a.HeadingSpan, d.OldHeadingSpan); Assert.Equal(b.HeadingSpan, d.NewHeadingSpan);
        var missing = new HeadingRecord { Index = 2, StableId = null, Text = "Missing", Level = 1 };
        var result = OutlineDiff.Compare(Outline(Paragraphs, "a", missing), Outline(Paragraphs, "a"));
        Assert.Contains(result.Differences, x => x.Kind == "UNBOUND_OLD" && x.Uncertain);
    }

    [Fact]
    public void Unknown_level_does_not_invent_a_parent_or_reviewed_state()
    {
        var first = H(0, "Root", 1);
        var unknown = new HeadingRecord { Index = 2, StableId = "body[1]/p[3]", Text = "Unknown", Level = null };
        var child = H(4, "Child", 2);
        var outline = Outline(Paragraphs, "a", first, unknown, child);
        var parents = OutlineDiff.Parents(outline.Headings);
        Assert.False(parents.ContainsKey(unknown.StableId!)); Assert.False(parents.ContainsKey(child.StableId!));
        var diff = OutlineDiff.Compare(outline, outline);
        Assert.Contains(diff.Differences, d => d.Kind == "PARENT_UNRESOLVED" && d.Uncertain && d.NewText == "Child");
    }

    [Fact]
    public void Stored_outline_without_source_parts_does_not_inherit_review()
    {
        var h = H(2, "Truyền thông:", 1);
        var original = Outline(Paragraphs, "sha", h);
        var hash = OutlineApprovals.OutlineHash(original);
        var decisions = new[] { new OutlineDecision("sha", "k", h.StableId!, OutlineApprovals.Heading, h.Text, 1, null, "reviewer", DateTimeOffset.UtcNow, hash, h.HeadingSpan),
            new OutlineDecision("sha", "k", "", OutlineApprovals.OutlineComplete, null, null, null, "reviewer", DateTimeOffset.UtcNow, hash) };
        var restored = System.Text.Json.JsonSerializer.Deserialize<StoredOutline>(System.Text.Json.JsonSerializer.Serialize(original))!;
        Assert.Equal("UNREVIEWED", OutlineApprovals.State(restored, decisions));
        Assert.Null(restored.Headings[0].SourceParts);
    }

    [Fact]
    public void Missing_boundaries_and_duplicate_locations_are_reported_without_throwing()
    {
        var h = H(2, "Truyền thông:", 1); h.HeadingSpan = null;
        var noSpan = OutlineDiff.Compare(Outline(Paragraphs, "a", h), Outline(Paragraphs, "a", h));
        Assert.Contains(noSpan.Differences, d => d.Kind == "BOUNDARY_UNRESOLVED" && d.Uncertain && !d.ContextEvidenceVerified);
        var duplicate = OutlineDiff.Compare(Outline(Paragraphs, "a", h, H(2, "Khác", 1)), Outline(Paragraphs, "a"));
        Assert.Equal(2, duplicate.Differences.Count(d => d.Kind == "UNBOUND_OLD" && d.Uncertain));
    }

    [Fact]
    public void Multi_paragraph_bindings_are_compared_and_missing_bindings_remain_unresolved()
    {
        var a = new HeadingRecord { Index = 2, StableId = "body[1]/p[3]", Text = "Same", Level = 1, HeadingSpan = new(0, 4),
            SourceParts = [new("body[1]/p[3]", 2, new(0, 4), "Same"), new("body[1]/p[4]", 3, new(0, 4), "Part")], ValidatedSourcePartCount = 2 };
        var b = new HeadingRecord { Index = 2, StableId = "body[1]/p[3]", Text = "Same", Level = 1, HeadingSpan = new(0, 4),
            SourceParts = [new("body[1]/p[3]", 2, new(0, 4), "Same"), new("body[1]/p[5]", 4, new(0, 4), "Part")], ValidatedSourcePartCount = 2 };
        var result = OutlineDiff.Compare(Outline(Paragraphs, "a", a), Outline(Paragraphs, "a", b));
        Assert.Contains(result.Differences, d => d.Kind == "BINDING" && d.OldSourceParts!.Count == 2 && d.NewSourceParts!.Count == 2);
        var restored = System.Text.Json.JsonSerializer.Deserialize<StoredOutline>(System.Text.Json.JsonSerializer.Serialize(Outline(Paragraphs, "a", a)))!;
        var unresolved = OutlineDiff.Compare(restored, restored);
        // Neither side kept its bindings: one outline-level item, not one per heading.
        Assert.Single(unresolved.Differences, d => d.Kind == "EVIDENCE_MISSING" && d.Uncertain);
        Assert.DoesNotContain(unresolved.Differences, d => d.Kind is "BINDING_UNRESOLVED" or "BOUNDARY_UNRESOLVED");
    }

    [Fact]
    public void Outline_store_roundtrips_private_binding_without_changing_public_heading_json()
    {
        var root = Path.Combine(Path.GetTempPath(), "dhx-outline-bindings-" + Guid.NewGuid().ToString("N"));
        var h = H(2, "Truyền thông:", 1);
        var original = Outline(Paragraphs, "sha", h);
        var hash = OutlineApprovals.OutlineHash(original);
        var decisions = new[] { new OutlineDecision("sha", "k", h.StableId!, OutlineApprovals.Heading, h.Text, 1, null, "reviewer", DateTimeOffset.UtcNow, hash, h.HeadingSpan),
            new OutlineDecision("sha", "k", "", OutlineApprovals.OutlineComplete, null, null, null, "reviewer", DateTimeOffset.UtcNow, hash) };
        try
        {
            var store = new OutlineStore(root);
            Assert.NotNull(store.Store(original));
            var restored = store.Find("sha", "k")!;
            Assert.NotNull(restored.Headings[0].SourceParts);
            Assert.Equal(hash, OutlineApprovals.OutlineHash(restored));
            Assert.Equal("REVIEWED", OutlineApprovals.State(restored, decisions));
            Assert.DoesNotContain("SourceParts", System.Text.Json.JsonSerializer.Serialize(restored.Headings[0]));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Separate_approvals_for_two_outlines_of_the_same_file_do_not_replace_each_other()
    {
        var root = Path.Combine(Path.GetTempPath(), "dhx-outline-approvals-" + Guid.NewGuid().ToString("N"));
        var a = Outline(Paragraphs, "sha", H(2, "One", 1));
        var b = Outline(Paragraphs, "sha", H(4, "Two", 1));
        var store = new OutlineApprovals(root);
        OutlineDecision[] Approvals(StoredOutline o)
        {
            var h = Assert.Single(o.Headings); var hash = OutlineApprovals.OutlineHash(o);
            return [new("sha", "k", h.StableId!, OutlineApprovals.Heading, h.Text, h.Level, null, "reviewer", DateTimeOffset.UtcNow, hash, h.HeadingSpan),
                new("sha", "k", "", OutlineApprovals.OutlineComplete, null, null, null, "reviewer", DateTimeOffset.UtcNow, hash)];
        }
        try
        {
            store.Add(Approvals(a)); store.Add(Approvals(b));
            var decisions = store.Of("sha", "k");
            Assert.Equal(4, decisions.Count);
            Assert.Equal("REVIEWED", OutlineApprovals.State(a, decisions));
            Assert.Equal("REVIEWED", OutlineApprovals.State(b, decisions));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_title_added_above_everything_is_one_level_shift_not_one_item_per_heading()
    {
        HeadingRecord[] shifted = [H(0, "BÁO CÁO Hoạt động tháng 9", 1), H(2, "Truyền thông:", 3), H(4, "Công tác xã hội:", 3), H(6, "Quỹ:", 3)];
        HeadingRecord[] flat = [H(0, "BÁO CÁO Hoạt động tháng 9", 1), H(2, "Truyền thông:", 2), H(4, "Công tác xã hội:", 2), H(6, "Quỹ:", 2)];
        var result = OutlineDiff.Compare(Outline(Paragraphs, "a", flat), Outline(Paragraphs, "a", shifted));
        var shift = Assert.Single(result.Differences, d => d.Kind == "LEVEL_SHIFT");
        Assert.Equal(1, shift.NewLevel);
        Assert.DoesNotContain(result.Differences, d => d.Kind == "LEVEL");
    }
}
