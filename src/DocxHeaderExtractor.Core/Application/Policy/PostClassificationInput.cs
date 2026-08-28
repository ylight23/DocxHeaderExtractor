using DocxHeaderExtractor.Core.Application.Features;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Application.Policy;

public sealed record PostClassificationInput(
    SourceParagraph Source,
    CandidateDecision Candidate,
    TocStructuralFeatures TocFeatures,
    string? NextNonEmptyText,
    ParagraphRole? PreviousNonEmptyRole);
