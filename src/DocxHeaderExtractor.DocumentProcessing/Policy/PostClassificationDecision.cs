using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Policy;

/// <summary>Post-classification policy result; no source mutation or downstream state.</summary>
public sealed record PostClassificationDecision(
    ParagraphRole Role,
    double Score,
    int? GuessedLevel);
