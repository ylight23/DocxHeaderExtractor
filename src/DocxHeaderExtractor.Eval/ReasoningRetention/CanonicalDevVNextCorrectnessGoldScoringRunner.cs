using System.Security.Cryptography;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Scores the already frozen DOC-0116 v2.1 prediction against the user-approved canonical
/// authority. This runner is offline-only: it never calls a provider and never changes the
/// prediction or Gold artifacts.
/// </summary>
public static class CanonicalDevVNextCorrectnessGoldScoringRunner
{
    private const string ExecutionRoot = "artifacts/level-accuracy/canonical-vnext-correctness-doc0116-live-execution-v2-1";
    private const string PredictionFreeze = ExecutionRoot + "/prediction-freeze.v1.json";
    private const string PredictionForensic = ExecutionRoot + "/prediction-forensic-v1/prediction-forensic.v1.json";
    private const string PreflightRoot = "artifacts/level-accuracy/canonical-vnext-correctness-doc0116-live-preflight-v2-1";
    private const string OutputRoot = ExecutionRoot + "/gold-scoring-v1";
    private const string AuthorityRoot = "artifacts/authority-audit/canonical-authority-freeze-v1/DOC-0116";
    private const string OccurrenceRoot = "artifacts/authority-audit/canonical-batch-v3.3/canonical-exhaustive-heading-occurrence-v3.3/DOC-0116";
    private const string IdentityRoot = "artifacts/authority-audit/canonical-batch-v3.3/canonical-semantic-identity-v3.3/DOC-0116";
    private const string HierarchyRoot = "artifacts/authority-audit/canonical-batch-v3.3/canonical-hierarchy-v3.3/DOC-0116";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var predictionPath = Full(repoRoot, PredictionFreeze);
        var forensicPath = Full(repoRoot, PredictionForensic);
        var authorityManifestPath = Full(repoRoot, AuthorityRoot + "/authority-freeze-manifest.json");
        var sourceAuthorityPath = Full(repoRoot, OccurrenceRoot + "/source-authority.json");
        var exactBindingsPath = Full(repoRoot, OccurrenceRoot + "/exact-bindings.json");
        var occurrenceValidationPath = Full(repoRoot, OccurrenceRoot + "/validation.json");
        var identityPath = Full(repoRoot, IdentityRoot + "/identity-decisions.json");
        var semanticNodesPath = Full(repoRoot, IdentityRoot + "/semantic-nodes.json");
        var identityValidationPath = Full(repoRoot, IdentityRoot + "/validation.json");
        var parentEdgesPath = Full(repoRoot, HierarchyRoot + "/parent-edges.json");
        var parentDecisionsPath = Full(repoRoot, HierarchyRoot + "/parent-decisions.json");
        var hierarchyValidationPath = Full(repoRoot, HierarchyRoot + "/validation.json");
        var derivedLevelsPath = Full(repoRoot, HierarchyRoot + "/derived-levels.json");
        var strictPartialPath = Full(repoRoot, "eval/a99-closed-loop/strict-gold-v4/DOC-0116.strict-gold-v4.json");

        var required = new[] { predictionPath, forensicPath, authorityManifestPath, sourceAuthorityPath,
            exactBindingsPath, occurrenceValidationPath, identityPath, semanticNodesPath,
            identityValidationPath, parentEdgesPath, parentDecisionsPath, hierarchyValidationPath, derivedLevelsPath };
        if (required.Any(path => !File.Exists(path)))
            return Blocked("FROZEN_PREDICTION_OR_CANONICAL_GOLD_MISSING");

        using var prediction = JsonDocument.Parse(await File.ReadAllTextAsync(predictionPath, ct));
        using var forensic = JsonDocument.Parse(await File.ReadAllTextAsync(forensicPath, ct));
        using var authority = JsonDocument.Parse(await File.ReadAllTextAsync(authorityManifestPath, ct));
        using var sourceAuthority = JsonDocument.Parse(await File.ReadAllTextAsync(sourceAuthorityPath, ct));
        using var exactBindings = JsonDocument.Parse(await File.ReadAllTextAsync(exactBindingsPath, ct));
        using var occurrenceValidation = JsonDocument.Parse(await File.ReadAllTextAsync(occurrenceValidationPath, ct));
        using var identity = JsonDocument.Parse(await File.ReadAllTextAsync(identityPath, ct));
        using var semanticNodes = JsonDocument.Parse(await File.ReadAllTextAsync(semanticNodesPath, ct));
        using var identityValidation = JsonDocument.Parse(await File.ReadAllTextAsync(identityValidationPath, ct));
        using var parentEdges = JsonDocument.Parse(await File.ReadAllTextAsync(parentEdgesPath, ct));
        using var parentDecisions = JsonDocument.Parse(await File.ReadAllTextAsync(parentDecisionsPath, ct));
        using var hierarchyValidation = JsonDocument.Parse(await File.ReadAllTextAsync(hierarchyValidationPath, ct));
        using var derivedLevels = JsonDocument.Parse(await File.ReadAllTextAsync(derivedLevelsPath, ct));

        if (prediction.RootElement.GetProperty("goldReads").GetInt32() != 0 ||
            prediction.RootElement.GetProperty("scoring").GetBoolean() ||
            prediction.RootElement.GetProperty("canonicalGraphOccurrenceCount").GetInt32() != 289 ||
            forensic.RootElement.GetProperty("replayMatchesFrozenGraph").GetBoolean() == false ||
            forensic.RootElement.GetProperty("goldReads").GetInt32() != 0)
            return Blocked("PREDICTION_FREEZE_NOT_ELIGIBLE_FOR_SCORING");

        if (!string.Equals(authority.RootElement.GetProperty("authorityStatus").GetString(),
                "USER_REVIEWED_CANONICAL_HIERARCHY_GOLD", StringComparison.Ordinal) ||
            !authority.RootElement.GetProperty("explicitUserApproval").GetBoolean() ||
            authority.RootElement.GetProperty("occurrenceCount").GetInt32() != 120 ||
            authority.RootElement.GetProperty("semanticNodeCount").GetInt32() != 120)
            return Blocked("CANONICAL_GOLD_AUTHORITY_NOT_EXPLICITLY_FROZEN");

        var authorityHashChecks = authority.RootElement.GetProperty("artifactHashes").EnumerateArray()
            .Select(item =>
            {
                var relative = item.GetProperty("path").GetString()!;
                var expected = item.GetProperty("sha256").GetString()!;
                var path = Full(repoRoot, relative);
                var actual = File.Exists(path) ? Sha256File(path) : null;
                return new { artifact = item.GetProperty("artifact").GetString(), path = relative, expected, actual, matches = actual is not null && string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase) };
            }).ToArray();
        if (authorityHashChecks.Any(item => !item.matches))
            return Blocked("CANONICAL_GOLD_AUTHORITY_HASH_DRIFT");

        var goldBindings = exactBindings.RootElement.GetProperty("bindings").EnumerateArray()
            .Select(item => new GoldOccurrence(
                item.GetProperty("occurrenceId").GetString()!,
                item.GetProperty("sourceId").GetString()!,
                item.GetProperty("sourceSpan").GetProperty("start").GetInt32(),
                item.GetProperty("sourceSpan").GetProperty("end").GetInt32(),
                item.GetProperty("exactText").GetString()!,
                item.GetProperty("headingKind").GetString()!))
            .ToArray();
        var goldIdentity = identity.RootElement.GetProperty("assignments").EnumerateArray()
            .ToDictionary(item => item.GetProperty("occurrenceId").GetString()!, item => new GoldIdentity(
                item.GetProperty("semanticNodeRef").GetString()!,
                item.GetProperty("occurrenceRole").GetString()!), StringComparer.Ordinal);

        // Freeze the complete canonical Gold input before any replay/scoring work.
        var goldFiles = new[]
        {
            ("authorityManifest", authorityManifestPath), ("sourceAuthority", sourceAuthorityPath),
            ("exactBindings", exactBindingsPath), ("occurrenceValidation", occurrenceValidationPath),
            ("identityDecisions", identityPath), ("semanticNodes", semanticNodesPath),
            ("identityValidation", identityValidationPath), ("parentEdges", parentEdgesPath),
            ("parentDecisions", parentDecisionsPath), ("hierarchyValidation", hierarchyValidationPath),
            ("derivedLevels", derivedLevelsPath)
        };
        var goldHashes = goldFiles.Select(item => new { artifact = item.Item1, path = Rel(repoRoot, item.Item2), sha256 = Sha256File(item.Item2) }).ToArray();
        var goldFreeze = new
        {
            schemaVersion = "a99-canonical-dev-vnext-doc0116-gold-freeze-v1",
            documentId = "DOC-0116",
            authorityStatus = authority.RootElement.GetProperty("authorityStatus").GetString(),
            explicitUserApproval = authority.RootElement.GetProperty("explicitUserApproval").GetBoolean(),
            canonicalGoldOccurrenceCount = goldBindings.Length,
            canonicalGoldSemanticNodeCount = authority.RootElement.GetProperty("semanticNodeCount").GetInt32(),
            predictionAuthority = new
            {
                predictionFreeze = Rel(repoRoot, predictionPath),
                predictionFreezeSha256 = Sha256File(predictionPath),
                predictionForensic = Rel(repoRoot, forensicPath),
                predictionForensicSha256 = Sha256File(forensicPath),
                frozenCanonicalGraphOccurrences = prediction.RootElement.GetProperty("canonicalGraphOccurrenceCount").GetInt32(),
            },
            rejectedPartialReference = File.Exists(strictPartialPath) ? Rel(repoRoot, strictPartialPath) : null,
            rejectedPartialReferenceReason = File.Exists(strictPartialPath) ? "STRICT_GOLD_V4_PARTIAL_NOT_EXHAUSTIVE" : "NOT_PRESENT",
            goldReads = 1,
            providerCalls = 0,
            modelCalls = 0,
            scoring = false,
            goldFiles = goldHashes,
            goldSourceSha256 = authority.RootElement.GetProperty("sourceSha256").GetString(),
            authorityManifestHashChecks = authorityHashChecks,
            frozenAtUtc = DateTimeOffset.UtcNow,
        };
        var outputDir = Full(repoRoot, OutputRoot);
        Directory.CreateDirectory(outputDir);
        var goldFreezePath = Path.Combine(outputDir, "gold-freeze.v1.json");
        await File.WriteAllTextAsync(goldFreezePath, JsonSerializer.Serialize(goldFreeze, JsonOptions) + Environment.NewLine, ct);

        var rawProposals = await ReadFrozenProposalsAsync(repoRoot, ct);
        var preflightManifest = Full(repoRoot, PreflightRoot + "/request-freeze-manifest.v1.json");
        using var requestFreeze = JsonDocument.Parse(await File.ReadAllTextAsync(preflightManifest, ct));
        var sourceUniverseSha = requestFreeze.RootElement.GetProperty("sourceUniverseSha256").GetString()!;
        var fullInput = await CanonicalDevVNextCorrectnessProviderExecutionRunner.BuildFullInputAsync(repoRoot, sourceUniverseSha, ct);
        var replay = CanonicalSemanticProductionEntryPoint.Run(fullInput with { SemanticProposals = rawProposals });
        var predictions = replay.CanonicalOccurrences
            .Select(item => new PredictedOccurrence(item.OccurrenceId, item.SourceId, item.Start, item.End, item.Text,
                item.SemanticRole, item.OccurrenceKind, item.SemanticNodeId))
            .ToArray();
        if (predictions.Length != 289)
            return Blocked("REPLAY_PREDICTION_CARDINALITY_DRIFT");

        var goldByKey = goldBindings.ToDictionary(item => Key(item.SourceId, item.Start, item.End, item.Text), StringComparer.Ordinal);
        var predictedByKey = predictions.ToDictionary(item => Key(item.SourceId, item.Start, item.End, item.Text), StringComparer.Ordinal);
        var truePositive = predictions.Where(item => goldByKey.ContainsKey(Key(item.SourceId, item.Start, item.End, item.Text))).ToArray();
        var falsePositive = predictions.Where(item => !goldByKey.ContainsKey(Key(item.SourceId, item.Start, item.End, item.Text))).ToArray();
        var falseNegative = goldBindings.Where(item => !predictedByKey.ContainsKey(Key(item.SourceId, item.Start, item.End, item.Text))).ToArray();
        var roleRows = truePositive.Select(item =>
        {
            var gold = goldByKey[Key(item.SourceId, item.Start, item.End, item.Text)];
            var identityRow = goldIdentity[gold.OccurrenceId];
            return new
            {
                occurrenceId = gold.OccurrenceId,
                predictedSemanticRole = item.SemanticRole,
                goldHeadingKind = gold.HeadingKind,
                semanticRoleExact = string.Equals(item.SemanticRole, gold.HeadingKind, StringComparison.Ordinal),
                predictedOccurrenceKind = item.OccurrenceKind,
                goldOccurrenceRole = identityRow.OccurrenceRole,
                occurrenceRoleExact = string.Equals(item.OccurrenceKind, identityRow.OccurrenceRole, StringComparison.Ordinal),
                predictedSemanticNodeId = item.SemanticNodeId,
                goldSemanticNodeRef = identityRow.SemanticNodeRef,
            };
        }).ToArray();
        var score = new
        {
            schemaVersion = "a99-canonical-dev-vnext-doc0116-gold-score-v1",
            documentId = "DOC-0116",
            predictionAuthority = new
            {
                predictionFreezeSha256 = Sha256File(predictionPath),
                predictionForensicSha256 = Sha256File(forensicPath),
                frozenPredictionOccurrences = predictions.Length,
                canonicalGraphOccurrenceCount = prediction.RootElement.GetProperty("canonicalGraphOccurrenceCount").GetInt32(),
                rawProposals = forensic.RootElement.GetProperty("rawProposals").GetInt32(),
                nonVerbatimText = forensic.RootElement.GetProperty("bindingObservationStatusCounts").TryGetProperty("NonVerbatimText", out var nvt) ? nvt.GetInt32() : 0,
            },
            goldAuthority = new
            {
                goldFreezeSha256 = Sha256File(goldFreezePath),
                canonicalGoldOccurrences = goldBindings.Length,
                canonicalGoldSemanticNodes = goldIdentity.Values.Select(item => item.SemanticNodeRef).Distinct(StringComparer.Ordinal).Count(),
                authorityStatus = authority.RootElement.GetProperty("authorityStatus").GetString(),
            },
            occurrenceDetection = Metrics(truePositive.Length, predictions.Length, goldBindings.Length),
            exactBoundCorrectness = new
            {
                predictedPhysicalOccurrences = predictions.Length,
                sourceIdMatches = predictions.Count(pred => goldBindings.Any(gold => string.Equals(gold.SourceId, pred.SourceId, StringComparison.Ordinal))),
                exactSpanMatches = predictions.Count(pred => goldBindings.Any(gold => string.Equals(gold.SourceId, pred.SourceId, StringComparison.Ordinal) && gold.Start == pred.Start && gold.End == pred.End)),
                exactPhysicalOccurrenceMatches = truePositive.Length,
                exactTextOnPhysicalMatches = truePositive.Count(item => goldByKey[Key(item.SourceId, item.Start, item.End, item.Text)].Text == item.Text),
            },
            semanticClassification = new
            {
                matchedOccurrences = roleRows.Length,
                semanticRoleTaxonomy = "predicted SemanticRole vs canonical headingKind",
                semanticRoleTaxonomiesComparable = false,
                semanticRoleAccuracy = (double?)null,
                rawLabelExactAgreement = Ratio(roleRows.Count(item => item.semanticRoleExact), roleRows.Length),
                rawLabelExactAgreementCount = roleRows.Count(item => item.semanticRoleExact),
                occurrenceRoleAccuracy = Ratio(roleRows.Count(item => item.occurrenceRoleExact), roleRows.Length),
                occurrenceRoleCorrect = roleRows.Count(item => item.occurrenceRoleExact),
                semanticRoleConfusion = roleRows.GroupBy(item => (Predicted: item.predictedSemanticRole, Gold: item.goldHeadingKind))
                    .OrderBy(group => group.Key.Predicted, StringComparer.Ordinal).ThenBy(group => group.Key.Gold, StringComparer.Ordinal)
                    .Select(group => new { predicted = group.Key.Predicted, gold = group.Key.Gold, count = group.Count() }).ToArray(),
            },
            errors = new
            {
                falsePositiveCount = falsePositive.Length,
                falseNegativeCount = falseNegative.Length,
                falsePositiveOccurrences = falsePositive.Select(item => new { item.OccurrenceId, item.SourceId, item.Start, item.End, item.Text, item.SemanticRole }).ToArray(),
                falseNegativeOccurrences = falseNegative.Select(item => new { item.OccurrenceId, item.SourceId, item.Start, item.End, item.Text, item.HeadingKind }).ToArray(),
                nonVerbatimTextIsDiagnosticOnly = true,
            },
            providerCalls = 0,
            modelCalls = 0,
            goldReads = 1,
            scoring = true,
        };
        var scorePath = Path.Combine(outputDir, "score.v1.json");
        await File.WriteAllTextAsync(scorePath, JsonSerializer.Serialize(score, JsonOptions) + Environment.NewLine, ct);
        var report = BuildReport(score, goldFreezePath, predictions.Length, goldBindings.Length, truePositive.Length, falsePositive.Length, falseNegative.Length);
        await File.WriteAllTextAsync(Path.Combine(outputDir, "report.md"), report, ct);
        await File.WriteAllTextAsync(Path.Combine(outputDir, "error-decomposition.v1.json"), JsonSerializer.Serialize(score.errors, JsonOptions) + Environment.NewLine, ct);
        await File.WriteAllTextAsync(Path.Combine(outputDir, "manifest.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = "a99-canonical-dev-vnext-doc0116-gold-scoring-manifest-v1",
            scoreSha256 = Sha256File(scorePath),
            goldFreezeSha256 = Sha256File(goldFreezePath),
            providerCalls = 0,
            modelCalls = 0,
            goldReads = 1,
            scoring = true,
        }, JsonOptions) + Environment.NewLine, ct);
        Console.WriteLine("STATUS=GOLD_SCORED");
        Console.WriteLine($"PREDICTED={predictions.Length}");
        Console.WriteLine($"GOLD={goldBindings.Length}");
        Console.WriteLine($"TP={truePositive.Length}");
        Console.WriteLine($"FP={falsePositive.Length}");
        Console.WriteLine($"FN={falseNegative.Length}");
        Console.WriteLine("PROVIDER_CALLS=0");
        Console.WriteLine("GOLD_READS=1");
        return 0;
    }

    private static async Task<IReadOnlyList<CanonicalSemanticProposal>> ReadFrozenProposalsAsync(string repoRoot, CancellationToken ct)
    {
        var execution = Full(repoRoot, ExecutionRoot);
        var result = new List<CanonicalSemanticProposal>();
        for (var ordinal = 1; ordinal <= 7; ordinal++)
        {
            var attemptPath = Path.Combine(execution, $"segment-{ordinal:000}.attempt.v1.json");
            using var attempt = JsonDocument.Parse(await File.ReadAllTextAsync(attemptPath, ct));
            var rawResponse = attempt.RootElement.GetProperty("rawResponse").GetString() ?? throw new InvalidDataException("RAW_RESPONSE_MISSING");
            var response = SemanticTextExactBindingContract.Parse(rawResponse);
            result.AddRange(response.Headings.Select(item => new CanonicalSemanticProposal(item.Source, true, item.Text,
                SemanticRole: item.Role, Occurrence: item.Occurrence, LeftExactContext: item.LeftExactContext,
                RightExactContext: item.RightExactContext)));
        }
        return result;
    }

    private static object Metrics(int tp, int predicted, int gold) => new
    {
        truePositive = tp,
        falsePositive = predicted - tp,
        falseNegative = gold - tp,
        predicted,
        gold,
        precision = Ratio(tp, predicted),
        recall = Ratio(tp, gold),
        f1 = F1(tp, predicted, gold),
    };

    private static string BuildReport(object score, string goldFreezePath, int predicted, int gold, int tp, int fp, int fn) => string.Join(Environment.NewLine, new[]
    {
        "# DOC-0116 canonical prediction scoring",
        "",
        "This is offline scoring of frozen provider output. No provider call or prediction rebuild was used as an authority.",
        "",
        $"- Prediction occurrences: **{predicted}**",
        $"- Canonical Gold occurrences: **{gold}**",
        $"- TP / FP / FN: **{tp} / {fp} / {fn}**",
        $"- Precision / Recall / F1: **{Ratio(tp, predicted):0.####} / {Ratio(tp, gold):0.####} / {F1(tp, predicted, gold):0.####}**",
        "",
        "The 5 `NonVerbatimText` observations remain diagnostic attribution only; they are not returned to the prediction set.",
        "",
        $"Gold freeze: `{goldFreezePath}`",
        "The historical `strict-gold-v4` DOC-0116 artifact was not used because it is partial/non-exhaustive.",
    });

    private static double? Ratio(int numerator, int denominator) => denominator == 0 ? null : (double)numerator / denominator;
    private static double? F1(int tp, int predicted, int gold)
    {
        var p = Ratio(tp, predicted);
        var r = Ratio(tp, gold);
        return p is null || r is null || p + r == 0 ? null : 2 * p * r / (p + r);
    }
    private static string Key(string sourceId, int start, int end, string text) => $"{sourceId}\u001f{start}\u001f{end}\u001f{text}";
    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static string Rel(string root, string path) => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static int Blocked(string reason)
    {
        Console.WriteLine($"STATUS=BLOCKED:{reason}");
        Console.WriteLine("PROVIDER_CALLS=0");
        Console.WriteLine("GOLD_READS=0");
        return 2;
    }

    private sealed record GoldOccurrence(string OccurrenceId, string SourceId, int Start, int End, string Text, string HeadingKind);
    private sealed record GoldIdentity(string SemanticNodeRef, string OccurrenceRole);
    private sealed record PredictedOccurrence(string OccurrenceId, string SourceId, int Start, int End, string Text, string SemanticRole, string OccurrenceKind, string SemanticNodeId);
}
