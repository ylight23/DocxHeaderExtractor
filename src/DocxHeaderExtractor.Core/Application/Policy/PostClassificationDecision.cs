using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Application.Policy;

/// <summary>Post-classification policy result; no source mutation or downstream state.</summary>
public sealed record PostClassificationDecision(
    ParagraphRole Role,
    double Score,
    int? GuessedLevel);
