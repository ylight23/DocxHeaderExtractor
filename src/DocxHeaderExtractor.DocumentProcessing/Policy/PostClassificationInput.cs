using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Policy;

public sealed record PostClassificationInput(
    SourceParagraph Source,
    CandidateDecision Candidate,
    TocStructuralFeatures TocFeatures,
    string? NextNonEmptyText,
    ParagraphRole? PreviousNonEmptyRole);
