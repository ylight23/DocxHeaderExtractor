using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxHeaderExtractor.DocumentProcessing.Output;
using DocxHeaderExtractor.Sources.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Harness;
using DocxHeaderExtractor.Contracts.Routing;
using DocxHeaderExtractor.DocumentProcessing.Intake;
using DocxHeaderExtractor.DocumentProcessing.Binding;
using DocxHeaderExtractor.DocumentProcessing.Selection;
using DocxHeaderExtractor.Sources.Docx;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// A paragraph whose text breaks lines (w:br) is one selectable line. Its heading must survive validation like any other line.
/// </summary>
public sealed class LineBreakHeadingTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "dhx-br-" + Guid.NewGuid().ToString("N"));

    public LineBreakHeadingTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task A_selected_paragraph_with_line_breaks_is_validated_as_a_heading()
    {
        var path = Path.Combine(dir, "breaks.docx");
        using (var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document(new Body(
                new Paragraph(new Run(new Text("Chapter I"))),
                new Paragraph(
                    new Run(new Text("Article 8. Prohibited acts")),
                    new Run(new Break()),
                    new Run(new Text("1. Using cyberspace to do the following:")),
                    new Run(new Break()),
                    new Run(new Text("a) An act under clause 1 of Article 18."))),
                new Paragraph(new Run(new Text("Closing paragraph.")))));
        }
        var source = DocxSourceAdapter.Build(new OpenXmlDocumentSource(new PipelineOptions().Extraction).Read(path)).Snapshot;
        var article = source.Atoms.OrderBy(a => a.Ordinal).First(a => a.Text.StartsWith("Article 8", StringComparison.Ordinal));
        var selector = new FixedSelector(new HashSet<string> { article.Alias });

        var authority = new SelectionAuthority(selector, path, "sk-test", new Dictionary<string, string>(StringComparer.Ordinal),
            null, () => { });
        var decided = await authority.DecideAsync(source, CancellationToken.None);

        Assert.Contains(decided.Decisions, d => d.Id == article.SourceId);
        var validated = DecisionBinder.BindAndValidate(source.OccurrenceContexts, decided.Decisions);
        Assert.Contains(validated, v => v.SourceId == article.SourceId);
    }

    private sealed class FixedSelector(IReadOnlySet<string> aliases) : IHeadingSelector
    {
        public string SelectorId => "fixed-break-test";

        public Task<HeadingSelection> SelectAsync(string pdfPath, string apiKey, CancellationToken ct) =>
            Task.FromResult(new HeadingSelection(aliases, 1, 1, [], ["{}"], 1, 0.0));
    }
}
