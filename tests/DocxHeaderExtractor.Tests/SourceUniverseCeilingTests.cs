using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// SEMANTIC_RECALL_CEILING = SOURCE_OCCURRENCE_UNIVERSE, not a parser-side heuristic subset.
/// <para>
/// Parser-side source facts may still exist inside the DOCX route, but no role label defines what
/// the model is allowed to inspect.
/// </para>
/// </summary>
public sealed class SourceUniverseCeilingTests
{
    [Fact]
    public void EveryNonEmptyParagraphIsShownToTheModel()
    {
        var state = State();
        var universe = DocxAuthorityPipeline.BuildForAudit(state).ModelContexts;

        var nonEmpty = state.Paragraphs
            .Where(p => !string.IsNullOrWhiteSpace(p.Text))
            .ToArray();

        Assert.NotEmpty(nonEmpty);
        Assert.All(nonEmpty, p => Assert.Contains(p.SourceId, universe.Keys));
    }

    private static SourceDocument State()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DocxHeaderExtractor.sln")))
            dir = dir.Parent;
        var docx = Path.Combine(dir!.FullName, "todo10_8", "heading_corpus_95_word",
            "02_hop_dong_mua_sam", "026_WB_RFB_Goods_One_Envelope_2017.docx");
        Assert.True(File.Exists(docx), $"Missing fixture: {docx}");

        return new OpenXmlDocumentSource().Read(docx);
    }
}
