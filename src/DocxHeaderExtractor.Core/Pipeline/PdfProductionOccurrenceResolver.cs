using System.Globalization;
using System.Text;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Source-fact-only occurrence resolver. It selects or defers between repeated candidate
/// occurrences; it never changes the ranker score and never emits an outline heading.
/// Gold keys, DOCX anchors and evaluator artifacts are intentionally absent from this API.
/// </summary>
public static class PdfProductionOccurrenceResolver
{
    public static PdfProductionOccurrenceReport Resolve(IReadOnlyList<RankedCandidate> candidates)
    {
        var byFamily = candidates
            // The first rendered line is a source fact. It keeps a heading together with the
            // wider semantic windows that begin with it, without trying to repair or rewrite it.
            .GroupBy(candidate => candidate.OccurrenceKey ?? FamilyKey(candidate.Text), StringComparer.Ordinal)
            .Where(group => group.Key.Length > 0)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(ResolveFamily)
            .ToArray();

        return new PdfProductionOccurrenceReport(candidates.Count,
            byFamily.Count(decision => decision.Resolution == PdfOccurrenceResolution.Unique),
            byFamily.Count(decision => decision.Resolution == PdfOccurrenceResolution.Preferred),
            byFamily.Count(decision => decision.Resolution == PdfOccurrenceResolution.Ambiguous),
            byFamily.Count(decision => decision.Resolution == PdfOccurrenceResolution.Rejected),
            byFamily);
    }

    public static string FamilyKey(string text) => new string((text ?? string.Empty)
        .Normalize(NormalizationForm.FormD)
        .Where(character => char.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
        .Where(char.IsLetterOrDigit)
        .Select(char.ToLowerInvariant)
        .ToArray());

    private static PdfOccurrenceFamilyDecision ResolveFamily(IGrouping<string, RankedCandidate> group)
    {
        var candidates = group.OrderBy(candidate => candidate.Page).ThenBy(candidate => candidate.SourceId, StringComparer.Ordinal).ToArray();
        var eligible = candidates.Where(IsEligible).ToArray();
        if (eligible.Length == 0)
        {
            return Build(group.Key, PdfOccurrenceResolution.Rejected, null,
                ["all_occurrences_scope_or_risk_rejected"], ["no_eligible_document_body_occurrence"], candidates);
        }

        if (eligible.Length == 1)
        {
            return Build(group.Key, PdfOccurrenceResolution.Unique, eligible[0].SourceId,
                ["single_eligible_source_occurrence", ScopeEvidence(eligible[0])], [], candidates);
        }

        var scored = eligible.Select(candidate => new Scored(candidate, Preference(candidate))).OrderByDescending(item => item.Score)
            .ThenBy(item => item.Candidate.Page).ThenBy(item => item.Candidate.SourceId, StringComparer.Ordinal).ToArray();
        var margin = scored[0].Score - scored[1].Score;
        if (margin >= 0.75)
        {
            return Build(group.Key, PdfOccurrenceResolution.Preferred, scored[0].Candidate.SourceId,
                ["source_fact_preference_margin", ScopeEvidence(scored[0].Candidate), $"margin:{margin:F2}"], [], candidates);
        }

        return Build(group.Key, PdfOccurrenceResolution.Ambiguous, null,
            ["multiple_eligible_source_occurrences"], ["source_facts_do_not_separate_occurrences", $"margin:{margin:F2}"], candidates);
    }

    private static PdfOccurrenceFamilyDecision Build(string key, PdfOccurrenceResolution resolution, string? preferred,
        IReadOnlyList<string> evidence, IReadOnlyList<string> ambiguities, IReadOnlyList<RankedCandidate> candidates) =>
        new(key, resolution, preferred, evidence, ambiguities, candidates.Select(candidate =>
            new PdfOccurrenceCandidateDecision(candidate.SourceId, candidate.Page, candidate.Scope, candidate.Text,
                candidate.CandidateScore, candidate.EscalationScore, IsEligible(candidate),
                IsEligible(candidate) && preferred == candidate.SourceId)).ToArray());

    private static bool IsEligible(RankedCandidate candidate) =>
        candidate.Scope == "document_body" &&
        !candidate.NegativeSignals.Contains("table_scope", StringComparer.Ordinal) &&
        !candidate.NegativeSignals.Contains("running_page_scope", StringComparer.Ordinal);

    private static double Preference(RankedCandidate candidate)
    {
        var score = 2.0;
        if (candidate.PositiveSignals.Contains("standalone", StringComparer.Ordinal)) score += 0.40;
        if (candidate.PositiveSignals.Contains("opens_content", StringComparer.Ordinal)) score += 0.25;
        if (candidate.PositiveSignals.Contains("labelled_numbering_marker", StringComparer.Ordinal)) score += 0.20;
        if (candidate.PositiveSignals.Contains("layout_prominence", StringComparer.Ordinal)) score += 0.15;
        if (candidate.NegativeSignals.Contains("header_footer_zone", StringComparer.Ordinal)) score -= 0.80;
        if (candidate.NegativeSignals.Contains("long_marker_body_window", StringComparer.Ordinal)) score -= 0.50;
        if (candidate.AmbiguitySignals.Contains("multi_line_boundary", StringComparer.Ordinal)) score -= 0.20;
        return score;
    }

    private static string ScopeEvidence(RankedCandidate candidate) => $"scope:{candidate.Scope}";
    private sealed record Scored(RankedCandidate Candidate, double Score);
}

public enum PdfOccurrenceResolution { Unique, Preferred, Ambiguous, Rejected }

public sealed record PdfProductionOccurrenceReport(int CandidateCount, int UniqueCount, int PreferredCount,
    int AmbiguousCount, int RejectedCount, IReadOnlyList<PdfOccurrenceFamilyDecision> Families)
{
    public PdfOccurrenceCandidateDecision? FindCandidate(string sourceId) => Families
        .SelectMany(decision => decision.Candidates)
        .FirstOrDefault(candidate => candidate.SourceId == sourceId);

    public PdfOccurrenceFamilyDecision? FindFamily(string sourceId) => Families
        .FirstOrDefault(family => family.Candidates.Any(candidate => candidate.SourceId == sourceId));
}

public sealed record PdfOccurrenceFamilyDecision(string FamilyKey, PdfOccurrenceResolution Resolution,
    string? PreferredCandidateId, IReadOnlyList<string> Evidence, IReadOnlyList<string> Ambiguities,
    IReadOnlyList<PdfOccurrenceCandidateDecision> Candidates);

public sealed record PdfOccurrenceCandidateDecision(string SourceId, int Page, string Scope, string Text,
    double CandidateScore, double EscalationScore, bool Eligible, bool Preferred);
