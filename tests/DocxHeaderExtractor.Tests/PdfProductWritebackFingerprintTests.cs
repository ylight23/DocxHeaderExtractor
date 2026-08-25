using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M11-B3.1 locks. A product output states the document revision it was derived from; the writeback
/// may only act against that revision.
/// <para>
/// The per-heading checks are not a substitute. They establish that a particular anchor still holds,
/// which is a different claim from "this is the document the anchors were taken from" - two documents
/// built the same way share stable ids and span text, so anchors can match across a boundary the
/// output never authorised. That is what the third test here pins down.
/// </para>
/// </summary>
public sealed class PdfProductWritebackFingerprintTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"dhx-writeback-fingerprint-{Guid.NewGuid():N}")).FullName;

    private string Target => Path.Combine(_dir, "dich.docx");

    /// <summary>A matching fingerprint changes nothing: the existing behaviour must survive intact.</summary>
    [Fact]
    public void MatchingFingerprintPreservesExistingBehaviour()
    {
        var source = Document("nguon.docx");
        var heading = Heading(source);

        var result = PdfProductWriteback.Apply(
            source, Target, Output(source, heading), new ExtractionOptions());

        Assert.Equal(1, result.Applied);
        Assert.Empty(result.Skipped);
    }

    /// <summary>A different revision refuses the whole operation rather than any part of it.</summary>
    [Fact]
    public void DifferentFingerprintRefusesTheWholeWriteback()
    {
        var source = Document("nguon.docx");
        var other = Document("khac.docx", "Một đoạn khác hẳn để đổi fingerprint.");

        var error = Assert.Throws<InvalidOperationException>(() =>
            PdfProductWriteback.Apply(source, Target, Output(other, Heading(source)), new ExtractionOptions()));

        Assert.Contains("writeback bị từ chối", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The point of the guard. Both documents are built identically, so every per-heading check would
    /// pass - stable id, span, text. Only the document fingerprint distinguishes them, and it must be
    /// enough on its own to refuse.
    /// </summary>
    [Fact]
    public void FingerprintMismatchIsNotRescuedByAnchorsThatStillMatch()
    {
        var source = Document("nguon.docx");
        var twin = Document("song-sinh.docx", extra: "Chỉ khác một dòng cuối.");
        var heading = Heading(source);

        // The anchor genuinely resolves in the twin as well: same builder, same first heading.
        var slim = new DocxSlimExtractor().Extract(twin);
        Assert.Contains(slim.Paragraphs, p => p.StableId == heading.StableId && p.Text == heading.Text);

        Assert.Throws<InvalidOperationException>(() =>
            PdfProductWriteback.Apply(source, Target, Output(twin, heading), new ExtractionOptions()));
    }

    /// <summary>A refused writeback leaves nothing behind - the copy is never even made.</summary>
    [Fact]
    public void RefusedWritebackWritesNothing()
    {
        var source = Document("nguon.docx");
        var other = Document("khac.docx", "Một đoạn khác hẳn để đổi fingerprint.");

        Assert.Throws<InvalidOperationException>(() =>
            PdfProductWriteback.Apply(source, Target, Output(other, Heading(source)), new ExtractionOptions()));

        Assert.False(File.Exists(Target));
    }

    /// <summary>The determinism the guard sits in front of is unchanged.</summary>
    [Fact]
    public void ValidWritebackRemainsDeterministic()
    {
        var source = Document("nguon.docx");
        var output = Output(source, Heading(source));

        PdfProductWriteback.Apply(source, Path.Combine(_dir, "a.docx"), output, new ExtractionOptions());
        PdfProductWriteback.Apply(source, Path.Combine(_dir, "b.docx"), output, new ExtractionOptions());

        Assert.Equal(
            File.ReadAllBytes(Path.Combine(_dir, "a.docx")).Length,
            File.ReadAllBytes(Path.Combine(_dir, "b.docx")).Length);
        Assert.Equal(
            new DocxSlimExtractor().Extract(Path.Combine(_dir, "a.docx")).Paragraphs.Select(p => p.Text),
            new DocxSlimExtractor().Extract(Path.Combine(_dir, "b.docx")).Paragraphs.Select(p => p.Text));
    }

    private string Document(string name, string? extra = null)
    {
        var path = Path.Combine(_dir, name);
        if (!File.Exists(path))
        {
            SampleDocumentFactory.Create(path);
            if (extra is not null) Append(path, extra);
        }
        return path;
    }

    private static void Append(string path, string text)
    {
        using var document = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(path, true);
        var body = document.MainDocumentPart!.Document!.Body!;
        body.AppendChild(new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
            new DocumentFormat.OpenXml.Wordprocessing.Run(
                new DocumentFormat.OpenXml.Wordprocessing.Text(text))));
    }

    private static PdfProductHeading Heading(string sourcePath)
    {
        var slim = new DocxSlimExtractor().Extract(sourcePath);
        var candidate = slim.Paragraphs.First(p => p.Role == ParagraphRole.HeadingCandidate);
        return new PdfProductHeading(
            $"p[{candidate.Index}]#0-{candidate.Text.Length}", candidate.Index, candidate.StableId,
            new DocxTextSpan(0, candidate.Text.Length), candidate.Text, "Heading", 2, null, true, []);
    }

    private static PdfProductOutput Output(string fingerprintOf, PdfProductHeading heading) =>
        new(PdfProductWritebackTests.Fingerprint(fingerprintOf), [heading]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
