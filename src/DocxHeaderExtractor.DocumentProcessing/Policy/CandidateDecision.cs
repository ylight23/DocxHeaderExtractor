using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Policy;

/// <summary>Candidate-stage decision only; no proposal, validation, or hierarchy state.</summary>
public sealed record CandidateDecision(
    bool IsCandidate,
    double Score,
    ParagraphRole Role,
    int? GuessedLevel);
