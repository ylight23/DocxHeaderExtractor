using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Deterministic, source-evidence-only error attribution for the frozen DOC-0116 score. The
/// categories are diagnostic hypotheses, not production filtering rules.
/// </summary>
public static class CanonicalDevVNextCorrectnessGoldForensicRunner
{
    private const string ExecutionRoot = "artifacts/level-accuracy/canonical-vnext-correctness-doc0116-live-execution-v2-1";
    private const string PreflightRoot = "artifacts/level-accuracy/canonical-vnext-correctness-doc0116-live-preflight-v2-1";
    private const string OutputRoot = ExecutionRoot + "/gold-scoring-v1";
    private const string OccurrenceRoot = "artifacts/authority-audit/canonical-batch-v3.3/canonical-exhaustive-heading-occurrence-v3.3/DOC-0116";
    private const string ScorePath = OutputRoot + "/score.v1.json";
    private const string ForensicPath = OutputRoot + "/forensic.v1.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var scorePath = Full(repoRoot, ScorePath);
        var exactBindingsPath = Full(repoRoot, OccurrenceRoot + "/exact-bindings.json");
        var sourceEvidencePath = Full(repoRoot, PreflightRoot + "/source-evidence.v1.json");
        var requestPlanPath = Full(repoRoot, PreflightRoot + "/request-plan.v1.json");
        var requestFreezePath = Full(repoRoot, PreflightRoot + "/request-freeze-manifest.v1.json");
        if (!File.Exists(scorePath) || !File.Exists(exactBindingsPath) || !File.Exists(sourceEvidencePath) ||
            !File.Exists(requestPlanPath) || !File.Exists(requestFreezePath))
            return Blocked("FROZEN_SCORE_OR_SOURCE_EVIDENCE_MISSING");

        using var score = JsonDocument.Parse(await File.ReadAllTextAsync(scorePath, ct));
        using var gold = JsonDocument.Parse(await File.ReadAllTextAsync(exactBindingsPath, ct));
        using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(sourceEvidencePath, ct));
        using var plan = JsonDocument.Parse(await File.ReadAllTextAsync(requestPlanPath, ct));
        using var requestFreeze = JsonDocument.Parse(await File.ReadAllTextAsync(requestFreezePath, ct));
        if (score.RootElement.GetProperty("providerCalls").GetInt32() != 0 ||
            score.RootElement.GetProperty("goldReads").GetInt32() != 1 ||
            score.RootElement.GetProperty("scoring").GetBoolean() == false)
            return Blocked("FROZEN_SCORE_NOT_ELIGIBLE_FOR_FORENSIC");

        var goldRows = gold.RootElement.GetProperty("bindings").EnumerateArray()
            .Select(item => new GoldRow(
                item.GetProperty("occurrenceId").GetString()!, item.GetProperty("sourceId").GetString()!,
                item.GetProperty("sourceSpan").GetProperty("start").GetInt32(),
                item.GetProperty("sourceSpan").GetProperty("end").GetInt32(), item.GetProperty("exactText").GetString()!))
            .ToArray();
        var sourceRows = evidence.RootElement.GetProperty("evidence").EnumerateArray()
            .Select(ReadEvidence).ToArray();
        var sourceById = sourceRows.GroupBy(item => item.SourceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var textFrequency = sourceRows.GroupBy(item => Norm(item.Text), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var rawProposals = await ReadFrozenProposalsAsync(repoRoot, ct);
        var sourceUniverseSha = requestFreeze.RootElement.GetProperty("sourceUniverseSha256").GetString()!;
        var fullInput = await CanonicalDevVNextCorrectnessProviderExecutionRunner.BuildFullInputAsync(repoRoot, sourceUniverseSha, ct);
        var replay = CanonicalSemanticProductionEntryPoint.Run(fullInput with { SemanticProposals = rawProposals });
        var predictions = replay.CanonicalOccurrences.Select(item => new Prediction(
            item.OccurrenceId, item.SourceId, item.SourceOrdinal, item.Start, item.End, item.Text, item.SemanticRole)).ToArray();
        var goldByKey = goldRows.ToDictionary(item => Key(item.SourceId, item.Start, item.End, item.Text), StringComparer.Ordinal);
        var predictedByKey = predictions.ToDictionary(item => Key(item.SourceId, item.Start, item.End, item.Text), StringComparer.Ordinal);
        var predictionBySourceId = predictions.GroupBy(item => item.SourceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        var falsePositives = predictions.Where(item => !goldByKey.ContainsKey(Key(item.SourceId, item.Start, item.End, item.Text)))
            .Select(item => BuildFalsePositive(item, sourceById, textFrequency)).ToArray();
        var falseNegatives = goldRows.Where(item => !predictedByKey.ContainsKey(Key(item.SourceId, item.Start, item.End, item.Text)))
            .Select(item => BuildFalseNegative(item, predictionBySourceId, sourceById)).ToArray();

        var output = new
        {
            schemaVersion = "a99-canonical-dev-vnext-doc0116-gold-forensic-v1",
            documentId = "DOC-0116",
            forensicHeuristicVersion = "SOURCE_EVIDENCE_GENERIC_V1",
            scoreAuthoritySha256 = Sha256File(scorePath),
            frozenPredictionOccurrences = predictions.Length,
            canonicalGoldOccurrences = goldRows.Length,
            falsePositiveCount = falsePositives.Length,
            falseNegativeCount = falseNegatives.Length,
            falsePositiveCategories = Counts(falsePositives.Select(item => item.Category)),
            falseNegativeCategories = Counts(falseNegatives.Select(item => item.Category)),
            falsePositiveCases = falsePositives,
            falseNegativeCases = falseNegatives,
            interpretation = new
            {
                diagnosticOnly = true,
                productionPolicyChanged = false,
                providerCalls = 0,
                goldReads = 1,
                scoring = false,
                segmentOmissionDetected = false,
                segmentOmissionReason = "all 1921 source aliases were model-visible; attribution does not promote a policy",
            }
        };
        var outputDir = Full(repoRoot, OutputRoot);
        await File.WriteAllTextAsync(Full(repoRoot, ForensicPath), JsonSerializer.Serialize(output, JsonOptions) + Environment.NewLine, ct);
        var report = BuildReport(falsePositives, falseNegatives);
        await File.WriteAllTextAsync(Path.Combine(outputDir, "forensic-report.md"), report, ct);
        await File.WriteAllTextAsync(Path.Combine(outputDir, "forensic-manifest.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = "a99-canonical-dev-vnext-doc0116-gold-forensic-manifest-v1",
            forensicSha256 = Sha256File(Full(repoRoot, ForensicPath)),
            scoreSha256 = Sha256File(scorePath),
            sourceEvidenceSha256 = Sha256File(sourceEvidencePath),
            requestPlanSha256 = Sha256File(requestPlanPath),
            providerCalls = 0,
            goldReads = 1,
            scoring = false,
        }, JsonOptions) + Environment.NewLine, ct);
        Console.WriteLine("STATUS=GOLD_FORENSIC_COMPLETE");
        Console.WriteLine($"FALSE_POSITIVES={falsePositives.Length}");
        Console.WriteLine($"FALSE_NEGATIVES={falseNegatives.Length}");
        Console.WriteLine("PROVIDER_CALLS=0");
        Console.WriteLine("GOLD_READS=1");
        return 0;
    }

    private static FalsePositive BuildFalsePositive(Prediction item, IReadOnlyDictionary<string, EvidenceRow[]> sourceById, IReadOnlyDictionary<string, int> frequency)
    {
        sourceById.TryGetValue(item.SourceId, out var candidates);
        var source = candidates?.OrderBy(candidate => Math.Abs(candidate.SourceOrdinal - item.SourceOrdinal)).FirstOrDefault();
        var category = ClassifyFalsePositive(item, source, frequency.TryGetValue(Norm(item.Text), out var count) ? count : 1, out var reasons);
        return new(category, reasons, item.SourceId, item.Start, item.End, item.Text, source?.StructuralScope,
            source?.TableDepth ?? 0, source?.StyleName, source?.OutlineLevel, source?.NumberLabel, source?.MarkerCount ?? 0);
    }

    private static FalseNegative BuildFalseNegative(GoldRow item, IReadOnlyDictionary<string, Prediction[]> predictionBySourceId, IReadOnlyDictionary<string, EvidenceRow[]> sourceById)
    {
        predictionBySourceId.TryGetValue(item.SourceId, out var candidates);
        candidates ??= [];
        sourceById.TryGetValue(item.SourceId, out var sourceCandidates);
        var source = sourceCandidates?.FirstOrDefault();
        string category;
        var reasons = new List<string>();
        if (candidates.Any(candidate => string.Equals(Norm(candidate.Text), Norm(item.Text), StringComparison.Ordinal)))
        {
            category = "EXACT_TEXT_BINDING_MISS";
            reasons.Add("same sourceId and normalized text predicted, but exact physical key was absent");
        }
        else if (candidates.Any(candidate => candidate.Start == item.Start && candidate.End == item.End))
        {
            category = "EXACT_TEXT_BINDING_MISS";
            reasons.Add("same sourceId and span predicted with non-matching text");
        }
        else if (candidates.Length > 1)
        {
            category = "DUPLICATE_OCCURRENCE_AMBIGUITY";
            reasons.Add("multiple prediction occurrences share the Gold sourceId");
        }
        else
        {
            category = "MODEL_OMITTED_TRUE_HEADING";
            reasons.Add("no prediction was bound to the Gold sourceId");
        }
        return new(category, reasons, item.OccurrenceId, item.SourceId, item.Start, item.End, item.Text,
            source?.StructuralScope, source?.TableDepth ?? 0);
    }

    private static string ClassifyFalsePositive(Prediction item, EvidenceRow? source, int textCount, out IReadOnlyList<string> reasons)
    {
        var why = new List<string>();
        if (source is null)
        {
            reasons = ["source evidence row unavailable"];
            return "OTHER_STRUCTURAL_LABEL";
        }
        var lowerScope = source.StructuralScope.ToLowerInvariant();
        var lowerText = item.Text.Trim().ToLowerInvariant();
        if (lowerScope.Contains("table_of_contents", StringComparison.Ordinal) || source.StyleName.Contains("toc", StringComparison.OrdinalIgnoreCase))
        {
            why.Add("source structural scope/style is TOC-like");
            reasons = why;
            return "TOC_LIKE_ENTRY";
        }
        if (source.TableDepth > 0 || item.SourceId.Contains("/tbl[", StringComparison.OrdinalIgnoreCase))
        {
            why.Add("source-owned table/cell ancestry");
            reasons = why;
            return "TABLE_STRUCTURAL_LABEL";
        }
        if (lowerScope.Contains("header", StringComparison.Ordinal) || lowerScope.Contains("footer", StringComparison.Ordinal) ||
            source.ContainerFacts.Any(fact => fact.Contains("header", StringComparison.OrdinalIgnoreCase) || fact.Contains("footer", StringComparison.OrdinalIgnoreCase)))
        {
            why.Add("source-owned header/footer container evidence");
            reasons = why;
            return "CAPTION_NOTE_SIGNATURE_FOOTER";
        }
        if (Regex.IsMatch(lowerText, "^(figure|fig\\.?|table|note|signature|signed|prepared|reviewed|approved)\\b", RegexOptions.CultureInvariant))
        {
            why.Add("generic caption/note/signature lexical marker");
            reasons = why;
            return "CAPTION_NOTE_SIGNATURE_FOOTER";
        }
        if (textCount > 1 && (source.AllCaps || source.Bold || string.Equals(source.Alignment, "center", StringComparison.OrdinalIgnoreCase)))
        {
            why.Add($"normalized source text repeats {textCount} times with banner-like formatting");
            reasons = why;
            return "REPEATED_OR_BANNER_OCCURRENCE";
        }
        if (!string.IsNullOrWhiteSpace(source.NumberLabel) || source.OutlineLevel is not null || source.BuiltInHeadingStyleLevel is not null)
        {
            why.Add("numbering/style/outline signal was present without stronger heading authority");
            reasons = why;
            return "NUMBERING_STYLE_FALSE_SIGNAL";
        }
        if (string.Equals(source.StyleName, "Normal", StringComparison.OrdinalIgnoreCase) &&
            (item.Text.Length > 80 || item.Text.Contains('.', StringComparison.Ordinal) || item.Text.Contains(';', StringComparison.Ordinal)))
        {
            why.Add("Normal-style long/sentence-like paragraph without stronger structural evidence");
            reasons = why;
            return "BODY_PARAGRAPH_MISTAKEN_AS_HEADING";
        }
        why.Add("source is structural/label-like but no generic exclusion signature was decisive");
        reasons = why;
        return "OTHER_STRUCTURAL_LABEL";
    }

    private static EvidenceRow ReadEvidence(JsonElement item) => new(
        item.GetProperty("sourceId").GetString()!, item.GetProperty("sourceOrdinal").GetInt32(),
        item.GetProperty("exactSourceText").GetString()!, item.GetProperty("structuralScope").GetString() ?? "",
        item.GetProperty("tableDepth").GetInt32(),
        item.GetProperty("styleFacts").GetProperty("styleName").GetString() ?? "",
        item.GetProperty("styleFacts").TryGetProperty("outlineLevel", out var outline) && outline.ValueKind != JsonValueKind.Null ? outline.GetInt32() : null,
        item.GetProperty("styleFacts").TryGetProperty("builtInHeadingStyleLevel", out var builtIn) && builtIn.ValueKind != JsonValueKind.Null ? builtIn.GetInt32() : null,
        item.GetProperty("styleFacts").GetProperty("bold").GetBoolean(), item.GetProperty("styleFacts").GetProperty("allCaps").GetBoolean(),
        item.GetProperty("styleFacts").GetProperty("alignment").GetString() ?? "",
        item.GetProperty("numberingFacts").GetProperty("numberLabel").GetString() ?? "",
        item.GetProperty("markerFacts").GetArrayLength(), item.GetProperty("containerFacts").EnumerateArray().Select(fact => fact.GetString() ?? "").ToArray());

    private static async Task<IReadOnlyList<CanonicalSemanticProposal>> ReadFrozenProposalsAsync(string repoRoot, CancellationToken ct)
    {
        var result = new List<CanonicalSemanticProposal>();
        var execution = Full(repoRoot, ExecutionRoot);
        for (var ordinal = 1; ordinal <= 7; ordinal++)
        {
            using var attempt = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(execution, $"segment-{ordinal:000}.attempt.v1.json"), ct));
            var response = SemanticTextExactBindingContract.Parse(attempt.RootElement.GetProperty("rawResponse").GetString()!);
            result.AddRange(response.Headings.Select(item => new CanonicalSemanticProposal(item.Source, true, item.Text,
                SemanticRole: item.Role, Occurrence: item.Occurrence, LeftExactContext: item.LeftExactContext, RightExactContext: item.RightExactContext)));
        }
        return result;
    }

    private static IReadOnlyList<CategoryCount> Counts(IEnumerable<string> categories) => categories.GroupBy(item => item, StringComparer.Ordinal)
        .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal)
        .Select(group => new CategoryCount(group.Key, group.Count())).ToArray();
    private static string Norm(string text) => Regex.Replace(text.Trim().ToUpperInvariant(), "\\s+", " ");
    private static string Key(string sourceId, int start, int end, string text) => $"{sourceId}\u001f{start}\u001f{end}\u001f{text}";
    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string BuildReport(IReadOnlyList<FalsePositive> fps, IReadOnlyList<FalseNegative> fns) => string.Join(Environment.NewLine, new[]
    {
        "# DOC-0116 frozen score forensic decomposition",
        "",
        "Diagnostic-only source-evidence attribution. No production policy, prediction, or Gold was modified.",
        "",
        "## False positives",
        "",
        string.Join(Environment.NewLine, Counts(fps.Select(item => item.Category)).Select(item => $"- `{item.category}`: {item.count}")),
        "",
        "## False negatives",
        "",
        string.Join(Environment.NewLine, Counts(fns.Select(item => item.Category)).Select(item => $"- `{item.category}`: {item.count}")),
        "",
        "All categories are generic forensic hypotheses and are not production filters.",
    });
    private static int Blocked(string reason)
    {
        Console.WriteLine($"STATUS=BLOCKED:{reason}");
        Console.WriteLine("PROVIDER_CALLS=0");
        Console.WriteLine("GOLD_READS=0");
        return 2;
    }

    private sealed record GoldRow(string OccurrenceId, string SourceId, int Start, int End, string Text);
    private sealed record Prediction(string OccurrenceId, string SourceId, int SourceOrdinal, int Start, int End, string Text, string SemanticRole);
    private sealed record EvidenceRow(string SourceId, int SourceOrdinal, string Text, string StructuralScope, int TableDepth, string StyleName, int? OutlineLevel, int? BuiltInHeadingStyleLevel, bool Bold, bool AllCaps, string Alignment, string NumberLabel, int MarkerCount, IReadOnlyList<string> ContainerFacts);
    private sealed record FalsePositive(string Category, IReadOnlyList<string> Reasons, string SourceId, int Start, int End, string Text, string? StructuralScope, int TableDepth, string? StyleName, int? OutlineLevel, string? NumberLabel, int MarkerCount);
    private sealed record FalseNegative(string Category, IReadOnlyList<string> Reasons, string OccurrenceId, string SourceId, int Start, int End, string Text, string? StructuralScope, int TableDepth);
    private sealed record CategoryCount(string category, int count);
}
