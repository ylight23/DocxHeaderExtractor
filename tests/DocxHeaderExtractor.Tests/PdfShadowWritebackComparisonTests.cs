using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M9.4 writeback half. Both underlying writebacks already verify their own output and throw on any
/// text corruption, so this comparison's job is narrower: does either lane agree with the other about
/// which original paragraphs were touched.
/// </summary>
public sealed class PdfShadowWritebackComparisonTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"dhx-shadow-writeback-{Guid.NewGuid():N}")).FullName;

    private string Source
    {
        get
        {
            var path = Path.Combine(_dir, "nguon.docx");
            if (!File.Exists(path)) SampleDocumentFactory.Create(path);
            return path;
        }
    }

    [Fact]
    public void BothLanesApplyingTheSameParagraphCountsAsASharedMutation()
    {
        var slim = new DocxSlimExtractor().Extract(Source);
        var fake = slim.Paragraphs.First(p => p.Role == ParagraphRole.HeadingCandidate && p.OutlineLevel is null);

        var legacyOutline = Outline(slim, LegacyHeading(fake, level: 2));
        var newOutput = NewOutput(NewHeading(fake, level: 2));

        var report = PdfShadowWritebackComparison.Compare(
            Source, Path.Combine(_dir, "legacy.docx"), Path.Combine(_dir, "new.docx"),
            legacyOutline, newOutput, new ExtractionOptions());

        Assert.Equal(1, report.LegacyModifiedParagraphs);
        Assert.Equal(1, report.NewModifiedParagraphs);
        Assert.Equal(1, report.SameSemanticMutations);
        Assert.Equal(0, report.NewAnchorFailures);
        Assert.Equal(0, report.NewLevelUnresolvedSkips);
        Assert.Equal(0, report.UnexpectedTextChanges);
    }

    /// <summary>An M9 abstention (unresolved level) is a real migration delta, not a shared mutation.</summary>
    [Fact]
    public void ANewLaneAbstentionIsNotCountedAsASharedMutation()
    {
        var slim = new DocxSlimExtractor().Extract(Source);
        var fake = slim.Paragraphs.First(p => p.Role == ParagraphRole.HeadingCandidate && p.OutlineLevel is null);

        var legacyOutline = Outline(slim, LegacyHeading(fake, level: 2));
        var newOutput = NewOutput(NewHeading(fake, level: null));

        var report = PdfShadowWritebackComparison.Compare(
            Source, Path.Combine(_dir, "legacy.docx"), Path.Combine(_dir, "new.docx"),
            legacyOutline, newOutput, new ExtractionOptions());

        Assert.Equal(1, report.LegacyModifiedParagraphs);
        Assert.Equal(0, report.NewModifiedParagraphs);
        Assert.Equal(0, report.SameSemanticMutations);
        Assert.Equal(1, report.NewLevelUnresolvedSkips);
    }

    [Fact]
    public void AStaleAnchorOnTheNewLaneIsCountedAsAnAnchorFailure()
    {
        var slim = new DocxSlimExtractor().Extract(Source);
        var fake = slim.Paragraphs.First(p => p.Role == ParagraphRole.HeadingCandidate && p.OutlineLevel is null);

        var legacyOutline = Outline(slim, LegacyHeading(fake, level: 2));
        var newOutput = NewOutput(NewHeading(fake, level: 2) with { Text = fake.Text + " đổi rồi" });

        var report = PdfShadowWritebackComparison.Compare(
            Source, Path.Combine(_dir, "legacy.docx"), Path.Combine(_dir, "new.docx"),
            legacyOutline, newOutput, new ExtractionOptions());

        Assert.Equal(1, report.NewAnchorFailures);
        Assert.Equal(0, report.SameSemanticMutations);
    }

    private static HeadingRecord LegacyHeading(SlimParagraph p, int level) => new()
    {
        Index = p.Index,
        StableId = p.StableId,
        SourceId = $"b{p.Index}",
        Level = level,
        Text = p.Text,
        Source = HeadingSource.Model,
        Confidence = 0.9,
        DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence,
    };

    private static PdfProductHeading NewHeading(SlimParagraph p, int? level) =>
        new($"@{p.StableId}#0-{p.Text.Length}", p.Index, p.StableId,
            new DocxTextSpan(0, p.Text.Length), p.Text, "Heading", level, null, true, []);

    private static DocumentOutline Outline(SlimDocument slim, params HeadingRecord[] headings) => new()
    {
        File = slim.FileName,
        ParagraphCount = slim.Paragraphs.Count,
        CandidateCount = headings.Length,
        Headings = headings,
    };

    // Bound to the real source fingerprint: the writeback now enforces the revision an output
    // declares, and a placeholder would be asserting against a document this output never described.
    private PdfProductOutput NewOutput(params PdfProductHeading[] headings) =>
        new(PdfProductWritebackTests.Fingerprint(Source), headings);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
