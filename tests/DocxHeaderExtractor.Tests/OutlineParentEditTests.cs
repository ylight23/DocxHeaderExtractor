using DocxHeaderExtractor.DocumentProcessing.Gather;
using DocxHeaderExtractor.DocumentProcessing.Output;

namespace DocxHeaderExtractor.Tests;

public sealed class OutlineParentEditTests
{
    static HeadingRecord H(int index, int level, string text) => new()
    {
        Index = index, StableId = $"body[1]/p[{index + 1}]", Level = level, Text = text,
        HeadingSpan = new(0, text.Length), ValidatedSourcePartCount = 1,
        SourceParts = [new($"body[1]/p[{index + 1}]", index, new(0, text.Length), text)]
    };

    [Fact]
    public void Parent_correction_is_a_new_version_and_old_approval_cannot_transfer()
    {
        var root = Path.Combine(Path.GetTempPath(), "dhx-parent-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new OutlineStore(root);
            var headings = new[] { H(0, 1, "Kế hoạch"), H(1, 2, "Công tác xã hội"), H(2, 3, "Hoàn thiện"), H(3, 2, "Duy trì"), H(4, 2, "Tổng đài") };
            var paragraphs = headings.Select(h => new StoredParagraph(h.StableId!, h.Index, "sig")).ToArray();
            store.Store(new(OutlineStore.Version, "abc123", "config", "file.docx", DateTimeOffset.UtcNow, paragraphs, [], headings));
            var old = store.Find("abc123", "config")!;
            var approvals = new OutlineApprovals(root);
            approvals.Add(old.Headings.Select(h => OutlineApprovals.Decide(old, "HEADING", h.StableId, "reviewer", DateTimeOffset.UtcNow).Decision!)
                .Append(OutlineApprovals.Decide(old, "OUTLINE_COMPLETE", null, "reviewer", DateTimeOffset.UtcNow).Decision!));
            Assert.Equal("REVIEWED", OutlineApprovals.State(old, approvals.Of("abc123", "config")));
            var (editedHeadings, error) = OutlineParentEdit.Apply(old, "body[1]/p[4]", "body[1]/p[2]");
            Assert.Null(error);
            var (edited, saveError) = store.StoreEdited("abc123", "config", OutlineApprovals.OutlineHash(old), editedHeadings!, "reviewer");
            Assert.Null(saveError);
            Assert.Equal(2, edited!.Revision);
            Assert.Equal("body[1]/p[2]", OutlineDiff.Parents(edited.Headings)["body[1]/p[4]"]);
            Assert.Equal("UNREVIEWED", OutlineApprovals.State(edited, approvals.Of("abc123", "config")));
            Assert.Equal(OutlineApprovals.OutlineHash(old), edited.PreviousOutlineHash);
            Assert.Equal(1, store.FindVersion("abc123", "config", edited.PreviousOutlineHash!)!.Revision);
            Assert.Equal("OUTLINE_STALE", store.StoreEdited("abc123", "config", OutlineApprovals.OutlineHash(old), editedHeadings!, "reviewer").Error);
            Assert.Equal(2, store.Find("abc123", "config")!.Revision);

            // A broken pointer to the reviewer's edit is reported, never treated as "no outline" (which would extract again and drop the edit).
            var active = Directory.GetFiles(root, "active.txt", SearchOption.AllDirectories).Single();
            File.WriteAllText(active, "not-a-hash");
            Assert.Throws<InvalidDataException>(() => store.Find("abc123", "config"));
            File.WriteAllText(active, new string('0', 64));
            Assert.Throws<InvalidDataException>(() => store.Find("abc123", "config"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void A_move_that_would_change_another_heading_parent_is_refused()
    {
        // A(1) > B(2) > C(3), D(3). Moving C under A would leave D (level 3) after C (level 2), so D would silently become C's child.
        var headings = new[] { H(0, 1, "A"), H(1, 2, "B"), H(2, 3, "C"), H(3, 3, "D") };
        var outline = new StoredOutline(OutlineStore.Version, "sha", "k", "f", DateTimeOffset.UtcNow, [], [], headings);
        Assert.Equal("OTHER_PARENTS_WOULD_CHANGE", OutlineParentEdit.Apply(outline, "body[1]/p[3]", "body[1]/p[1]").Error);
    }

    [Fact]
    public void Cannot_move_to_a_later_heading_or_an_unrepresentable_parent()
    {
        var headings = new[] { H(0, 1, "A"), H(1, 2, "B"), H(2, 1, "C"), H(3, 2, "D") };
        var outline = new StoredOutline(OutlineStore.Version, "sha", "k", "f", DateTimeOffset.UtcNow, [], [], headings);
        Assert.Equal("PARENT_NOT_EARLIER", OutlineParentEdit.Apply(outline, "body[1]/p[2]", "body[1]/p[4]").Error);
        Assert.Equal("NO_CHANGE", OutlineParentEdit.Apply(outline, "body[1]/p[4]", "body[1]/p[1]").Error);
    }

    [Fact]
    public void Parent_edit_shifts_children_and_keeps_source_order()
    {
        var headings = new[] { H(0, 1, "A"), H(1, 2, "B"), H(2, 2, "C"), H(3, 3, "C1"), H(4, 1, "D") };
        var outline = new StoredOutline(OutlineStore.Version, "sha", "k", "f", DateTimeOffset.UtcNow, [], [], headings);
        var (changed, error) = OutlineParentEdit.Apply(outline, "body[1]/p[3]", "body[1]/p[2]");
        Assert.Null(error);
        Assert.Equal(new int?[] { 1, 2, 3, 4, 1 }, changed!.Select(h => h.Level));
        Assert.Equal(headings.Select(h => h.StableId), changed.Select(h => h.StableId));
        Assert.Equal("body[1]/p[3]", OutlineDiff.Parents(changed)["body[1]/p[4]"]);
    }

    [Fact]
    public void Leading_outside_tree_title_does_not_block_parent_edit()
    {
        var title = H(0, 1, "Báo cáo");
        title.Level = null;
        title.HierarchyResolution = "model-out-of-hierarchy";
        var outline = new StoredOutline(OutlineStore.Version, "sha", "k", "f", DateTimeOffset.UtcNow, [], [],
            [title, H(1, 1, "A"), H(2, 2, "B"), H(3, 2, "C")]);
        var (changed, error) = OutlineParentEdit.Apply(outline, "body[1]/p[4]", "body[1]/p[3]");
        Assert.Null(error);
        Assert.Null(changed![0].Level);
        Assert.Equal("body[1]/p[3]", OutlineDiff.Parents(changed)["body[1]/p[4]"]);
    }
}
