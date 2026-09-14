using System.Text.Json;
using System.Text.Json.Serialization;

namespace IdentityBenchmarkV4HB;

public static class AdjudicationConstants
{
    public static readonly IReadOnlySet<string> Relations = new HashSet<string>(StringComparer.Ordinal)
    {
        "SAME_SEMANTIC_REPEAT",
        "CONTINUATION_OF",
        "DISTINCT_SEMANTIC_NODE",
        "UNRESOLVED",
    };

    public static readonly IReadOnlySet<string> ConfidenceValues = new HashSet<string>(StringComparer.Ordinal)
    {
        "HIGH", "MEDIUM", "LOW",
    };
}

public sealed record FrozenReviewBinding(
    string ReviewId,
    string CandidateId,
    string LeftOccurrenceId,
    string RightOccurrenceId,
    string SourceBindingSha256);

public sealed record HumanReviewResponse(
    string ReviewId,
    string CandidateId,
    string LeftOccurrenceId,
    string RightOccurrenceId,
    string Relation,
    string Confidence,
    string EvidenceNote,
    bool NeedsMoreSourceInspection,
    string? ContinuationSourceOccurrenceId = null,
    string? ContinuedFromOccurrenceId = null,
    string? ReviewerNote = null,
    string? ReviewedAtUtc = null,
    int Revision = 1);

public sealed record ValidationResult(IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public static class AdjudicationValidator
{
    private static readonly string[] ForbiddenEvidenceTerms =
    [
        "model", "provider", "retrieval", "prediction", "score", "gold", "candidate reason",
    ];

    public static ValidationResult ValidateResponse(
        HumanReviewResponse response,
        IReadOnlyDictionary<string, FrozenReviewBinding> bindings)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(response.ReviewId)) errors.Add("REVIEW_ID_REQUIRED");
        if (!bindings.TryGetValue(response.ReviewId, out var binding))
        {
            errors.Add("UNKNOWN_REVIEW_ID");
            return new(errors);
        }

        if (!string.Equals(response.CandidateId, binding.CandidateId, StringComparison.Ordinal)) errors.Add("CANDIDATE_BINDING_MISMATCH");
        if (!string.Equals(response.LeftOccurrenceId, binding.LeftOccurrenceId, StringComparison.Ordinal)) errors.Add("LEFT_OCCURRENCE_BINDING_MISMATCH");
        if (!string.Equals(response.RightOccurrenceId, binding.RightOccurrenceId, StringComparison.Ordinal)) errors.Add("RIGHT_OCCURRENCE_BINDING_MISMATCH");
        if (!AdjudicationConstants.Relations.Contains(response.Relation)) errors.Add("INVALID_RELATION");
        if (!AdjudicationConstants.ConfidenceValues.Contains(response.Confidence)) errors.Add("INVALID_CONFIDENCE");
        if (string.IsNullOrWhiteSpace(response.EvidenceNote)) errors.Add("EVIDENCE_NOTE_REQUIRED");
        if (ContainsForbiddenEvidenceLanguage(response.EvidenceNote)) errors.Add("EVIDENCE_NOTE_CONTAINS_NON_SOURCE_LANGUAGE");
        if (response.Revision < 1) errors.Add("INVALID_REVISION");

        var isContinuation = string.Equals(response.Relation, "CONTINUATION_OF", StringComparison.Ordinal);
        if (isContinuation)
        {
            if (string.IsNullOrWhiteSpace(response.ContinuationSourceOccurrenceId) || string.IsNullOrWhiteSpace(response.ContinuedFromOccurrenceId))
            {
                errors.Add("CONTINUATION_DIRECTION_REQUIRED");
            }
            else
            {
                var endpoints = new HashSet<string>(StringComparer.Ordinal) { binding.LeftOccurrenceId, binding.RightOccurrenceId };
                if (!endpoints.Contains(response.ContinuationSourceOccurrenceId) || !endpoints.Contains(response.ContinuedFromOccurrenceId)) errors.Add("CONTINUATION_DIRECTION_OUTSIDE_PAIR");
                if (string.Equals(response.ContinuationSourceOccurrenceId, response.ContinuedFromOccurrenceId, StringComparison.Ordinal)) errors.Add("SELF_CONTINUATION");
                if (response.ContinuationSourceOccurrenceId is not null && response.ContinuationSourceOccurrenceId != binding.LeftOccurrenceId && response.ContinuationSourceOccurrenceId != binding.RightOccurrenceId) errors.Add("UNKNOWN_CONTINUATION_SOURCE");
                if (response.ContinuedFromOccurrenceId is not null && response.ContinuedFromOccurrenceId != binding.LeftOccurrenceId && response.ContinuedFromOccurrenceId != binding.RightOccurrenceId) errors.Add("UNKNOWN_CONTINUED_FROM");
            }
        }
        else if (response.ContinuationSourceOccurrenceId is not null || response.ContinuedFromOccurrenceId is not null)
        {
            errors.Add("NON_CONTINUATION_HAS_DIRECTION");
        }

        return new(errors);
    }

    public static ValidationResult ValidateSet(
        IReadOnlyList<HumanReviewResponse> responses,
        IReadOnlyDictionary<string, FrozenReviewBinding> bindings,
        bool requireComplete)
    {
        var errors = new List<string>();
        var duplicateIds = responses.GroupBy(x => x.ReviewId, StringComparer.Ordinal).Where(x => x.Count() > 1).Select(x => x.Key).OrderBy(x => x, StringComparer.Ordinal);
        errors.AddRange(duplicateIds.Select(x => $"DUPLICATE_REVIEW_ID:{x}"));
        foreach (var response in responses)
        {
            var result = ValidateResponse(response, bindings);
            errors.AddRange(result.Errors.Select(error => $"{response.ReviewId}:{error}"));
        }

        var responseIds = responses.Select(x => x.ReviewId).ToHashSet(StringComparer.Ordinal);
        if (requireComplete)
        {
            foreach (var missingId in bindings.Keys.Where(candidateId => !responseIds.Contains(candidateId)).OrderBy(candidateId => candidateId, StringComparer.Ordinal))
                errors.Add($"MISSING_REVIEW_ID:{missingId}");
            foreach (var unknownId in responseIds.Where(candidateId => !bindings.ContainsKey(candidateId)).OrderBy(candidateId => candidateId, StringComparer.Ordinal))
                errors.Add($"UNKNOWN_REVIEW_ID:{unknownId}");
        }
        return new(errors);
    }

    private static bool ContainsForbiddenEvidenceLanguage(string note) =>
        ForbiddenEvidenceTerms.Any(term => note.Contains(term, StringComparison.OrdinalIgnoreCase));
}

public static class AdjudicationJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
