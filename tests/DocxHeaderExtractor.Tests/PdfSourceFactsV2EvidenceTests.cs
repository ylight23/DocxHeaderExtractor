using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Tests.GenericAudit;
using V12 = DocxHeaderExtractor.Tests.GenericAudit.V1_2;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The guard on <see cref="PdfSourceFactsV2Evidence"/>: read under PDF_SOURCE_FACTS_V1 it must be V1.2's own
/// evidence layer, proposal for proposal, on every PDF V1.2 was developed or held out on. Only then does a run
/// under V2 measure the facts and nothing else.
/// </summary>
public sealed class PdfSourceFactsV2EvidenceTests
{
    public static TheoryData<string> Pdfs => new()
    {
        Src029SourceReviewTests.Pdf,
        Src041BlindGeneralizationTests.Pdf,
        Src042BlindGeneralizationTests.Pdf,
        Src044BlindGeneralizationTests.Pdf,
        Src053BlindGeneralizationTests.Pdf,
    };

    [Theory]
    [MemberData(nameof(Pdfs))]
    public void Under_v1_facts_it_is_v1_2_exactly(string pdf)
    {
        var path = TestRepository.Path(pdf);
        var frozen = V12.SemanticAuditEngine.Propose(V12.SourceEvidenceProfile.FromPdf(path));
        var replayed = V12.SemanticAuditEngine.Propose(PdfSourceFactsV2Evidence.FromPdf(path, PdfSourceFactsVersion.V1_NominalFontSize));
        Assert.Equal(JsonSerializer.Serialize(frozen), JsonSerializer.Serialize(replayed));
    }
}
