using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Tests;

public sealed class OutlineWritebackTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"dhx-writeback-{Guid.NewGuid():N}")).FullName;

    private string Source
    {
        get
        {
            var path = Path.Combine(_dir, "nguon.docx");
            if (!File.Exists(path)) SampleDocumentFactory.Create(path);
            return path;
        }
    }

    private string Target => Path.Combine(_dir, "dich.docx");

    [Fact]
    public void Applied_levels_land_on_the_paragraph_the_model_pointed_at()
    {
        var slim = new DocxSlimExtractor().Extract(Source);
        var fake = slim.Paragraphs.First(p =>
            p.Role == ParagraphRole.HeadingCandidate && p.OutlineLevel is null);

        var result = OutlineWriteback.Apply(Source, Target, Outline(slim, Accepted(fake, level: 2)),
            new ExtractionOptions());

        Assert.Equal(1, result.Applied);
        Assert.Empty(result.Skipped);

        var written = new DocxSlimExtractor().Extract(Target);
        var updated = written.ByIndex(fake.Index)!;
        Assert.Equal(1, updated.OutlineLevel);          // cấp 2 → w:outlineLvl = 1
        Assert.Equal(fake.Text, updated.Text);
        Assert.Equal(fake.StableId, updated.StableId);
        Assert.Equal(fake.StyleId, updated.StyleId);    // không đổi style khi không được yêu cầu
    }

    [Fact]
    public void Source_document_is_never_touched()
    {
        var before = File.ReadAllBytes(Source);
        var slim = new DocxSlimExtractor().Extract(Source);
        var fake = slim.Paragraphs.First(p => p.Role == ParagraphRole.HeadingCandidate);

        OutlineWriteback.Apply(Source, Target, Outline(slim, Accepted(fake, level: 2)),
            new ExtractionOptions());

        Assert.Equal(before, File.ReadAllBytes(Source));
    }

    [Fact]
    public void Every_other_paragraph_keeps_its_text_verbatim()
    {
        var slim = new DocxSlimExtractor().Extract(Source);
        var fake = slim.Paragraphs.First(p => p.Role == ParagraphRole.HeadingCandidate);

        OutlineWriteback.Apply(Source, Target, Outline(slim, Accepted(fake, level: 2)),
            new ExtractionOptions());

        var written = new DocxSlimExtractor().Extract(Target);
        Assert.Equal(slim.Paragraphs.Count, written.Paragraphs.Count);
        Assert.Equal(
            slim.Paragraphs.Select(p => p.Text),
            written.Paragraphs.Select(p => p.Text));
    }

    [Fact]
    public void Headings_still_waiting_for_review_are_skipped_not_written()
    {
        var slim = new DocxSlimExtractor().Extract(Source);
        var fake = slim.Paragraphs.First(p =>
            p.Role == ParagraphRole.HeadingCandidate && p.OutlineLevel is null);
        var pending = Accepted(fake, level: 2);
        pending.DecisionStatus = HeadingDecisionStatus.RequiresReview;

        var result = OutlineWriteback.Apply(Source, Target, Outline(slim, pending),
            new ExtractionOptions());

        Assert.Equal(0, result.Applied);
        Assert.Equal("requires_review", Assert.Single(result.Skipped).Reason);
        Assert.Null(new DocxSlimExtractor().Extract(Target).ByIndex(fake.Index)!.OutlineLevel);
    }

    /// <summary>
    /// M9.5a: HeadingRecord.Level is nullable so a route can abstain instead of guessing. The legacy
    /// writeback still needs a real level to set w:outlineLvl, so a null one is rejected explicitly
    /// (not silently coerced) with the same "level_unresolved" reason PdfProductWriteback already uses.
    /// </summary>
    [Fact]
    public void Heading_with_unresolved_level_is_skipped_not_defaulted()
    {
        var slim = new DocxSlimExtractor().Extract(Source);
        var fake = slim.Paragraphs.First(p =>
            p.Role == ParagraphRole.HeadingCandidate && p.OutlineLevel is null);
        var unresolved = Accepted(fake, level: 2);
        unresolved.Level = null;

        var result = OutlineWriteback.Apply(Source, Target, Outline(slim, unresolved),
            new ExtractionOptions());

        Assert.Equal(0, result.Applied);
        Assert.Equal("level_unresolved", Assert.Single(result.Skipped).Reason);
        Assert.Null(new DocxSlimExtractor().Extract(Target).ByIndex(fake.Index)!.OutlineLevel);
    }

    [Fact]
    public void Paragraph_holding_both_a_heading_and_body_text_is_left_alone()
    {
        var slim = new DocxSlimExtractor().Extract(Source);
        var fake = slim.Paragraphs.First(p => p.Role == ParagraphRole.HeadingCandidate);
        var inline = Accepted(fake, level: 2);
        inline.OriginalText = fake.Text;
        inline.HeadingSpan = new TextOffsetSpan(0, fake.Text.Length);
        inline.InlineBody = fake.Text;
        inline.InlineBodySpan = new TextOffsetSpan(0, fake.Text.Length);

        var result = OutlineWriteback.Apply(Source, Target, Outline(slim, inline),
            new ExtractionOptions());

        Assert.Equal(0, result.Applied);
        Assert.Equal("inline_body_not_splittable", Assert.Single(result.Skipped).Reason);
    }

    [Fact]
    public void Writing_onto_the_source_path_is_refused()
    {
        var slim = new DocxSlimExtractor().Extract(Source);

        Assert.Throws<InvalidOperationException>(() =>
            OutlineWriteback.Apply(Source, Source, Outline(slim), new ExtractionOptions()));
    }

    [Fact]
    public void Existing_target_is_kept_unless_overwrite_was_granted()
    {
        var slim = new DocxSlimExtractor().Extract(Source);
        File.WriteAllText(Target, "giữ nguyên");

        Assert.Throws<InvalidOperationException>(() =>
            OutlineWriteback.Apply(Source, Target, Outline(slim), new ExtractionOptions()));

        Assert.Equal("giữ nguyên", File.ReadAllText(Target));
    }

    [Fact]
    public void Stale_index_from_a_shifted_document_is_skipped_rather_than_misapplied()
    {
        var slim = new DocxSlimExtractor().Extract(Source);
        var fake = slim.Paragraphs.First(p => p.Role == ParagraphRole.HeadingCandidate);
        var moved = Accepted(fake, level: 2, stableId: "body[1]/p[999]");

        var result = OutlineWriteback.Apply(Source, Target, Outline(slim, moved),
            new ExtractionOptions());

        Assert.Equal(0, result.Applied);
        Assert.Equal("stable_id_mismatch", Assert.Single(result.Skipped).Reason);
    }

    [Fact]
    public void Heading_styles_are_applied_only_when_asked_for()
    {
        var slim = new DocxSlimExtractor().Extract(Source);
        var fake = slim.Paragraphs.First(p =>
            p.Role == ParagraphRole.HeadingCandidate && p.OutlineLevel is null);

        OutlineWriteback.Apply(Source, Target, Outline(slim, Accepted(fake, level: 2)),
            new ExtractionOptions(), new OutlineWritebackOptions { ApplyHeadingStyles = true });

        var written = new DocxSlimExtractor().Extract(Target).ByIndex(fake.Index)!;
        Assert.Equal("Heading2", written.StyleId);
        Assert.Equal(fake.Text, written.Text);
    }

    private static HeadingRecord Accepted(SlimParagraph paragraph, int level, string? stableId = null) => new()
    {
        Index = paragraph.Index,
        StableId = stableId ?? paragraph.StableId,
        Level = level,
        Text = paragraph.Text,
        StyleId = paragraph.StyleId,
        Source = HeadingSource.Model,
        Confidence = 0.9,
        DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence,
    };

    private static DocumentOutline Outline(SlimDocument slim, params HeadingRecord[] headings) => new()
    {
        File = slim.FileName,
        ParagraphCount = slim.Paragraphs.Count,
        CandidateCount = headings.Length,
        Headings = headings,
    };

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
