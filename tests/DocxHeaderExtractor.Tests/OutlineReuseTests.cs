using DocxHeaderExtractor.App;
using DocxHeaderExtractor.DocumentProcessing.Gather;
using DocxHeaderExtractor.DocumentProcessing.Output;
using DocxHeaderExtractor.Sources.Docx.Changes;

namespace DocxHeaderExtractor.Tests;

/// <summary>Stored outlines: reused for the same file, carried over a diff only when every heading paragraph is unchanged, otherwise extracted again.</summary>
public sealed class OutlineReuseTests
{
    static string P(string text, bool bold = false) =>
        $"<w:p><w:r>{(bold ? "<w:rPr><w:b/></w:rPr>" : "")}<w:t xml:space=\"preserve\">{text}</w:t></w:r></w:p>";

    static HeadingRecord H(int paragraph, int level, string text) =>
        new() { Index = paragraph - 1, Level = level, Text = text, StableId = $"body[1]/p[{paragraph}]" };

    // p1 A (heading), p2 body, p3 B (heading), p4 body
    static readonly string Original = P("A. Mục một", true) + P("Nội dung một.") + P("B. Mục hai", true) + P("Nội dung hai.");
    static readonly HeadingRecord[] Headings = [H(1, 1, "A. Mục một"), H(3, 1, "B. Mục hai")];

    static (StoredOutline Stored, string Root) Store(string path)
    {
        var version = DocxVersionReader.Read(path);
        var stored = new StoredOutline(OutlineStore.Version, version.FileSha256, "k", "bao-cao.docx", DateTimeOffset.UtcNow,
            OutlineStore.Paragraphs(version), version.UnsupportedRegions, Headings);
        var root = Path.Combine(Path.GetTempPath(), "dhx-outline-" + Guid.NewGuid().ToString("N"));
        new OutlineStore(root).Store(stored);
        return (stored, root);
    }

    static OutlineMapResult MapTo(string originalBody, string changedBody)
    {
        var before = RawDocxFixtureTests.Build(originalBody);
        var after = RawDocxFixtureTests.Build(changedBody);
        try
        {
            var (stored, root) = Store(before);
            Directory.Delete(root, recursive: true);
            var version = DocxVersionReader.Read(after);
            return OutlineMap.Map(stored, OutlineStore.Paragraphs(version), version.UnsupportedRegions);
        }
        finally { File.Delete(before); File.Delete(after); }
    }

    [Fact]
    public void Paragraphs_added_under_a_heading_move_the_later_headings_and_are_listed_as_unchecked()
    {
        var result = MapTo(Original, P("A. Mục một", true) + P("Nội dung một.") + P("Thêm 1.") + P("Thêm 2.") + P("B. Mục hai", true) + P("Nội dung hai."));
        Assert.Equal("MAPPED", result.Status);
        Assert.Equal(["body[1]/p[1]", "body[1]/p[5]"], result.Headings.Select(h => h.StableId));
        Assert.Equal([0, 4], result.Headings.Select(h => h.Index));
        Assert.Equal(["body[1]/p[3]", "body[1]/p[4]"], result.AddedParagraphs);
    }

    [Theory]
    [InlineData("footers,merged_table_cells", "merged_table_cells,footers", "MAPPED")]
    [InlineData("footers", "footers,text_box", "RUN")]
    [InlineData("footers", "footers,tracked_changes", "RUN")]
    [InlineData("tracked_formatting", "tracked_formatting", "RUN")]
    public void Regions_read_in_part_block_the_mapping_only_when_tracked_or_changed(string before, string after, string status)
    {
        static StoredParagraph Para(int n, string signature) => new($"body[1]/p[{n}]", n - 1, signature);
        var stored = new StoredOutline(OutlineStore.Version, "sha", "k", "f.docx", DateTimeOffset.UtcNow,
            [Para(1, "a"), Para(2, "b"), Para(3, "c")], before.Split(','), [H(1, 1, "A"), H(3, 1, "C")]);
        var result = OutlineMap.Map(stored, [Para(1, "a"), Para(2, "b"), Para(3, "new"), Para(4, "c")], after.Split(','));
        Assert.Equal(status, result.Status);
        if (status == "MAPPED") Assert.Equal(["body[1]/p[1]", "body[1]/p[4]"], result.Headings.Select(h => h.StableId));
    }

    [Fact]
    public void The_same_paragraphs_are_exact_and_keep_every_heading()
    {
        var result = MapTo(Original, Original);
        Assert.Equal("EXACT", result.Status);
        Assert.Equal(Headings.Select(h => h.StableId), result.Headings.Select(h => h.StableId));
    }

    [Theory]
    [InlineData("edited", "HEADING_CHANGED_OR_MOVED")]
    [InlineData("unbolded", "HEADING_CHANGED_OR_MOVED")]
    [InlineData("deleted", "HEADING_CHANGED_OR_MOVED")]
    [InlineData("moved", "HEADING_CHANGED_OR_MOVED")]
    [InlineData("repeated", "HEADING_TEXT_REPEATED")]
    public void A_heading_paragraph_that_changed_sends_the_file_back_to_the_pipeline(string change, string reason)
    {
        var changed = change switch
        {
            "edited" => P("A. Mục một", true) + P("Nội dung một.") + P("B. Mục hai (sửa)", true) + P("Nội dung hai."),
            "unbolded" => P("A. Mục một", true) + P("Nội dung một.") + P("B. Mục hai") + P("Nội dung hai."),
            "deleted" => P("A. Mục một", true) + P("Nội dung một.") + P("Nội dung hai."),
            "moved" => P("B. Mục hai", true) + P("Nội dung hai.") + P("A. Mục một", true) + P("Nội dung một."),
            _ => Original + P("B. Mục hai", true),
        };
        var result = MapTo(Original, changed);
        Assert.Equal("RUN", result.Status);
        Assert.StartsWith(reason, result.Reason);
        Assert.Empty(result.Headings);
    }

    [Fact]
    public async Task The_same_file_is_stored_once_and_reused_without_running_the_pipeline_and_a_changed_heading_runs_it_again()
    {
        var first = RawDocxFixtureTests.Build(Original);
        var edited = RawDocxFixtureTests.Build(P("A. Mục một", true) + P("Nội dung một.") + P("Thêm.") + P("B. Mục hai", true) + P("Nội dung hai."));
        var renamed = RawDocxFixtureTests.Build(P("A. Mục một đã đổi", true) + P("Nội dung một.") + P("B. Mục hai", true) + P("Nội dung hai."));
        var root = Path.Combine(Path.GetTempPath(), "dhx-outline-" + Guid.NewGuid().ToString("N"));
        var runs = 0;
        Task<IReadOnlyList<HeadingRecord>> Extract() { runs++; return Task.FromResult<IReadOnlyList<HeadingRecord>>(Headings); }
        try
        {
            var store = new OutlineStore(root);
            Assert.Equal("EXTRACTED", (await OutlineReuse.GetAsync(first, "bao-cao.docx", store, "k", Extract)).Status);
            Assert.Equal("STORED", (await OutlineReuse.GetAsync(first, "bao-cao.docx", store, "k", Extract)).Status);
            Assert.Equal(1, runs);

            var mapped = await OutlineReuse.GetAsync(edited, "bao-cao.docx", store, "k", Extract);
            Assert.Equal("MAPPED", mapped.Status);
            Assert.Equal(["body[1]/p[3]"], mapped.AddedParagraphs);
            Assert.Equal(1, runs);

            // Another configuration never reads this one's entries.
            Assert.Equal("EXTRACTED", (await OutlineReuse.GetAsync(first, "bao-cao.docx", store, "other", Extract)).Status);
            Assert.Equal(2, runs);

            var again = await OutlineReuse.GetAsync(renamed, "bao-cao.docx", store, "k", Extract);
            Assert.Equal("EXTRACTED", again.Status);
            Assert.StartsWith("HEADING_CHANGED_OR_MOVED", again.Reason);
            Assert.Equal(3, runs);
        }
        finally
        {
            File.Delete(first); File.Delete(edited); File.Delete(renamed);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Facts_from_added_paragraphs_are_flagged_and_others_are_not()
    {
        var fact = (string location) => new GatheredFact([new SourcedPart("08:" + location, "08", location, "text", "x")], []);
        var section = new GatherSection("body[1]/p[1]", "A", "", new GatherRun([fact("body[1]/p[3]"), fact("body[1]/p[2]")], [], [], [], [], [], 0), []);
        var plan = OutlineReuse.FlagAdded(new GatherHeadingPlan([section], [], []),
            new Dictionary<string, IReadOnlyList<string>> { ["08"] = ["body[1]/p[3]"] });
        var facts = Assert.Single(plan.Sections).Run.Facts;
        Assert.Contains(facts[0].Flags, f => f.StartsWith("UNVERIFIED_STRUCTURE", StringComparison.Ordinal));
        Assert.Empty(facts[1].Flags);
    }
}
