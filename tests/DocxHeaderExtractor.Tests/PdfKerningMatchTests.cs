using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfKerningMatchTests
{
    [Fact]
    public void TinyGlyphGapIsNotTreatedAsWordBoundaryForMatching()
    {
        Assert.False(PdfLineExtraction.IsMatchWordGapForAudit(.8, 12, 12));
    }

    [Fact]
    public void NormalWordGapRemainsBoundaryForMatching()
    {
        Assert.True(PdfLineExtraction.IsMatchWordGapForAudit(4.0, 12, 12));
    }

    [Fact]
    public void KerningEvidenceAllowsFragmentedWordShapeButNotOrdinarySpacedMarker()
    {
        var style = new PdfStyleKey(12, "serif", "black");
        var fragmented = Line("E u rostat-OECD PPP Program", "Eurostat-OECD PPP Program");
        var ordinary = Line("D A Y 1", "D A Y 1");

        Assert.True(PdfLayoutEvidenceOutline.HasKerningFragmentationForAudit(Block("fragmented", fragmented, style)));
        Assert.False(PdfLayoutEvidenceOutline.HasKerningFragmentationForAudit(Block("ordinary", ordinary, style)));
    }

    [Fact]
    public void KerningRepairCandidateRequiresMeasuredGeometryEvidence()
    {
        var style = new PdfStyleKey(12, "serif", "black");
        var fragmented = Block("fragmented", Line("E u rostat-OECD PPP Program", "Eurostat-OECD PPP Program"), style);
        var ordinary = Block("ordinary", Line("E u rostat-OECD PPP Program", "E u rostat-OECD PPP Program"), style);

        Assert.True(PdfLayoutEvidenceOutline.LooksLikeKerningRepairCandidate(fragmented));
        Assert.False(PdfLayoutEvidenceOutline.LooksLikeKerningRepairCandidate(ordinary));
    }

    [Fact]
    public void IntentionalLetterSpacingDoesNotBecomeKerningRepairCandidate()
    {
        var style = new PdfStyleKey(12, "serif", "black");
        var intentionallySpaced = Block("spaced", Line("N e t I n c o m e", "N e t I n c o m e"), style);

        Assert.False(PdfLayoutEvidenceOutline.HasKerningFragmentationForAudit(intentionallySpaced));
        Assert.False(PdfLayoutEvidenceOutline.LooksLikeKerningRepairCandidate(intentionallySpaced));
    }

    private static PdfSemanticBlock Block(string id, PdfLine line, PdfStyleKey style) =>
        new(id, [line], style, line.Page, line.Y, line.Y, line.Left, line.Right, line.Text);

    private static PdfLine Line(string observed, string match) => new(1, 700, 12, observed, 0, "", 0,
        72, 300, "serif", "black", PdfTextUtilities.CanonicalForMatch(match), match);
}
