using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M9.3b locks. The writeback acts only on <see cref="PdfProductHeading"/>'s canonical anchor —
/// paragraph index, stable id, span — never on a title search, never on <c>PdfEvidenceAnchor</c>, and
/// never by re-deriving a level or parent it does not already have.
/// </summary>
public sealed class PdfProductWritebackTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"dhx-product-writeback-{Guid.NewGuid():N}")).FullName;

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
    public void AppliedLevelsLandOnTheAnchoredParagraph()
    {
        var slim = new DocxSlimExtractor().Extract(Source);
        var fake = slim.Paragraphs.First(p => p.Role == ParagraphRole.HeadingCandidate && p.OutlineLevel is null);

        var result = PdfProductWriteback.Apply(Source, Target, Output(Heading(fake, level: 2)), new ExtractionOptions());

        Assert.Equal(1, result.Applied);
        Assert.Empty(result.Skipped);

        var written = new DocxSlimExtractor().Extract(Target);
        var updated = written.ByIndex(fake.Index)!;
        Assert.Equal(1, updated.OutlineLevel);          // level 2 -> w:outlineLvl = 1
        Assert.Equal(fake.Text, updated.Text);
        Assert.Equal(fake.StableId, updated.StableId);
    }

    [Fact]
    public void SourceDocumentIsNeverTouched()
    {
        var before = File.ReadAllBytes(Source);
        var slim = new DocxSlimExtractor().Extract(Source);
        var fake = slim.Paragraphs.First(p => p.Role == ParagraphRole.HeadingCandidate);

        PdfProductWriteback.Apply(Source, Target, Output(Heading(fake, level: 2)), new ExtractionOptions());

        Assert.Equal(before, File.ReadAllBytes(Source));
    }

    [Fact]
    public void EveryOtherParagraphKeepsItsTextVerbatim()
    {
        var slim = new DocxSlimExtractor().Extract(Source);
        var fake = slim.Paragraphs.First(p => p.Role == ParagraphRole.HeadingCandidate);

        PdfProductWriteback.Apply(Source, Target, Output(Heading(fake, level: 2)), new ExtractionOptions());

        var written = new DocxSlimExtractor().Extract(Target);
        Assert.Equal(slim.Paragraphs.Count, written.Paragraphs.Count);
        Assert.Equal(slim.Paragraphs.Select(p => p.Text), written.Paragraphs.Select(p => p.Text));
    }

    /// <summary>An unresolved level is evidence M9.1 already reported, not a gap for writeback to fill.</summary>
    [Fact]
    public void HeadingWithoutAResolvedLevelIsSkipped()
    {
        var slim = new DocxSlimExtractor().Extract(Source);
        var fake = slim.Paragraphs.First(p => p.Role == ParagraphRole.HeadingCandidate && p.OutlineLevel is null);

        var result = PdfProductWriteback.Apply(Source, Target, Output(Heading(fake, level: null)), new ExtractionOptions());

        Assert.Equal(0, result.Applied);
        Assert.Equal("level_unresolved", Assert.Single(result.Skipped).Reason);
        Assert.Null(new DocxSlimExtractor().Extract(Target).ByIndex(fake.Index)!.OutlineLevel);
    }

    [Fact]
    public void StaleStableIdIsSkippedRatherThanMisapplied()
    {
        var slim = new DocxSlimExtractor().Extract(Source);
        var fake = slim.Paragraphs.First(p => p.Role == ParagraphRole.HeadingCandidate);

        var result = PdfProductWriteback.Apply(
            Source, Target, Output(Heading(fake, level: 2, stableId: "body[1]/p[999]")), new ExtractionOptions());

        Assert.Equal(0, result.Applied);
        Assert.Equal("stable_id_mismatch", Assert.Single(result.Skipped).Reason);
    }

    /// <summary>The anchor's span must still point at the text the projection captured.</summary>
    [Fact]
    public void SpanThatNoLongerMatchesTheSourceTextIsRejected()
    {
        var slim = new DocxSlimExtractor().Extract(Source);
        var fake = slim.Paragraphs.First(p => p.Role == ParagraphRole.HeadingCandidate);

        var stale = Heading(fake, level: 2) with { Text = fake.Text + " đã đổi" };
        var result = PdfProductWriteback.Apply(Source, Target, Output(stale), new ExtractionOptions());

        Assert.Equal(0, result.Applied);
        Assert.Equal("anchor_text_mismatched", Assert.Single(result.Skipped).Reason);
    }

    [Fact]
    public void WritingOntoTheSourcePathIsRefused()
    {
        var slim = new DocxSlimExtractor().Extract(Source);
        var fake = slim.Paragraphs.First(p => p.Role == ParagraphRole.HeadingCandidate);

        Assert.Throws<InvalidOperationException>(() =>
            PdfProductWriteback.Apply(Source, Source, Output(Heading(fake, level: 2)), new ExtractionOptions()));
    }

    [Fact]
    public void ExistingTargetIsKeptUnlessOverwriteWasGranted()
    {
        File.WriteAllText(Target, "giữ nguyên");

        Assert.Throws<InvalidOperationException>(() =>
            PdfProductWriteback.Apply(Source, Target, Output(), new ExtractionOptions()));

        Assert.Equal("giữ nguyên", File.ReadAllText(Target));
    }

    [Fact]
    public void HeadingStylesAreAppliedOnlyWhenAskedFor()
    {
        var slim = new DocxSlimExtractor().Extract(Source);
        var fake = slim.Paragraphs.First(p => p.Role == ParagraphRole.HeadingCandidate && p.OutlineLevel is null);

        PdfProductWriteback.Apply(Source, Target, Output(Heading(fake, level: 2)), new ExtractionOptions(),
            new OutlineWritebackOptions { ApplyHeadingStyles = true });

        var written = new DocxSlimExtractor().Extract(Target).ByIndex(fake.Index)!;
        Assert.Equal("Heading2", written.StyleId);
        Assert.Equal(fake.Text, written.Text);
    }

    /// <summary>Same frozen output against a fresh copy of the same source has to land identically.</summary>
    [Fact]
    public void ApplyingTheSameOutputTwiceIsDeterministic()
    {
        var slim = new DocxSlimExtractor().Extract(Source);
        var fake = slim.Paragraphs.First(p => p.Role == ParagraphRole.HeadingCandidate && p.OutlineLevel is null);
        var output = Output(Heading(fake, level: 2));

        var first = PdfProductWriteback.Apply(Source, Path.Combine(_dir, "a.docx"), output, new ExtractionOptions());
        var second = PdfProductWriteback.Apply(Source, Path.Combine(_dir, "b.docx"), output, new ExtractionOptions());

        Assert.Equal(first.Applied, second.Applied);
        Assert.Equal(File.ReadAllBytes(first.OutputPath), File.ReadAllBytes(second.OutputPath));
    }

    private const string HeadingText = "Điều 1. Phạm vi áp dụng";
    private const string BodyText = "Quy trình này áp dụng cho toàn bộ đơn vị trực thuộc kể từ ngày ký.";

    private string WriteInlineSource(bool separateRuns)
    {
        var path = Path.Combine(_dir, separateRuns ? "tach-duoc.docx" : "tach-khong-duoc.docx");
        using var wp = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = wp.AddMainDocumentPart();

        var joined = separateRuns
            ? new Paragraph(
                new Run(new Text(HeadingText + " ") { Space = SpaceProcessingModeValues.Preserve }),
                new Run(new Text(BodyText)))
            : new Paragraph(new Run(new Text(HeadingText + "   " + BodyText)
                { Space = SpaceProcessingModeValues.Preserve }));

        main.Document = new Document(new Body(
            joined,
            new Paragraph(new Run(new Text("Đoạn thân bài kế tiếp, đủ dài để không bị coi là ứng viên tiêu đề nào cả.")))));
        main.Document.Save();
        return path;
    }

    /// <summary>Heading and body in the same paragraph, split across two runs: the boundary sits at a run start.</summary>
    [Fact]
    public void ParagraphHoldingAHeadingAndTrailingBodyIsSplit()
    {
        var source = WriteInlineSource(separateRuns: true);
        var slim = new DocxSlimExtractor().Extract(source);
        var joined = slim.Paragraphs.First(p => p.Text.StartsWith(HeadingText));

        var heading = new PdfProductHeading(
            $"p[{joined.Index}]#0-{HeadingText.Length}", joined.Index, joined.StableId,
            new DocxTextSpan(0, HeadingText.Length), HeadingText, "Heading", 2, null, true, []);

        var result = PdfProductWriteback.Apply(source, Target, Output(source, heading), new ExtractionOptions());

        Assert.Equal(1, result.Applied);
        Assert.Empty(result.Skipped);

        var written = new DocxSlimExtractor().Extract(Target);
        var head = written.ByIndex(joined.Index)!;
        var tail = written.ByIndex(joined.Index + 1)!;

        Assert.Equal(HeadingText, head.Text);
        Assert.Equal(1, head.OutlineLevel);
        Assert.Equal(BodyText, tail.Text);
        Assert.Null(tail.OutlineLevel);
        Assert.Equal(slim.Paragraphs.Count + 1, written.Paragraphs.Count);
        Assert.Equal(joined.Text, $"{head.Text} {tail.Text}");
    }

    /// <summary>Fail-closed: a boundary inside a run would require cutting the run's text in half.</summary>
    [Fact]
    public void BoundaryInsideARunIsStillRejected()
    {
        var source = WriteInlineSource(separateRuns: false);
        var slim = new DocxSlimExtractor().Extract(source);
        var joined = slim.Paragraphs.First(p => p.Text.StartsWith(HeadingText));

        var heading = new PdfProductHeading(
            $"p[{joined.Index}]#0-{HeadingText.Length}", joined.Index, joined.StableId,
            new DocxTextSpan(0, HeadingText.Length), HeadingText, "Heading", 2, null, true, []);

        var result = PdfProductWriteback.Apply(source, Target, Output(source, heading), new ExtractionOptions());

        Assert.Equal(0, result.Applied);
        Assert.Equal("inline_body_not_splittable", Assert.Single(result.Skipped).Reason);
        Assert.Equal(slim.Paragraphs.Count, new DocxSlimExtractor().Extract(Target).Paragraphs.Count);
    }

    /// <summary>A span that does not start at the paragraph's first character is a different operation this layer refuses.</summary>
    [Fact]
    public void SpanWithLeadingTextIsRejected()
    {
        var source = WriteInlineSource(separateRuns: true);
        var slim = new DocxSlimExtractor().Extract(source);
        var joined = slim.Paragraphs.First(p => p.Text.StartsWith(HeadingText));

        var heading = new PdfProductHeading(
            $"p[{joined.Index}]#2-4", joined.Index, joined.StableId,
            new DocxTextSpan(2, 4), joined.Text[2..4], "Heading", 2, null, true, []);

        var result = PdfProductWriteback.Apply(source, Target, Output(source, heading), new ExtractionOptions());

        Assert.Equal(0, result.Applied);
        Assert.Equal("leading_text_not_splittable", Assert.Single(result.Skipped).Reason);
    }

    private static PdfProductHeading Heading(SlimParagraph paragraph, int? level, string? stableId = null) =>
        new($"p[{paragraph.Index}]#0-{paragraph.Text.Length}", paragraph.Index, stableId ?? paragraph.StableId,
            new DocxTextSpan(0, paragraph.Text.Length), paragraph.Text, "Heading", level, null, true, []);

    // The fingerprint is part of the contract now, so these fixtures state the real one. They were
    // passing a placeholder while nothing read the field.
    private PdfProductOutput Output(params PdfProductHeading[] headings) => Output(Source, headings);

    private static PdfProductOutput Output(string sourcePath, params PdfProductHeading[] headings) =>
        new(Fingerprint(sourcePath), headings);

    internal static string Fingerprint(string path) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))
            .ToLowerInvariant();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
