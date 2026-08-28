using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Eval;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Core.Repair;

public sealed record RepairGateCalibrationReport(
    string FormatVersion,
    DateTimeOffset CreatedAt,
    int Documents,
    string CalibrationStatus,
    IReadOnlyList<string> Findings,
    RepairGateCalibrationSplitReport? Split,
    RepairFixedRuleReplayReport? FixedRuleReplay,
    string PerFileRankingStatus,
    string ScoreStopCondition,
    string GateBranchStatus,
    double ScoreNavigationPearson,
    double ScoreF1Pearson,
    double GatePassRate,
    double GatePassedAverageNavigation,
    double GateFailedAverageNavigation,
    IReadOnlyList<RepairGateCalibrationRow> Rows);

public sealed record RepairGateCalibrationRow(
    string File,
    string? DocumentMode,
    string? CurrentRoute,
    string? BestRoute,
    string? BaselineRoute,
    bool BaselineMatchedCurrent,
    double BestScore,
    string ScoreCalibrationStatus,
    string? RouteValidationStatus,
    bool BestAccepted,
    bool GatePassed,
    int TruthCount,
    int ResultCount,
    double Precision,
    double Recall,
    double F1,
    double NavigationRecall,
    double NavigationLevelAccuracy,
    int FalsePositiveCount,
    int FalseNegativeCount,
    int WrongLevelCount);

public sealed record RepairFixedRuleReplayReport(
    string Status,
    string Reason,
    int Documents,
    double AverageNavigation,
    double GatePassedAverageNavigation,
    double GateFailedAverageNavigation,
    int LowNavigationGatePasses,
    IReadOnlyList<string> Findings);

public sealed record RepairGateCalibrationSplitReport(
    RepairGateCalibrationSubset Tune,
    RepairGateCalibrationSubset Holdout);

public sealed record RepairGateCalibrationSubset(
    string Name,
    int Documents,
    double ScoreNavigationPearson,
    double ScoreF1Pearson,
    double GatePassRate,
    double GatePassedAverageNavigation,
    double GateFailedAverageNavigation,
    IReadOnlyDictionary<string, int> RouteDistribution,
    IReadOnlyDictionary<string, int> ModeDistribution,
    IReadOnlyList<string> Findings);

public static class RepairGateCalibration
{
    public const string FormatVersion = "dhx-repair-gate-calibration/v1";

    public static async Task<RepairGateCalibrationReport> RunAsync(
        IReadOnlyList<string> files,
        PipelineOptions options,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? keyIndex = null,
        CancellationToken ct = default)
    {
        var rows = new List<RepairGateCalibrationRow>();
        foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var keyPath = ResolveKeyPath(file, keyIndex);
            if (keyPath is null) continue;

            var conversion = LegacyDocConverter.EnsureDocx(file);
            SlimDocument slim;
            AnswerKey key;
            HashSet<int> candidateIndexes;
            try
            {
                slim = new DocxSlimExtractor(options.Extraction).Extract(conversion.Path);
                key = AnswerKey.Load(keyPath).ResolveStableIds(
                    slim.Paragraphs.ToDictionary(p => p.StableId, p => p.Index));
                candidateIndexes = options.DisableLlm || !options.ReviewAllParagraphs
                    ? [.. slim.Candidates.Select(p => p.Index)]
                    : [.. slim.Paragraphs
                        .Where(p => p.Role != Models.ParagraphRole.Empty)
                        .Select(p => p.Index)];
            }
            finally
            {
                LegacyDocConverter.Cleanup(conversion);
            }

            using var repairRunner = new AuthorityRepairOutlineRunner(options);
            var outline = await repairRunner.RunAsync(file, ct);
            var candidateReport = RepairCandidateRunner.Analyze(outline);
            var validation = RepairValidationGate.Validate(outline, candidateReport);
            var score = Evaluator.Score(Path.GetFileNameWithoutExtension(file), outline, candidateIndexes, key);
            var best = candidateReport.Candidates.FirstOrDefault(c => c.Route == candidateReport.BestRoute);
            var baselineRoute = BaselineRouteTree(outline, candidateReport);

            rows.Add(new RepairGateCalibrationRow(
                Path.GetFileName(file),
                outline.DocumentMode?.Mode.ToString(),
                outline.DeterministicRoute,
                candidateReport.BestRoute,
                baselineRoute,
                string.Equals(baselineRoute, outline.DeterministicRoute, StringComparison.Ordinal),
                best?.Score ?? 0,
                candidateReport.ScoreCalibrationStatus,
                best?.RouteValidationStatus,
                best?.Accepted ?? false,
                validation.Passed,
                score.TruthCount,
                score.ResultCount,
                score.Precision,
                score.Recall,
                score.F1,
                score.NavigationRecall,
                score.NavigationLevelAccuracy,
                score.FalsePositives.Count,
                score.FalseNegatives.Count,
                score.WrongLevels.Count));
        }

        var summary = Summarize("all", rows);
        var split = BuildSplit(rows);
        var fixedRuleReplay = BuildFixedRuleReplay(rows);
        var findings = Findings(rows, summary.ScoreNavigationPearson, summary.ScoreF1Pearson, summary.GatePassRate,
            summary.GatePassedAverageNavigation, summary.GateFailedAverageNavigation);
        var status = findings.Count == 0 ? "trusted_for_analysis" : "untrusted_until_recalibrated";

        return new RepairGateCalibrationReport(
            FormatVersion,
            DateTimeOffset.UtcNow,
            rows.Count,
            status,
            findings,
            split,
            fixedRuleReplay,
            "not_measured_candidate_outlines_unavailable",
            "if_per_file_ranking_accuracy_below_0.60_or_not_better_than_mode_route_baseline_then_do_not_tune_score",
            "stopped_after_six_gate_rounds_score_untrusted_gate_flags_only",
            summary.ScoreNavigationPearson,
            summary.ScoreF1Pearson,
            summary.GatePassRate,
            summary.GatePassedAverageNavigation,
            summary.GateFailedAverageNavigation,
            rows);
    }

    private static string? ResolveKeyPath(
        string file,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? keyIndex)
    {
        var sidecar = Path.ChangeExtension(file, ".key");
        if (File.Exists(sidecar)) return sidecar;

        var stem = Path.GetFileNameWithoutExtension(file);
        if (keyIndex is not null &&
            keyIndex.TryGetValue(stem, out var keys) &&
            keys.Count > 0)
            return keys
                .OrderBy(k => IsReviewedKeyPath(k) ? 0 : 1)
                .ThenBy(k => k, StringComparer.OrdinalIgnoreCase)
                .First();

        return null;
    }

    private static bool IsReviewedKeyPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/keys/", StringComparison.OrdinalIgnoreCase);
    }

    public static string ToCsv(RepairGateCalibrationReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("file,documentMode,currentRoute,bestRoute,baselineRoute,baselineMatchedCurrent,bestScore,scoreCalibrationStatus,routeValidationStatus,bestAccepted,gatePassed,truth,result,precision,recall,f1,nav,navLevel,falsePositives,falseNegatives,wrongLevels");
        foreach (var r in report.Rows)
        {
            sb.Append(Escape(r.File)).Append(',')
              .Append(Escape(r.DocumentMode)).Append(',')
              .Append(Escape(r.CurrentRoute)).Append(',')
              .Append(Escape(r.BestRoute)).Append(',')
              .Append(Escape(r.BaselineRoute)).Append(',')
              .Append(r.BaselineMatchedCurrent).Append(',')
              .Append(r.BestScore.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(Escape(r.ScoreCalibrationStatus)).Append(',')
              .Append(Escape(r.RouteValidationStatus)).Append(',')
              .Append(r.BestAccepted).Append(',')
              .Append(r.GatePassed).Append(',')
              .Append(r.TruthCount).Append(',')
              .Append(r.ResultCount).Append(',')
              .Append(Pct(r.Precision)).Append(',')
              .Append(Pct(r.Recall)).Append(',')
              .Append(Pct(r.F1)).Append(',')
              .Append(Pct(r.NavigationRecall)).Append(',')
              .Append(Pct(r.NavigationLevelAccuracy)).Append(',')
              .Append(r.FalsePositiveCount).Append(',')
              .Append(r.FalseNegativeCount).Append(',')
              .Append(r.WrongLevelCount)
              .AppendLine();
        }
        return sb.ToString();
    }

    public static string ToJson(RepairGateCalibrationReport report) =>
        JsonSerializer.Serialize(report, JsonOptions);

    private static string Escape(string? value)
    {
        value ??= "";
        return value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    private static string Pct(double value) =>
        value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

    private static double Pearson(IReadOnlyList<double> xs, IReadOnlyList<double> ys)
    {
        if (xs.Count != ys.Count || xs.Count < 2) return 0;
        var avgX = xs.Average();
        var avgY = ys.Average();
        var numerator = 0.0;
        var dx2 = 0.0;
        var dy2 = 0.0;
        for (var i = 0; i < xs.Count; i++)
        {
            var dx = xs[i] - avgX;
            var dy = ys[i] - avgY;
            numerator += dx * dy;
            dx2 += dx * dx;
            dy2 += dy * dy;
        }
        var denom = Math.Sqrt(dx2 * dy2);
        return denom == 0 ? 0 : numerator / denom;
    }

    private static RepairGateCalibrationSplitReport? BuildSplit(IReadOnlyList<RepairGateCalibrationRow> rows)
    {
        if (rows.Count < 6) return null;
        var tuneNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "072_ICP_TAG_Minutes_Mar_2025.docx",
            "080_ICP_Governing_Board_Minutes_Feb_2023.docx",
            "030_WB_RFP_Consulting_Services_2019.docx",
        };

        var ordered = rows.OrderBy(r => r.File, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var row in ordered)
        {
            if (tuneNames.Count >= Math.Min(13, Math.Max(3, rows.Count / 2))) break;
            tuneNames.Add(row.File);
        }

        var tune = ordered.Where(r => tuneNames.Contains(r.File)).ToList();
        var holdout = ordered.Where(r => !tuneNames.Contains(r.File)).ToList();
        return new RepairGateCalibrationSplitReport(
            Summarize("tune", tune),
            Summarize("holdout", holdout));
    }

    private static RepairFixedRuleReplayReport? BuildFixedRuleReplay(IReadOnlyList<RepairGateCalibrationRow> rows)
    {
        if (rows.Count == 0) return null;
        var passed = rows.Where(r => r.GatePassed).ToList();
        var failed = rows.Where(r => !r.GatePassed).ToList();
        var lowPasses = rows
            .Where(r => r.GatePassed && r.NavigationRecall < 0.80)
            .OrderBy(r => r.NavigationRecall)
            .Select(r => $"{r.File}:{r.NavigationRecall:0.###}")
            .ToList();
        var findings = new List<string>();
        if (lowPasses.Count > 0)
            findings.AddRange(lowPasses.Select(x => $"fixed_rule_replay_gate_pass_low_nav:{x}"));
        if (passed.Count > 0 && failed.Count > 0 &&
            passed.Average(r => r.NavigationRecall) <= failed.Average(r => r.NavigationRecall))
            findings.Add("fixed_rule_replay_gate_pass_not_better_than_fail");

        return new RepairFixedRuleReplayReport(
            "not_applicable_rules_are_hand_written",
            "This is a replay of fixed gates, not leave-one-out learning; no thresholds are inferred from N-1 documents.",
            rows.Count,
            rows.Average(r => r.NavigationRecall),
            passed.Count == 0 ? 0 : passed.Average(r => r.NavigationRecall),
            failed.Count == 0 ? 0 : failed.Average(r => r.NavigationRecall),
            lowPasses.Count,
            findings);
    }

    private static RepairGateCalibrationSubset Summarize(string name, IReadOnlyList<RepairGateCalibrationRow> rows)
    {
        var passed = rows.Where(r => r.GatePassed).ToList();
        var failed = rows.Where(r => !r.GatePassed).ToList();
        var scoreNav = Pearson(rows.Select(r => r.BestScore).ToList(), rows.Select(r => r.NavigationRecall).ToList());
        var scoreF1 = Pearson(rows.Select(r => r.BestScore).ToList(), rows.Select(r => r.F1).ToList());
        var gatePassRate = rows.Count == 0 ? 0 : (double)passed.Count / rows.Count;
        var passNav = passed.Count == 0 ? 0 : passed.Average(r => r.NavigationRecall);
        var failNav = failed.Count == 0 ? 0 : failed.Average(r => r.NavigationRecall);
        return new RepairGateCalibrationSubset(
            name,
            rows.Count,
            scoreNav,
            scoreF1,
            gatePassRate,
            passNav,
            failNav,
            rows.GroupBy(r => r.CurrentRoute ?? "(none)")
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            rows.GroupBy(r => r.DocumentMode ?? "(none)")
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            Findings(rows, scoreNav, scoreF1, gatePassRate, passNav, failNav));
    }

    private static string? BaselineRouteTree(DocumentOutline outline, RepairCandidateReport candidates)
    {
        var current = outline.DeterministicRoute;
        bool CandidateAccepted(string route) =>
            candidates.Candidates.Any(c => string.Equals(c.Route, route, StringComparison.Ordinal) && c.Accepted);
        bool CandidateStrong(string route) =>
            candidates.Candidates.Any(c =>
                string.Equals(c.Route, route, StringComparison.Ordinal) &&
                (c.RouteValidationStatus == "route_metrics_strong" || c.Accepted));

        return outline.DocumentMode?.Mode switch
        {
            DocumentMode.VietnameseLegal => "auto:vietnamese-legal",
            DocumentMode.OutlineLevelDriven => "auto:outline-level",
            DocumentMode.TypedNumbering when current == "auto:pdf-textbook-layout" => "auto:pdf-textbook-layout",
            DocumentMode.TypedNumbering when CandidateStrong("auto:rfc-toc-dictionary") => "auto:rfc-toc-dictionary",
            DocumentMode.TypedNumbering when CandidateAccepted("auto:part-section-text-toc") => "auto:part-section-text-toc",
            DocumentMode.TypedNumbering => "auto:typed-numbering",
            DocumentMode.FormatDriven when current == "auto:pdf-bold-label" || CandidateAccepted("auto:pdf-bold-label") => "auto:pdf-bold-label",
            DocumentMode.FormatDriven when current == "auto:vietnamese-administrative" => "auto:vietnamese-administrative",
            _ => current,
        };
    }

    private static IReadOnlyList<string> Findings(
        IReadOnlyList<RepairGateCalibrationRow> rows,
        double scoreNav,
        double scoreF1,
        double gatePassRate,
        double passNav,
        double failNav)
    {
        var findings = new List<string>();
        if (rows.Count < 5)
            findings.Add($"too_few_documents:{rows.Count}");
        if (Math.Abs(scoreNav) < 0.35)
            findings.Add($"score_navigation_correlation_weak:{scoreNav:0.###}");
        if (Math.Abs(scoreF1) < 0.35)
            findings.Add($"score_f1_correlation_weak:{scoreF1:0.###}");
        if (gatePassRate is <= 0.05 or >= 0.95)
            findings.Add($"gate_not_discriminating:{gatePassRate:0.###}");
        if (rows.Any(r => r.GatePassed) && rows.Any(r => !r.GatePassed) && passNav <= failNav)
            findings.Add($"gate_pass_not_better_than_fail:{passNav:0.###}<={failNav:0.###}");
        foreach (var row in rows.Where(r => r.GatePassed && r.NavigationRecall < 0.80).OrderBy(r => r.NavigationRecall).Take(5))
            findings.Add($"gate_pass_low_nav:{row.File}:{row.NavigationRecall:0.###}");
        return findings;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };
}
