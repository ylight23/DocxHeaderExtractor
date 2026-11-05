extern alias WebApp;
using DocxHeaderExtractor.DocumentProcessing.Gather;
using DocxHeaderExtractor.Sources.Docx.Changes;

namespace DocxHeaderExtractor.Tests;

public sealed class GatherImpactTests
{
    static string P(string text, string? id = null) => $"<w:p{(id is null ? "" : $" xmlns:w14=\"http://schemas.microsoft.com/office/word/2010/wordml\" w14:paraId=\"{id}\"")}><w:r><w:t>{text}</w:t></w:r></w:p>";
    static DocxVersion Version(string body)
    {
        var path = RawDocxFixtureTests.Build(body);
        try { return DocxVersionReader.Read(path); } finally { File.Delete(path); }
    }
    static GatheredFact Fact(string source, string location, string text) => new([new(source + "|" + location, source, location, "text", text, 0, text.Length)], []);
    static GatherHeadingPlan Plan(params GatheredFact[] facts) => new([new("body[1]/p[1]", "Mục A", "Mục A", new GatherRun(facts, [], [], [], [], [], 1), [])], [], []);
    static GatherSnapshot Snapshot(DocxVersion target, DocxVersion source, GatherHeadingPlan plan, string sourceName = "source.docx") =>
        new(DateTimeOffset.UtcNow, [new("target", "target.docx", "target", target), new("S", sourceName, "source", source)], plan);

    [Fact]
    public void Unchanged_and_inserted_source_parts_are_distinguished_without_auto_approval()
    {
        var target = Version(P("Target")); var old = Version(P("A")); var next = Version(P("A") + P("B"));
        var a = Fact("S", "body[1]/p[1]", "A"); var b = Fact("S", "body[1]/p[2]", "B");
        var result = GatherImpact.Compare(Snapshot(target, old, Plan(a)), Snapshot(target, next, Plan(a, b)));
        Assert.False(result.TargetChanged);
        var item = Assert.Single(result.Items);
        Assert.Equal("NEW_OR_UNMATCHED_EVIDENCE", item.Kind);
        Assert.Equal("B", item.After!.Fact.Render());
        Assert.True(item.NeedsReview);
        Assert.Contains(Assert.Single(result.DocumentDiffs).Changes, c => c.Document?.Text == "B");
    }

    [Fact]
    public void Added_source_content_not_selected_by_either_plan_is_still_reported_by_document_diff()
    {
        var target = Version(P("Target")); var old = Version(P("A")); var next = Version(P("A") + P("B"));
        var a = Fact("S", "body[1]/p[1]", "A");
        var result = GatherImpact.Compare(Snapshot(target, old, Plan(a)), Snapshot(target, next, Plan(a)));
        Assert.Empty(result.Items);
        Assert.Contains(Assert.Single(result.DocumentDiffs).Changes, c => c.Document?.Text == "B");
    }

    [Fact]
    public void Same_paragraph_id_with_changed_text_is_only_a_candidate_and_both_sides_are_kept()
    {
        var target = Version(P("Target")); var old = Version(P("30", "12345678")); var next = Version(P("45", "12345678"));
        var result = GatherImpact.Compare(Snapshot(target, old, Plan(Fact("S", "body[1]/p[1]", "30"))),
            Snapshot(target, next, Plan(Fact("S", "body[1]/p[1]", "45"))));
        Assert.Equal("30", Assert.Single(result.PossibleModifications).OldText);
        Assert.Equal("45", Assert.Single(result.PossibleModifications).NewText);
        Assert.Contains(result.Items, x => x.Kind == "OLD_EVIDENCE_UNMAPPED");
        Assert.Contains(result.Items, x => x.Kind == "NEW_OR_UNMATCHED_EVIDENCE");
    }

    [Fact]
    public void Duplicate_source_names_are_not_paired_and_target_change_is_reported()
    {
        var oldTarget = Version(P("Target")); var newTarget = Version(P("Changed target")); var source = Version(P("A"));
        var old = new GatherSnapshot(DateTimeOffset.UtcNow, [new("target", "target", "target", oldTarget),
            new("S1", "same.docx", "source", source), new("S2", "same.docx", "source", source)], Plan(Fact("S1", "body[1]/p[1]", "A")));
        var next = Snapshot(newTarget, source, Plan(Fact("S", "body[1]/p[1]", "A")), "same.docx");
        var result = GatherImpact.Compare(old, next);
        Assert.True(result.TargetChanged);
        Assert.NotEmpty(result.UnmatchedDocuments);
        Assert.Contains(result.Items, x => x.Kind == "OLD_EVIDENCE_UNMAPPED");
    }

    [Fact]
    public void Additional_provenance_changes_are_visible()
    {
        var target = Version(P("Target")); var source = Version(P("A") + P("B"));
        var a = Fact("S", "body[1]/p[1]", "A");
        var b = a with { AlsoFrom = [new("S|body[1]/p[2]", "S", "body[1]/p[2]", "text", "B", 0, 1)] };
        var result = GatherImpact.Compare(Snapshot(target, source, Plan(a)), Snapshot(target, source, Plan(b)));
        Assert.Equal("ADDITIONAL_PROVENANCE_CHANGED", Assert.Single(result.Items).Kind);
    }

    [Fact]
    public void Named_history_persists_across_store_instances_and_corrupt_history_is_not_overwritten()
    {
        var root = Path.Combine(Path.GetTempPath(), "dhx-impact-" + Guid.NewGuid().ToString("N"));
        try
        {
            var target = Version(P("Target")); var source = Version(P("A")); var snap = Snapshot(target, source, Plan(Fact("S", "body[1]/p[1]", "A")));
            var store = new WebApp::DocxHeaderExtractor.Web.GatherHistoryStore(root);
            Assert.Equal("BASELINE_SAVED", store.CompareAndSave("hồ sơ", snap).Status);
            var other = new WebApp::DocxHeaderExtractor.Web.GatherHistoryStore(root);
            Assert.Equal("COMPARED", other.CompareAndSave("hồ sơ", snap).Status);
            var file = Assert.Single(Directory.GetFiles(root, "*.json"));
            File.WriteAllText(file, "not-json");
            Assert.Equal("HISTORY_UNAVAILABLE", other.CompareAndSave("hồ sơ", snap).Status);
            Assert.Equal("not-json", File.ReadAllText(file));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
