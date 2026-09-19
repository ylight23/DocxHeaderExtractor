using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Routing;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// One parse, one catalog: what a consumer is handed is what the model was shown.
/// <para>
/// The lane used to rebuild the consumer catalog from the execution audit after the semantic stage
/// had finished. The audit records <c>DisplayText</c> - a readable rendering for a person - while
/// the model and the binder use the declared glyph projection, so a consumer could receive
/// different text for precisely the occurrences where a PDF's reconstruction is hard. Provenance,
/// section and chunk projection, and any occurrence-level evaluation all read that catalog, so the
/// divergence would surface as a semantic disagreement with no semantic cause.
/// </para>
/// <para>
/// These assertions are written to fail if the rebuild returns, which is why the first one proves
/// the two representations genuinely differ on this document: without that, every other assertion
/// here would pass on a document where nothing could go wrong.
/// </para>
/// </summary>
public sealed class PdfSourceCatalogIdentityTests
{
    private const string Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";

    [Fact]
    public void The_two_representations_of_a_block_really_do_disagree_on_this_document()
    {
        // The discriminating premise. HeadingReadable repairs a PDF's broken spacing its own way;
        // the projection repairs it from glyph geometry. On a document where they happened to agree
        // everywhere, the catalog could be rebuilt from the audit and no test would notice.
        var blocks = Blocks();

        var divergent = blocks
            .Where(block => !string.Equals(block.DisplayText, block.VerbatimText, StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(divergent);
    }

    [Fact]
    public async Task A_consumer_reads_the_occurrence_the_model_was_shown()
    {
        var blocks = Blocks().ToDictionary(block => block.Id, StringComparer.Ordinal);

        var document = await PdfCanonicalExtraction.RunAsync(
            UploadedFile.FromLocalPath(Path.Combine(RepositoryRoot(), Pdf.Replace('/', Path.DirectorySeparatorChar))),
            new PipelineOptions { DisableLlm = true });

        Assert.NotEmpty(document.SourceCatalog.Units);
        Assert.All(document.SourceCatalog.Units, unit =>
        {
            Assert.True(blocks.ContainsKey(unit.SourceId), $"catalog unit not a parser block: {unit.SourceId}");
            Assert.Equal(blocks[unit.SourceId].VerbatimText, unit.Text);
        });
    }

    [Fact]
    public async Task The_catalog_covers_every_source_occurrence_rather_than_the_audited_subset()
    {
        // The audit is a record of a run; the catalog is the source universe. Deriving one from the
        // other also tied the consumer's view of the document to what the run happened to audit.
        var blocks = Blocks();

        var document = await PdfCanonicalExtraction.RunAsync(
            UploadedFile.FromLocalPath(Path.Combine(RepositoryRoot(), Pdf.Replace('/', Path.DirectorySeparatorChar))),
            new PipelineOptions { DisableLlm = true });

        Assert.Equal(
            blocks.Select(block => block.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
            document.SourceCatalog.Units.Select(unit => unit.SourceId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task At_least_one_unit_carries_text_the_audit_would_have_spelled_differently()
    {
        // The regression this file exists for, stated positively: if the catalog is ever rebuilt
        // from the audit again, these units come back carrying DisplayText and this fails.
        var blocks = Blocks();
        var divergent = blocks
            .Where(block => !string.Equals(block.DisplayText, block.VerbatimText, StringComparison.Ordinal))
            .ToDictionary(block => block.Id, StringComparer.Ordinal);

        var document = await PdfCanonicalExtraction.RunAsync(
            UploadedFile.FromLocalPath(Path.Combine(RepositoryRoot(), Pdf.Replace('/', Path.DirectorySeparatorChar))),
            new PipelineOptions { DisableLlm = true });

        var checkedUnits = document.SourceCatalog.Units
            .Where(unit => divergent.ContainsKey(unit.SourceId))
            .ToArray();

        Assert.NotEmpty(checkedUnits);
        Assert.All(checkedUnits, unit =>
            Assert.NotEqual(divergent[unit.SourceId].DisplayText, unit.Text));
    }

    private static IReadOnlyList<PdfSemanticBlock> Blocks()
    {
        var path = Path.Combine(RepositoryRoot(), Pdf.Replace('/', Path.DirectorySeparatorChar));
        IReadOnlyList<PdfLine> lines;
        using (var document = UglyToad.PdfPig.PdfDocument.Open(path))
        {
            lines = PdfLineExtraction.ExtractLines(document);
        }

        return PdfSemanticBlockGrouper.Build(PdfLineBlockFilter.Analyze(lines), includeRiskLines: true);
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DocxHeaderExtractor.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Cannot find repository root.");
    }
}
