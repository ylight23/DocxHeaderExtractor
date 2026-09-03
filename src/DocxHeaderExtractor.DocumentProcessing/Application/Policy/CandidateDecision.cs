using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Application.Policy;

/// <summary>Candidate-stage decision only; no proposal, validation, or hierarchy state.</summary>
public sealed record CandidateDecision(
    bool IsCandidate,
    double Score,
    ParagraphRole Role,
    int? GuessedLevel);
