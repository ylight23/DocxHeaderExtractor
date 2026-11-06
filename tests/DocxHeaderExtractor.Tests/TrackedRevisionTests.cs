using DocxHeaderExtractor.Contracts.Docx;
using DocxHeaderExtractor.DocumentProcessing.Changes;
using DocxHeaderExtractor.DocumentProcessing.Gather;
using DocxHeaderExtractor.DocumentProcessing.Output;
using DocxHeaderExtractor.Sources.Docx.Changes;
using DocxHeaderExtractor.Sources.Docx.OpenXmlParser;

namespace DocxHeaderExtractor.Tests;

/// <summary>Tracked changes Word recorded in a file: read as evidence, and never read twice as text.</summary>
public sealed class TrackedRevisionTests
{
    const string Author = "w:author=\"An\" w:date=\"2026-11-06T09:00:00Z\"";

    // A sentence moved from the first paragraph to the second, a word inserted, a word deleted, a run made bold, a paragraph realigned.
    static readonly string Body =
        $"<w:p><w:r><w:t xml:space=\"preserve\">Giữ lại </w:t></w:r><w:moveFrom w:id=\"1\" {Author}><w:r><w:t>Câu dời đi.</w:t></w:r></w:moveFrom></w:p>" +
        $"<w:p><w:moveTo w:id=\"2\" {Author}><w:r><w:t>Câu dời đi.</w:t></w:r></w:moveTo></w:p>" +
        $"<w:p><w:r><w:t xml:space=\"preserve\">Tiếp đón </w:t></w:r><w:ins w:id=\"3\" {Author}><w:r><w:t>66</w:t></w:r></w:ins>" +
        $"<w:del w:id=\"4\" {Author}><w:r><w:delText>50</w:delText></w:r></w:del><w:r><w:t xml:space=\"preserve\"> lượt.</w:t></w:r></w:p>" +
        $"<w:p><w:r><w:rPr><w:b/><w:rPrChange w:id=\"5\" {Author}><w:rPr/></w:rPrChange></w:rPr><w:t>Mục A</w:t></w:r></w:p>" +
        $"<w:p><w:pPr><w:jc w:val=\"center\"/><w:pPrChange w:id=\"6\" {Author}><w:pPr/></w:pPrChange></w:pPr><w:r><w:t>Giữa</w:t></w:r></w:p>";

    [Fact]
    public void Text_moved_away_is_not_read_again_by_the_heading_reader()
    {
        var path = RawDocxFixtureTests.Build(Body);
        try
        {
            var paragraphs = new OpenXmlDocumentSource(new ExtractionOptions()).Read(path).Paragraphs;
            Assert.Equal("Giữ lại", paragraphs[0].Text.Trim());
            Assert.Equal("Câu dời đi.", paragraphs[1].Text.Trim());
            Assert.Equal("Tiếp đón 66 lượt.", paragraphs[2].Text.Trim());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Every_recorded_revision_is_read_with_its_paragraph_author_date_and_text()
    {
        var path = RawDocxFixtureTests.Build(Body);
        try
        {
            var version = DocxVersionReader.Read(path);
            var revisions = version.Revisions;
            Assert.Equal(["move_from", "move_to", "insert", "delete", "format_run", "format_paragraph"], revisions.Select(r => r.Kind));
            Assert.All(revisions, r => { Assert.Equal("An", r.Author); Assert.Equal("2026-11-06T09:00:00Z", r.Date); });
            Assert.Equal(["Câu dời đi.", "Câu dời đi.", "66", "50", "Mục A", "Giữa"], revisions.Select(r => r.Text));
            Assert.Equal(version.Paragraphs[0].Location, revisions[0].Location);
            Assert.Equal(version.Paragraphs[2].Location, revisions[3].Location);
            // The accepted view stays what it was: inserted and moved-to text kept, deleted and moved-away text left out.
            Assert.Equal("Tiếp đón 66 lượt.", version.Paragraphs[2].RawText);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void The_comparison_lists_the_recorded_revisions_beside_the_computed_changes()
    {
        // The template is the text before the author's edits; the document carries them as tracked changes.
        var template = RawDocxFixtureTests.Build("<w:p><w:r><w:t>Giữ lại Câu dời đi.</w:t></w:r></w:p><w:p><w:r><w:t>Tiếp đón 50 lượt.</w:t></w:r></w:p>");
        var document = RawDocxFixtureTests.Build(Body);
        try
        {
            var result = DocxComparer.Compare(DocxVersionReader.Read(template), DocxVersionReader.Read(document));
            Assert.Null(result.TemplateRevisions);
            Assert.Equal(6, result.DocumentRevisions!.Count);
            var number = Assert.Single(result.Changes, c => c.Document?.Text.Contains("66", StringComparison.Ordinal) == true);
            Assert.Contains("tracked_revision_recorded", number.Note);
            Assert.Contains(result.DocumentRevisions, r => r.Kind == "delete" && r.Text == "50");
        }
        finally { File.Delete(template); File.Delete(document); }
    }

    [Fact]
    public void A_file_without_tracked_changes_has_no_revisions()
    {
        var path = RawDocxFixtureTests.Build("<w:p><w:r><w:t>Không có vết sửa.</w:t></w:r></w:p>");
        try
        {
            Assert.Empty(DocxVersionReader.Read(path).Revisions);
            Assert.Null(DocxComparer.Compare(DocxVersionReader.Read(path), DocxVersionReader.Read(path)).DocumentRevisions);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_target_heading_edited_with_track_changes_still_receives_its_content()
    {
        // The target heading reads "Mục A" once the change is accepted: "cũ" deleted, "A" inserted, and a word moved away.
        var target = RawDocxFixtureTests.Build(
            $"<w:p><w:r><w:t xml:space=\"preserve\">Mục </w:t></w:r><w:del w:id=\"1\" {Author}><w:r><w:delText>cũ</w:delText></w:r></w:del>" +
            $"<w:ins w:id=\"2\" {Author}><w:r><w:t>A</w:t></w:r></w:ins><w:moveFrom w:id=\"3\" {Author}><w:r><w:t> rời</w:t></w:r></w:moveFrom></w:p>" +
            "<w:p><w:r><w:t>Nội dung đích</w:t></w:r></w:p>");
        var source = RawDocxFixtureTests.Build("<w:p><w:r><w:t>Mục A</w:t></w:r></w:p><w:p><w:r><w:t>Nội dung nguồn</w:t></w:r></w:p>");
        var output = Path.Combine(Path.GetTempPath(), "dhx-tracked-" + Guid.NewGuid().ToString("N") + ".docx");
        HeadingRecord H() => new() { Index = 0, StableId = "body[1]/p[1]", Text = "Mục A", Level = 1 };
        try
        {
            var plan = GatherByHeading.Build(target, [H()], [new GatherSourceFile("S", source, [H()])]);
            Assert.Equal(["Nội dung nguồn"], plan.Sections.Single().Run.Facts.Select(f => f.Render()));
            var written = GatherWriteback.Export(target, output, GatherByHeading.DecidedByChain(plan.Sections).Where(s => s.Run.Facts.Count > 0).ToList(),
                plan.Uncertain, DateTime.UtcNow, gaps: plan.Gaps, excluded: plan.Excluded);
            Assert.Empty(written.SkippedHeadings);
            using var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(output, false);
            var texts = doc.MainDocumentPart!.Document!.Body!.Elements<DocumentFormat.OpenXml.Wordprocessing.Paragraph>().Select(p => p.InnerText).ToList();
            var at = texts.FindIndex(t => t.Contains("Nội dung nguồn", StringComparison.Ordinal));
            // Written in the body after the heading and its content, not in the appendix.
            var appendix = texts.FindIndex(t => t.Contains(GatherWriteback.AppendixTitle, StringComparison.Ordinal));
            Assert.True(at >= 2 && (appendix < 0 || at < appendix), $"at={at}, appendix={appendix}");
        }
        finally { File.Delete(target); File.Delete(source); File.Delete(output); }
    }
}
