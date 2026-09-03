using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Repair;

public sealed record RepairCandidateReport(
    string FormatVersion,
    string File,
    string? CurrentRoute,
    string? BestRoute,
    string ScoreCalibrationStatus,
    bool HasAcceptedCandidate,
    bool PatchCandidateNeeded,
    IReadOnlyList<RepairCandidateScore> Candidates);

public sealed record RepairCandidateScore(
    int Rank,
    string Route,
    bool Accepted,
    string Reason,
    int HeadingCount,
    double Score,
    double DuplicateRate,
    double TitlePollutionRate,
    double LevelJumpRate,
    double? BodyAnchorRatio,
    double? TocCoverage,
    string RouteValidationStatus,
    string ScoreCalibrationStatus,
    IReadOnlyDictionary<string, double> RouteMetrics,
    bool IsCurrentRoute);

public static class RepairCandidateRunner
{
    public const string FormatVersion = "dhx-repair-candidates/v1";

    public static RepairCandidateReport Analyze(DocumentOutline outline)
    {
        var candidates = (outline.Diagnostics?.Candidates ?? []).ToList();
        if (!string.IsNullOrWhiteSpace(outline.DeterministicRoute) &&
            candidates.All(c => !string.Equals(c.Route, outline.DeterministicRoute, StringComparison.Ordinal)))
        {
            candidates.Add(CurrentOutputCandidate(outline));
        }

        var ranked = candidates
            .Select(c => Score(c, outline.DeterministicRoute))
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.Accepted)
            .ThenByDescending(c => c.BodyAnchorRatio ?? 0)
            .ThenByDescending(c => c.TocCoverage ?? 0)
            .ThenByDescending(c => c.HeadingCount)
            .Select((c, i) => c with { Rank = i + 1 })
            .ToList();

        var best = ranked.FirstOrDefault();
        var hasAccepted = ranked.Any(c => c.Accepted);
        var needsPatch = outline.Diagnostics?.Status != "normal" ||
                         !hasAccepted ||
                         outline.Headings.Any(h =>
                             h.DecisionStatus == HeadingDecisionStatus.RequiresReview || h.Disputed);

        return new RepairCandidateReport(
            FormatVersion,
            outline.File,
            outline.DeterministicRoute,
            best?.Route,
            "untrusted_cross_route_score",
            hasAccepted,
            needsPatch,
            ranked);
    }

    private static OutlineCandidateDiagnostic CurrentOutputCandidate(DocumentOutline outline)
    {
        var duplicateRate = DuplicateRate(outline.Headings);
        var pollutionRate = TitlePollutionRate(outline.Headings);
        var jumpRate = LevelJumpRate(outline.Headings);
        var reviewRate = outline.Headings.Count == 0
            ? 1
            : (double)outline.Headings.Count(h =>
                h.DecisionStatus == HeadingDecisionStatus.RequiresReview || h.Disputed) / outline.Headings.Count;
        var accepted = outline.Headings.Count > 0 &&
                       duplicateRate <= 0.02 &&
                       pollutionRate <= 0.05 &&
                       jumpRate <= 0.25 &&
                       reviewRate <= 0.10;
        var reason = accepted
            ? "current_output_internal_validation"
            : $"current_output_weak dup={duplicateRate:P1} pollution={pollutionRate:P1} jump={jumpRate:P1} review={reviewRate:P1}";
        return new OutlineCandidateDiagnostic(
            outline.DeterministicRoute!,
            accepted,
            reason,
            outline.Headings.Count,
            duplicateRate,
            pollutionRate,
            jumpRate);
    }

    private static RepairCandidateScore Score(OutlineCandidateDiagnostic candidate, string? currentRoute)
    {
        var score = 0.0;
        if (candidate.Accepted) score += 100;
        if (candidate.HeadingCount > 0) score += Math.Min(25, Math.Log(candidate.HeadingCount + 1, 2) * 4);
        if (candidate.BodyAnchorRatio is { } body) score += body * 35;
        if (candidate.TocCoverage is { } toc) score += toc * 30;
        score -= candidate.DuplicateRate * 40;
        score -= candidate.TitlePollutionRate * 60;
        score -= candidate.LevelJumpRate * 30;
        if (string.Equals(candidate.Route, currentRoute, StringComparison.Ordinal)) score += 5;

        return new RepairCandidateScore(
            0,
            candidate.Route,
            candidate.Accepted,
            candidate.Reason,
            candidate.HeadingCount,
            Math.Round(score, 3),
            Math.Round(candidate.DuplicateRate, 4),
            Math.Round(candidate.TitlePollutionRate, 4),
            Math.Round(candidate.LevelJumpRate, 4),
            candidate.BodyAnchorRatio is null ? null : Math.Round(candidate.BodyAnchorRatio.Value, 4),
            candidate.TocCoverage is null ? null : Math.Round(candidate.TocCoverage.Value, 4),
            RouteValidationStatus(candidate),
            "untrusted_until_route_calibrated",
            RouteMetrics(candidate),
            string.Equals(candidate.Route, currentRoute, StringComparison.Ordinal));
    }

    private static string RouteValidationStatus(OutlineCandidateDiagnostic candidate)
    {
        if (candidate.Route.Contains("toc-dictionary", StringComparison.OrdinalIgnoreCase))
        {
            return candidate.TocCoverage >= 0.90 && candidate.BodyAnchorRatio >= 0.90
                ? "route_metrics_strong"
                : "route_metrics_weak";
        }

        return candidate.Accepted ? "generic_internal_pass" : "generic_internal_fail";
    }

    private static IReadOnlyDictionary<string, double> RouteMetrics(OutlineCandidateDiagnostic candidate)
    {
        var metrics = new Dictionary<string, double>
        {
            ["duplicateRate"] = Math.Round(candidate.DuplicateRate, 4),
            ["titlePollutionRate"] = Math.Round(candidate.TitlePollutionRate, 4),
            ["levelJumpRate"] = Math.Round(candidate.LevelJumpRate, 4),
        };
        if (candidate.BodyAnchorRatio is { } body)
            metrics["bodyAnchorRatio"] = Math.Round(body, 4);
        if (candidate.TocCoverage is { } toc)
            metrics["tocCoverage"] = Math.Round(toc, 4);
        return metrics;
    }

    private static double DuplicateRate(IReadOnlyList<HeadingRecord> headings)
    {
        if (headings.Count == 0) return 0;
        var unique = headings.Select(h => (h.Index, Text: (h.Text ?? "").Trim())).Distinct().Count();
        return (double)(headings.Count - unique) / headings.Count;
    }

    private static double TitlePollutionRate(IReadOnlyList<HeadingRecord> headings)
    {
        if (headings.Count == 0) return 0;
        var polluted = headings.Count(h =>
        {
            var text = h.Text?.Trim() ?? "";
            var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return text.Length > 180 ||
                   text.Count(c => c is '.' or ';') >= 4 ||
                   words.Length >= 24 ||
                   (words.Length >= 14 && text.EndsWith('.') && !LooksLikeNumberedLabel(text));
        });
        return (double)polluted / headings.Count;
    }

    private static double LevelJumpRate(IReadOnlyList<HeadingRecord> headings)
    {
        if (headings.Count <= 1) return 0;
        var ordered = headings.OrderBy(h => h.Index).ToList();
        var jumps = 0;
        for (var i = 1; i < ordered.Count; i++)
            if (ordered[i].Level - ordered[i - 1].Level > 1)
                jumps++;
        return (double)jumps / (ordered.Count - 1);
    }

    private static bool LooksLikeNumberedLabel(string text) =>
        text.Length <= 90 &&
        (char.IsDigit(text[0]) ||
         text.StartsWith("Appendix ", StringComparison.OrdinalIgnoreCase) ||
         text.StartsWith("Annex ", StringComparison.OrdinalIgnoreCase) ||
         text.StartsWith("Chapter ", StringComparison.OrdinalIgnoreCase) ||
         text.StartsWith("Part ", StringComparison.OrdinalIgnoreCase));
}

public sealed record RepairValidationReport(
    string FormatVersion,
    string File,
    string? Route,
    bool Passed,
    string Status,
    IReadOnlyList<RepairValidationGateResult> Gates);

public sealed record RepairValidationGateResult(
    string Name,
    bool Passed,
    string Severity,
    string Detail);

public static class RepairValidationGate
{
    public const string FormatVersion = "dhx-repair-validation/v1";
    private static readonly Regex DateLikeRx = new(
        @"\b(?:jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:tember)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?|\d{1,2})[\w\s,\-\/]{0,20}\b\d{4}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SectionNumberRx = new(
        @"^\s*Section\s+(?<n>\d{1,3})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static RepairValidationReport Validate(DocumentOutline outline, RepairCandidateReport candidates)
    {
        var gates = new List<RepairValidationGateResult>();
        var best = candidates.Candidates.FirstOrDefault(c => c.Route == candidates.BestRoute);

        Add(gates, "candidate_exists", best is not null, "blocker",
            best is null ? "No deterministic candidate was available." : $"Best candidate is {best.Route}.");

        Add(gates, "candidate_accepted", best?.Accepted == true, "blocker",
            best is null ? "No candidate to accept." : $"{best.Route}: {best.Reason}.");

        Add(gates, "current_route_present", !string.IsNullOrWhiteSpace(outline.DeterministicRoute), "warning",
            outline.DeterministicRoute is null ? "Current output has no deterministic route." : $"Current route is {outline.DeterministicRoute}.");

        Add(gates, "outline_not_empty", outline.Headings.Count > 0, "blocker",
            $"Current outline has {outline.Headings.Count} headings.");

        var duplicateRate = DuplicateRate(outline.Headings);
        Add(gates, "duplicate_rate", duplicateRate <= 0.02, "blocker",
            $"Duplicate heading rate is {duplicateRate:P1}.");

        var pollutionRate = TitlePollutionRate(outline.Headings);
        var stronglyAnchoredDictionary =
            best?.Route.Contains("toc-dictionary", StringComparison.OrdinalIgnoreCase) == true &&
            best.TocCoverage >= 0.90 &&
            best.BodyAnchorRatio >= 0.90;
        var pollutionLimit = stronglyAnchoredDictionary ? 0.35 : 0.05;
        Add(gates, "title_pollution", pollutionRate <= pollutionLimit, "blocker",
            $"Title pollution rate is {pollutionRate:P1}; limit is {pollutionLimit:P0} for this route/evidence.");

        var jumpRate = LevelJumpRate(outline.Headings);
        Add(gates, "level_consistency", jumpRate <= 0.25, "warning",
            $"Level jump rate is {jumpRate:P1}.");

        var reviewCount = outline.Headings.Count(h =>
            h.DecisionStatus == HeadingDecisionStatus.RequiresReview || h.Disputed);
        Add(gates, "no_review_flags", reviewCount == 0, "warning",
            $"Current outline has {reviewCount} headings requiring review or disputed.");

        if (best?.Route.Contains("toc-dictionary", StringComparison.OrdinalIgnoreCase) == true)
        {
            Add(gates, "toc_coverage", best.TocCoverage >= 0.90, "blocker",
                best.TocCoverage is null ? "TOC coverage was not measured." : $"TOC coverage is {best.TocCoverage:P1}.");
            Add(gates, "body_anchor_ratio", best.BodyAnchorRatio >= 0.90, "blocker",
                best.BodyAnchorRatio is null ? "Body anchor ratio was not measured." : $"Body anchor ratio is {best.BodyAnchorRatio:P1}.");
        }

        if (string.Equals(best?.Route, "auto:pdf-bold-label", StringComparison.Ordinal))
        {
            var fragmentRate = PdfBoldFragmentRate(outline.Headings);
            Add(gates, "pdf_bold_fragment_rate", fragmentRate <= 0.15, "blocker",
                $"PDF bold-label fragment rate is {fragmentRate:P1}.");

            var coverArtifactRate = PdfBoldCoverArtifactRate(outline.Headings);
            Add(gates, "pdf_bold_cover_artifact_rate", coverArtifactRate <= 0.25, "blocker",
                $"PDF bold-label cover/date artifact rate is {coverArtifactRate:P1}.");
        }

        if (string.Equals(best?.Route, "auto:part-section-text-toc", StringComparison.Ordinal))
        {
            var decreases = PartSectionDecreases(outline.Headings);
            Add(gates, "part_section_number_order", decreases == 0, "blocker",
                $"Section numbering decreases {decreases} time(s).");
        }

        var passed = gates.All(g => g.Passed || g.Severity != "blocker");
        return new RepairValidationReport(
            FormatVersion,
            outline.File,
            outline.DeterministicRoute ?? candidates.BestRoute,
            passed,
            passed ? "passed" : "failed",
            gates);
    }

    private static void Add(ICollection<RepairValidationGateResult> gates, string name, bool passed, string severity, string detail) =>
        gates.Add(new RepairValidationGateResult(name, passed, severity, detail));

    private static double DuplicateRate(IReadOnlyList<HeadingRecord> headings)
    {
        if (headings.Count == 0) return 0;
        var unique = headings.Select(h => (h.Index, Text: (h.Text ?? "").Trim())).Distinct().Count();
        return (double)(headings.Count - unique) / headings.Count;
    }

    private static double TitlePollutionRate(IReadOnlyList<HeadingRecord> headings)
    {
        if (headings.Count == 0) return 0;
        var polluted = headings.Count(h =>
        {
            var text = h.Text?.Trim() ?? "";
            var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return text.Length > 180 ||
                   text.Count(c => c is '.' or ';') >= 4 ||
                   words.Length >= 24 ||
                   (words.Length >= 14 && text.EndsWith('.') && !LooksLikeNumberedLabel(text));
        });
        return (double)polluted / headings.Count;
    }

    private static bool LooksLikeNumberedLabel(string text) =>
        text.Length <= 90 &&
        (char.IsDigit(text[0]) ||
         text.StartsWith("Appendix ", StringComparison.OrdinalIgnoreCase) ||
         text.StartsWith("Annex ", StringComparison.OrdinalIgnoreCase) ||
         text.StartsWith("Chapter ", StringComparison.OrdinalIgnoreCase) ||
         text.StartsWith("Part ", StringComparison.OrdinalIgnoreCase));

    private static double LevelJumpRate(IReadOnlyList<HeadingRecord> headings)
    {
        if (headings.Count <= 1) return 0;
        var ordered = headings.OrderBy(h => h.Index).ToList();
        var jumps = 0;
        for (var i = 1; i < ordered.Count; i++)
            if (ordered[i].Level - ordered[i - 1].Level > 1)
                jumps++;
        return (double)jumps / (ordered.Count - 1);
    }

    private static double PdfBoldFragmentRate(IReadOnlyList<HeadingRecord> headings)
    {
        if (headings.Count == 0) return 0;
        var bad = headings.Count(h =>
        {
            var text = h.Text.Trim();
            var lastWord = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
            return HasUnbalancedParenthesis(text) ||
                   lastWord.Equals("and", StringComparison.OrdinalIgnoreCase) ||
                   lastWord.Equals("or", StringComparison.OrdinalIgnoreCase) ||
                   lastWord.Equals("the", StringComparison.OrdinalIgnoreCase) ||
                   lastWord.Equals("of", StringComparison.OrdinalIgnoreCase) ||
                   lastWord.Equals("for", StringComparison.OrdinalIgnoreCase) ||
                   lastWord.Equals("with", StringComparison.OrdinalIgnoreCase) ||
                   lastWord.Equals("on", StringComparison.OrdinalIgnoreCase) ||
                   lastWord.Equals("current", StringComparison.OrdinalIgnoreCase) ||
                   lastWord.Equals("any", StringComparison.OrdinalIgnoreCase);
        });
        return (double)bad / headings.Count;
    }

    private static double PdfBoldCoverArtifactRate(IReadOnlyList<HeadingRecord> headings)
    {
        if (headings.Count == 0) return 0;
        var bad = headings.Count(h =>
        {
            var text = h.Text.Trim();
            if (text.EndsWith(':')) return false;
            if (text.StartsWith("Session ", StringComparison.OrdinalIgnoreCase)) return false;
            if (text.StartsWith("Annex ", StringComparison.OrdinalIgnoreCase)) return false;
            if (DateLikeRx.IsMatch(text)) return true;
            var letters = text.Where(char.IsLetter).ToList();
            if (letters.Count >= 6 && letters.All(char.IsUpper)) return true;
            return text.Length <= 70 &&
                   (text.Contains("meeting", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("minutes", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("hybrid", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("room", StringComparison.OrdinalIgnoreCase));
        });
        return (double)bad / headings.Count;
    }

    private static bool HasUnbalancedParenthesis(string text) =>
        text.Count(c => c == '(') != text.Count(c => c == ')');

    private static int PartSectionDecreases(IReadOnlyList<HeadingRecord> headings)
    {
        int? previous = null;
        var decreases = 0;
        foreach (var heading in headings.OrderBy(h => h.Index).ThenBy(h => h.Level))
        {
            var match = SectionNumberRx.Match(heading.Text);
            if (!match.Success || !int.TryParse(match.Groups["n"].Value, out var current)) continue;
            if (previous is { } p && current < p) decreases++;
            previous = current;
        }
        return decreases;
    }
}
