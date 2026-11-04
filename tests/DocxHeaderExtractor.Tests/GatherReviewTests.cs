using DocxHeaderExtractor.DocumentProcessing.Gather;
using DocxHeaderExtractor.DocumentProcessing.Output;
using DocxHeaderExtractor.Contracts.Models;

namespace DocxHeaderExtractor.Tests;

/// <summary>Heading and content in one paragraph, the goal built from both, and the review applied before writing. No model is called.</summary>
public sealed class GatherReviewTests
{
    static string P(string text) => $"<w:p><w:r><w:t xml:space=\"preserve\">{text}</w:t></w:r></w:p>";
    static string Hash(string path) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

    static HeadingRecord Heading(int index, int? level, string text, string stableId) => new() { Index = index, Level = level, Text = text, StableId = stableId };

    static GatheredFact Fact(string unit, string text) => new([new SourcedPart(unit, "A", unit, "text", text, 0, text.Length)], []);

    static GatherSection Section(string heading, params GatheredFact[] facts) =>
        new("body[1]/p[1]", heading, heading, new GatherRun(facts, [], [], [], [], [], 1), []);

    sealed class NoFacts : IGatherModel
    {
        public string? LastUser;
        public int Calls;
        public Task<string> CompleteAsync(string system, string user, CancellationToken ct) { Calls++; LastUser = user; return Task.FromResult("{\"facts\":[]}"); }
    }

    [Fact]
    public void A_heading_with_its_content_in_the_same_paragraph_keeps_both_apart()
    {
        var path = RawDocxFixtureTests.Build(P("Opening: The meeting was called to order at 7:00 am.") + P("Present:"));
        try
        {
            var source = GatherUnits.Read(path, "T", [Heading(0, 1, "Opening:", "body[1]/p[1]"), Heading(1, 1, "Present:", "body[1]/p[2]")]);

            var opening = source.Units[0];
            Assert.Equal(8, opening.HeadingEnd);
            Assert.Equal("The meeting was called to order at 7:00 am.", opening.InlineBody);
            Assert.DoesNotContain(source.Gaps, g => g.Reason == "heading_boundary_unverified");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task The_goal_of_a_heading_includes_the_content_written_after_it_in_the_same_paragraph()
    {
        var target = RawDocxFixtureTests.Build(P("Opening: The meeting was called to order at 7:00 am.") + P("Present:") + P("Ông A."));
        var source = RawDocxFixtureTests.Build(P("Nguồn."));
        try
        {
            var outline = new[] { Heading(0, 1, "Opening:", "body[1]/p[1]"), Heading(1, 1, "Present:", "body[1]/p[2]") };
            var sections = await GatherDocument.RunAsync(target, outline, [new GatherSourceFile("S", source, null)], new NoFacts());

            Assert.Equal("Opening:\nThe meeting was called to order at 7:00 am.", sections[0].Goal);
            Assert.Equal("Present:\nÔng A.", sections[1].Goal);
        }
        finally { File.Delete(target); File.Delete(source); }
    }

    [Fact]
    public async Task Verified_heading_span_keeps_inline_content_in_goal_and_before_the_next_heading()
    {
        var target = RawDocxFixtureTests.Build(P("Mở đầu: Nội dung gốc.") + P("Phần tiếp") + P("Nội dung sau."));
        var source = RawDocxFixtureTests.Build(P("Nguồn."));
        try
        {
            var outline = new[]
            {
                new HeadingRecord { Index = 0, Level = 1, Text = "Mở đầu:", StableId = "body[1]/p[1]", HeadingSpan = new TextOffsetSpan(0, 7) },
                Heading(1, 1, "Phần tiếp", "body[1]/p[2]")
            };
            var sections = await GatherDocument.RunAsync(target, outline, [new GatherSourceFile("S", source, null)], new NoFacts());
            Assert.Equal("Mở đầu:\nNội dung gốc.", sections[0].Goal);
            Assert.Equal("body[1]/p[2]", sections[0].NextHeadingLocation);
            Assert.DoesNotContain(sections[0].Run.Gaps, g => g.SourceId == "target" && g.Reason == "heading_boundary_unverified");
        }
        finally { File.Delete(target); File.Delete(source); }
    }

    [Fact]
    public async Task Invalid_supplied_span_is_not_silently_repaired_and_writeback_is_skipped()
    {
        var target = RawDocxFixtureTests.Build(P("Mở đầu: Nội dung gốc.") + P("Phần tiếp"));
        var source = RawDocxFixtureTests.Build(P("Nguồn."));
        var output = Path.Combine(Path.GetTempPath(), "dhx-unverified-" + Guid.NewGuid().ToString("N") + ".docx");
        try
        {
            var outline = new[]
            {
                new HeadingRecord { Index = 0, Level = 1, Text = "Mở đầu:", StableId = "body[1]/p[1]", HeadingSpan = new TextOffsetSpan(0, 6) },
                Heading(1, 1, "Phần tiếp", "body[1]/p[2]")
            };
            var model = new NoFacts();
            var sections = await GatherDocument.RunAsync(target, outline, [new GatherSourceFile("S", source, null)], model);
            var targetUnits = GatherUnits.Read(target, "target", outline);
            Assert.False(targetUnits.Units[0].PathCertain);
            Assert.Contains(sections[0].Run.Gaps, g => g.SourceId == "target" && g.Reason == "heading_boundary_unverified");
            Assert.False(sections[0].Run.Complete);
            Assert.Equal(0, model.Calls);
            Assert.Equal(["TARGET_BOUNDARY_UNVERIFIED"], sections[0].Run.BatchFailures);
            Assert.Contains("S|body[1]/p[1]", sections[0].Run.UnreviewedUnitIds);
            var written = GatherWriteback.Apply(target, output, sections, DateTime.UtcNow, targetOutlineCoverageVerified: true, verifiedTargetSha256: Hash(target));
            Assert.Contains(written.SkippedHeadings, s => s.Contains("ranh giới heading", StringComparison.Ordinal));
        }
        finally { File.Delete(target); File.Delete(source); File.Delete(output); }
    }

    [Fact]
    public async Task A_heading_found_at_a_different_source_location_is_reported_and_cannot_authorize_writeback()
    {
        var target = RawDocxFixtureTests.Build(P("Giới thiệu") + P("Mở đầu: Nội dung gốc.") + P("Phần tiếp"));
        var source = RawDocxFixtureTests.Build(P("Nguồn."));
        var output = Path.Combine(Path.GetTempPath(), "dhx-wrong-source-" + Guid.NewGuid().ToString("N") + ".docx");
        try
        {
            var outline = new[] { Heading(0, 1, "Mở đầu:", "body[1]/p[1]"), Heading(2, 1, "Phần tiếp", "body[1]/p[3]") };
            var sections = await GatherDocument.RunAsync(target, outline, [new GatherSourceFile("S", source, null)], new NoFacts());
            Assert.Contains(sections[0].Run.Gaps, g => g.SourceId == "target" && g.Reason.StartsWith("heading_located_by_text:", StringComparison.Ordinal));
            Assert.False(sections[0].Run.Complete);
            var written = GatherWriteback.Apply(target, output, sections, DateTime.UtcNow, targetOutlineCoverageVerified: true, verifiedTargetSha256: Hash(target));
            Assert.Contains(written.SkippedHeadings, s => s.Contains("ranh giới heading", StringComparison.Ordinal));
        }
        finally { File.Delete(target); File.Delete(source); File.Delete(output); }
    }

    [Fact]
    public void Content_in_a_heading_paragraph_is_sent_under_that_heading_with_the_heading_words_named()
    {
        var unit = new GatherUnit("S|p1", "S", "p1", "body", false, true, "Opening: The meeting was called.", "", "pipeline", true,
            null, null, false, null, null, null, null, "k") { HeadingEnd = 8 };

        var payload = GatherCheck.Payload([unit]);

        Assert.Contains("\"heading\":\"Opening:\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"headingText\":\"Opening:\"", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void Review_removes_excluded_facts_flags_unsure_and_unreviewed_ones_and_keeps_the_rest()
    {
        var section = Section("Opening:", Fact("u1", "khai mạc"), Fact("u2", "phê duyệt tài liệu"), Fact("u3", "chủ tọa"), Fact("u4", "không có verdict"));
        var review = new GatherReviewResult([
            new(0, "KEEP", "đúng mục"), new(1, "EXCLUDE", "thuộc mục phê duyệt"), new(2, "UNSURE", "khác cuộc họp")], null, 0);

        var (applied, excluded) = GatherReviewApply.Apply(section, review);

        Assert.Equal(["khai mạc", "chủ tọa", "không có verdict"], applied.Run.Facts.Select(f => f.Render()));
        Assert.Empty(applied.Run.Facts[0].Flags);
        Assert.Contains("REVIEW_UNSURE: khác cuộc họp", applied.Run.Facts[1].Flags);
        Assert.Contains("REVIEW_MISSING", applied.Run.Facts[2].Flags);
        var dropped = Assert.Single(excluded);
        Assert.Equal("thuộc mục phê duyệt", dropped.Reason);
    }

    [Fact]
    public void A_failed_review_keeps_every_fact_with_a_flag()
    {
        var (applied, excluded) = GatherReviewApply.Apply(Section("Opening:", Fact("u1", "khai mạc")), new GatherReviewResult([], "SOURCE_MISMATCH", 0));

        Assert.Empty(excluded);
        Assert.Contains("REVIEW_FAILED:SOURCE_MISMATCH", Assert.Single(applied.Run.Facts).Flags);
    }

    [Fact]
    public void The_same_source_text_kept_under_two_headings_is_flagged_under_both()
    {
        var shared = Fact("u13", "Các tài liệu được thông qua");
        var sections = GatherReviewApply.FlagAcrossHeadings([Section("Opening:", shared), Section("Item 1", shared, Fact("u2", "khác"))]);

        Assert.Contains("ALSO_IN: Item 1", sections[0].Run.Facts[0].Flags);
        Assert.Contains("ALSO_IN: Opening:", sections[1].Run.Facts[0].Flags);
        Assert.Empty(sections[1].Run.Facts[1].Flags);
    }

    [Fact]
    public void A_fact_the_target_section_already_says_is_excluded_and_not_written_again()
    {
        var section = new GatherSection("body[1]/p[1]", "Item 1", "Item 1\nFor the Funding Request Template, language will be proposed by Ukraine on the process for endorsing proposals and will be inserted accordingly.",
            new GatherRun([
                Fact("u14", "For the Funding Request Template, language will be proposed by Ukraine on the process for endorsing proposals and will be inserted accordingly after the Voting Members are informed."),
                Fact("u10", "With this agreement, the GC provisionally approved the full set of documents.")], [], [], [], [], [], 1), []);

        var (applied, excluded) = GatherReviewApply.RemoveAlreadyInTarget(section);

        // A longer variant is not a repeat: it is kept, flagged against the line it is close to.
        Assert.Empty(excluded);
        Assert.Contains(applied.Run.Facts[0].Flags, f => f.StartsWith("POSSIBLE_CONFLICT: For the Funding Request Template", StringComparison.Ordinal));
        Assert.Empty(applied.Run.Facts[1].Flags);
    }

    [Fact]
    public void A_fact_already_written_word_for_word_is_excluded_and_one_with_another_date_is_kept_as_a_possible_conflict()
    {
        var section = new GatherSection("body[1]/p[1]", "Opening", "Opening\nThe meeting was called to order on 21 November.",
            new GatherRun([Fact("u1", "The meeting was called to order on 21 November."), Fact("u2", "The meeting was called to order on 27 November.")], [], [], [], [], [], 1), []);

        var (applied, excluded) = GatherReviewApply.RemoveAlreadyInTarget(section);

        Assert.StartsWith("ALREADY_IN_TARGET", Assert.Single(excluded).Reason, StringComparison.Ordinal);
        var kept = Assert.Single(applied.Run.Facts);
        Assert.Equal("The meeting was called to order on 27 November.", kept.Render());
        Assert.Contains(kept.Flags, f => f.StartsWith("POSSIBLE_CONFLICT", StringComparison.Ordinal));
    }
}
