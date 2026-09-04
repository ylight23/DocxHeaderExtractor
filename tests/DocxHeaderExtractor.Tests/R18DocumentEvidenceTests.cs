using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Tests;

public sealed class R18DocumentEvidenceTests
{
    [Fact]
    public void Neutral_view_keeps_mode_evidence_opt_in()
    {
        var lines = new[] { new XmlLine("BLOCK\ncontent:\n    Heading\nEND_BLOCK", 0, true) };
        var baseline = NeutralDocumentViewSerializer.WrapChunk(lines, 1, 1);
        var mode = new DocumentModeReport(DocumentMode.FormatDriven, 4, 1, .25, 0, 0, 0, true);
        var withEvidence = NeutralDocumentViewSerializer.WrapChunk(lines, 1, 1, mode);

        Assert.DoesNotContain("DOCUMENT_EVIDENCE", baseline);
        Assert.Contains("DOCUMENT_EVIDENCE", withEvidence);
        Assert.Contains("\"mode\":\"FormatDriven\"", withEvidence);
        Assert.Equal(HeaderPrompt.System, HeaderPrompt.SystemFor(baseline));
        Assert.Contains("not ground truth", HeaderPrompt.SystemFor(withEvidence), StringComparison.Ordinal);
    }

    [Fact]
    public void Actual_role_prompt_exposes_only_opt_in_document_mode_evidence()
    {
        var line = new PdfLine(1, 700, 14, "Heading", .8, "", 0, 72, 420, "serif", "0.00,0.20,0.40");
        var block = new PdfSemanticBlock("p1", [line], PdfStyleClusterProfile.StyleOf(line), 1, 700, 700, 72, 420, "Heading");
        var facts = new PdfSourceFacts("p1", "Heading", 1, 1, 72, 700, 420, 700, "document_body", []);
        var mode = new DocumentModeReport(DocumentMode.SemanticOnly, 1, 0, 0, 0, 0, 0, false);
        var baselineContext = new PdfCandidateContext(facts, [], [], [], "SemanticOnly", []);
        var evidenceContext = baselineContext with { DocumentModeEvidence = mode };

        var baseline = PdfBlockAnalyst.BuildUserPrompt([block],
            new Dictionary<string, PdfCandidateContext> { ["p1"] = baselineContext });
        var withEvidence = PdfBlockAnalyst.BuildUserPrompt([block],
            new Dictionary<string, PdfCandidateContext> { ["p1"] = evidenceContext },
            includeDocumentModeEvidence: true);

        Assert.DoesNotContain("document_evidence", baseline);
        Assert.Contains("document_evidence", withEvidence);
        Assert.Contains("outline_level_ratio", withEvidence);
        Assert.Contains("SemanticOnly", withEvidence);
    }
}
