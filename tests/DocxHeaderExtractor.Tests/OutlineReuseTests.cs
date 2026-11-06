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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Selection_coverage_is_saved_and_restored_with_the_exact_outline(bool complete)
    {
        var path = RawDocxFixtureTests.Build(Original);
        var root = Path.Combine(Path.GetTempPath(), "dhx-selection-coverage-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new OutlineStore(root);
            var first = await OutlineReuse.GetAsync(path, "f.docx", store, "k",
                () => Task.FromResult<IReadOnlyList<HeadingRecord>>(Headings), selectionComplete: () => complete);
            Assert.Equal(complete, first.Current!.SelectionComplete);
            var cached = await OutlineReuse.GetAsync(path, "f.docx", store, "k",
                () => throw new InvalidOperationException("must use cached outcome"));
            Assert.Equal("STORED", cached.Status);
            Assert.Equal(complete, cached.Current!.SelectionComplete);
        }
        finally { File.Delete(path); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Legacy_outline_is_refreshed_once_and_old_version_remains_available()
    {
        var path = RawDocxFixtureTests.Build(Original);
        var (_, root) = Store(path);
        try
        {
            var store = new OutlineStore(root);
            var sha = DocxVersionReader.Read(path).FileSha256;
            var old = store.Find(sha, "k")!;
            var oldHash = OutlineApprovals.OutlineHash(old);
            var calls = 0;
            Task<IReadOnlyList<HeadingRecord>> Extract() { calls++; return Task.FromResult<IReadOnlyList<HeadingRecord>>(Headings); }
            var first = await OutlineReuse.GetAsync(path, "bao-cao.docx", store, "k", Extract,
                selectionComplete: () => true, refreshUnknownSelection: true);
            var second = await OutlineReuse.GetAsync(path, "bao-cao.docx", store, "k", Extract,
                selectionComplete: () => true, refreshUnknownSelection: true);
            Assert.Equal(1, calls);
            Assert.Equal("EXTRACTED", first.Status);
            Assert.Equal("STORED", second.Status);
            Assert.True(second.Current!.SelectionComplete);
            Assert.NotNull(store.FindVersion(sha, "k", oldHash));
            Assert.Equal("OUTLINE_STALE", store.StoreRefreshedExtraction(sha, "k", oldHash, Headings, null).Error);
        }
        finally { File.Delete(path); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Incomplete_legacy_refresh_does_not_replace_stored_outline()
    {
        var path = RawDocxFixtureTests.Build(Original);
        var (_, root) = Store(path);
        try
        {
            var store = new OutlineStore(root);
            var sha = DocxVersionReader.Read(path).FileSha256;
            var oldHash = OutlineApprovals.OutlineHash(store.Find(sha, "k")!);
            var result = await OutlineReuse.GetAsync(path, "bao-cao.docx", store, "k",
                () => Task.FromResult<IReadOnlyList<HeadingRecord>>(Headings),
                selectionComplete: () => false, refreshUnknownSelection: true);
            Assert.Equal("SELECT_COVERAGE_INCOMPLETE", result.Reason);
            Assert.Null(result.Current);
            Assert.Equal(oldHash, OutlineApprovals.OutlineHash(store.Find(sha, "k")!));
        }
        finally { File.Delete(path); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

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
    public async Task Only_the_identical_file_reuses_an_outline_and_every_changed_version_runs_the_pipeline()
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
            Assert.Equal("EXTRACTED", mapped.Status);
            Assert.Equal("SOURCE_CHANGED:OUTLINE_REQUIRES_REEXTRACTION", mapped.Reason);
            Assert.Equal(2, runs);

            // Another configuration never reads this one's entries.
            Assert.Equal("EXTRACTED", (await OutlineReuse.GetAsync(first, "bao-cao.docx", store, "other", Extract)).Status);
            Assert.Equal(3, runs);

            var again = await OutlineReuse.GetAsync(renamed, "bao-cao.docx", store, "k", Extract);
            Assert.Equal("EXTRACTED", again.Status);
            Assert.Equal("SOURCE_CHANGED:OUTLINE_REQUIRES_REEXTRACTION", again.Reason);
            Assert.Equal(4, runs);
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

    [Fact]
    public async Task An_added_heading_cannot_be_mapped_as_unverified_body_or_leave_following_content_under_its_old_parent()
    {
        var original = RawDocxFixtureTests.Build(Original);
        var changed = RawDocxFixtureTests.Build(P("A. Mục một", true) + P("C. Mục mới", true) + P("Nội dung một.") + P("B. Mục hai", true) + P("Nội dung hai."));
        var root = Path.Combine(Path.GetTempPath(), "dhx-outline-" + Guid.NewGuid().ToString("N"));
        var calls = 0;
        try
        {
            var store = new OutlineStore(root);
            await OutlineReuse.GetAsync(original, "report.docx", store, "k", () => { calls++; return Task.FromResult<IReadOnlyList<HeadingRecord>>(Headings); });
            var stored = store.Latest("report.docx", "k")!;
            var changedVersion = DocxVersionReader.Read(changed);
            var proposed = OutlineMap.Map(stored, OutlineStore.Paragraphs(changedVersion), changedVersion.UnsupportedRegions);
            Assert.Equal("MAPPED", proposed.Status);
            Assert.DoesNotContain(proposed.Headings, h => h.Text == "C. Mục mới");
            Assert.Contains("body[1]/p[2]", proposed.AddedParagraphs);
            var result = await OutlineReuse.GetAsync(changed, "report.docx", store, "k", () =>
            {
                calls++;
                return Task.FromResult<IReadOnlyList<HeadingRecord>>([H(1, 1, "A. Mục một"), H(2, 1, "C. Mục mới"), H(4, 1, "B. Mục hai")]);
            });
            Assert.Equal("EXTRACTED", result.Status);
            Assert.Equal(2, calls);
            Assert.Contains(result.Headings, h => h.Text == "C. Mục mới" && h.StableId == "body[1]/p[2]");
        }
        finally { File.Delete(original); File.Delete(changed); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Changed_package_with_identical_body_paragraphs_is_not_accepted_as_exact_cache()
    {
        var original = RawDocxFixtureTests.Build(Original);
        var changed = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".docx");
        var root = Path.Combine(Path.GetTempPath(), "dhx-outline-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.Copy(original, changed);
            using (var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(changed, true)) doc.PackageProperties.Creator = "Different creator";
            var calls = 0;
            Task<IReadOnlyList<HeadingRecord>> Extract() { calls++; return Task.FromResult<IReadOnlyList<HeadingRecord>>(Headings); }
            var store = new OutlineStore(root);
            await OutlineReuse.GetAsync(original, "report.docx", store, "k", Extract);
            var result = await OutlineReuse.GetAsync(changed, "report.docx", store, "k", Extract);
            Assert.Equal("EXTRACTED", result.Status);
            Assert.Equal("SOURCE_CHANGED:OUTLINE_REQUIRES_REEXTRACTION", result.Reason);
            Assert.Equal(2, calls);
        }
        finally { File.Delete(original); File.Delete(changed); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Each_changed_outline_is_a_new_revision_and_the_earlier_ones_stay_readable()
    {
        var root = Path.Combine(Path.GetTempPath(), "dhx-outline-" + Guid.NewGuid().ToString("N"));
        var store = new OutlineStore(root);
        var paragraphs = new[] { new StoredParagraph("body[1]/p[1]", 0, "a") };
        try
        {
            var v1 = store.Store(new(OutlineStore.Version, "sha-one", "k", "bao-cao.docx", DateTimeOffset.UtcNow, paragraphs, [], [H(1, 1, "A")]));
            Assert.Equal(1, v1!.Revision);
            Assert.Null(store.Store(new(OutlineStore.Version, "sha-one", "k", "bao-cao.docx", DateTimeOffset.UtcNow, paragraphs, [], [H(1, 1, "A")])));

            var v2 = store.Store(new(OutlineStore.Version, "sha-two", "k", "bao-cao.docx", DateTimeOffset.UtcNow, paragraphs, [], [H(1, 1, "A"), H(2, 1, "B")]));
            Assert.Equal(2, v2!.Revision);
            Assert.Equal("sha-one", v2.PreviousSha256);

            Assert.Equal([1, 2], store.History("bao-cao.docx", "k").Select(r => r.Number));
            Assert.Equal("sha-two", store.Latest("bao-cao.docx", "k")!.SourceSha256);
            Assert.Equal(["A"], store.Find("sha-one", "k")!.Headings.Select(h => h.Text));
            Assert.Null(store.Latest("bao-cao.docx", "other"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Stored_outline_keeps_placement_evidence_and_legacy_data_remains_unknown()
    {
        var root = Path.Combine(Path.GetTempPath(), "dhx-placement-" + Guid.NewGuid().ToString("N"));
        var store = new OutlineStore(root);
        var decision = new DocxHeaderExtractor.DocumentProcessing.Audit.HeadingPlacementDecisionObservation("body[1]/p[1]", "S0001", "unresolved")
            { ProposedParentAlias = "NONE", ResolutionBasis = "model-parent-proposal" };
        var evidence = new DocxHeaderExtractor.DocumentProcessing.Audit.PlacementAudit("placement-proposed", 1, 1, 0, 1, 0, null, [decision]);
        try
        {
            var paragraphs = new[] { new StoredParagraph("body[1]/p[1]", 0, "a") };
            store.Store(new(OutlineStore.Version, "old", "k", "file", DateTimeOffset.UtcNow, paragraphs, [], [H(1, 1, "A")]));
            store.Store(new(OutlineStore.Version, "new", "k", "file", DateTimeOffset.UtcNow, paragraphs, [], [H(1, 1, "A")], Placement: evidence));
            Assert.Null(store.Find("old", "k")!.Placement);
            var saved = store.Find("new", "k")!.Placement;
            Assert.NotNull(saved);
            Assert.Equal("NONE", Assert.Single(saved!.Decisions).ProposedParentAlias);
            Assert.Equal("model-parent-proposal", Assert.Single(saved.Decisions).ResolutionBasis);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Extracted_outline_stores_its_own_placement_observation()
    {
        var path = RawDocxFixtureTests.Build("<w:p><w:r><w:t>A</w:t></w:r></w:p>");
        var root = Path.Combine(Path.GetTempPath(), "dhx-placement-reuse-" + Guid.NewGuid().ToString("N"));
        var evidence = new DocxHeaderExtractor.DocumentProcessing.Audit.PlacementAudit("placement-proposed", 1, 1, 1, 0, 0, null,
            [new("body[1]/p[1]", "S0001", "model-root") { ProposedParentAlias = "ROOT", ResolutionBasis = "model-parent-proposal" }]);
        try
        {
            var result = await OutlineReuse.GetAsync(path, "file.docx", new OutlineStore(root), "k",
                () => Task.FromResult<IReadOnlyList<HeadingRecord>>([H(1, 1, "A")]), () => evidence);
            Assert.Equal("ROOT", Assert.Single(result.Current!.Placement!.Decisions).ProposedParentAlias);
        }
        finally { File.Delete(path); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
