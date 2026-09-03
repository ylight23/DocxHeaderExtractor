using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

namespace DocxHeaderExtractor.DocumentProcessing.Policy;

public sealed record CandidatePolicyInput(
    IPolicyParagraph Paragraph,
    DerivedDocumentFeatures DocumentFeatures,
    ExtractionOptions Options,
    bool TrustStyleSelection = true);
