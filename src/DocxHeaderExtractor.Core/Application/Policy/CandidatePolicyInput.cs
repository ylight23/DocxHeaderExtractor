using DocxHeaderExtractor.Core.Application.Features;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Core.Application.Policy;

public sealed record CandidatePolicyInput(
    IPolicyParagraph Paragraph,
    DerivedDocumentFeatures DocumentFeatures,
    ExtractionOptions Options,
    bool TrustStyleSelection = true);
