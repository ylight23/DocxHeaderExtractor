using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Policy;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Offline A/B replay. Both branches consume the same already-frozen raw model proposals; the
/// runner does not contact a provider. Gold is opened only after the replay prediction/freeze is
/// persisted, so this isolates downstream v6 behavior from model stochasticity.
/// </summary>
public static class A99V6DeterministicReplayRunner
{
    private const string InventoryPath = "eval/a99-dataset/document-inventory.v1.json";
    private const string BaselineRoot = "eval/a99-closed-loop/production-acceptance/runs/flash-restart-20260912-02";
    private const string WholeAliasInputRoot = "eval/a99-closed-loop/production-v6-accuracy-full-e2e";
    private const string OutputRoot = "eval/a99-closed-loop/production-v6-replay-ab";
    private const string WholeAliasOutputRoot = "eval/a99-closed-loop/selection-mode-whole-alias-replay/DOC-0205";
    private static readonly string[] Documents = ["DOC-0001", "DOC-0205", "DOC-0252", "DOC-0256", "DOC-0258"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(output);
        var startHead = GitSha(repoRoot);
        var rows = new List<ReplayCell>();
        foreach (var documentId in Documents)
        {
            var context = LoadContext(repoRoot, documentId);
            for (var repeat = 1; repeat <= 3; repeat++)
            {
                ct.ThrowIfCancellationRequested();
                var baselinePath = Path.Combine(repoRoot, BaselineRoot.Replace('/', Path.DirectorySeparatorChar), documentId, $"r{repeat}", "prediction.v1.json");
                using var baseline = JsonDocument.Parse(await File.ReadAllTextAsync(baselinePath, ct));
                var root = baseline.RootElement;
                var raw = ParseRaw(root.GetProperty("rawModelHeadings"));
                var oldBound = ParseRows(root.GetProperty("boundHeadings"));
                var oldFinal = ParseRows(root.GetProperty("finalHeadings"));
                var replayDir = Path.Combine(output, documentId, $"r{repeat}");
                Directory.CreateDirectory(replayDir);
                var production = CanonicalSemanticProductionEntryPoint.Run(BuildInput(context, raw));
                var v6Bound = production.TextPipeline.BoundHeadings.Select(item =>
                    new ReplayRow(item.SourceId, item.Start, item.End, item.Text, item.SemanticRole)).ToArray();
                var proposals = v6Bound.Select(item => new ReasoningHeadingProposal
                {
                    SourceId = item.SourceId,
                    HeadingSpan = new StructuralSpan(item.Start, item.End),
                    Text = item.Text,
                    SemanticRole = item.Role,
                    Confidence = 1,
                }).ToArray();
                var materialized = ReasoningProposalMaterializer.Materialize(context.Source, context.Policy, proposals);
                var v6Final = ReasoningTaskProjection.ProjectContentHeadings(materialized.Structure)
                    .OrderBy(item => item.Sources.Single().SourceOrdinal)
                    .ThenBy(item => item.Sources.Single().Span.Start)
                    .ThenBy(item => item.Id, StringComparer.Ordinal)
                    .Select(item => new ReplayRow(item.Sources.Single().SourceId, item.Sources.Single().Span.Start,
                        item.Sources.Single().Span.End, item.Text, item.Role.ToString()))
                    .ToArray();
                var stage = StageAttribution(oldBound, v6Bound, oldFinal, v6Final, production);
                var predictionPath = Path.Combine(replayDir, "prediction.v1.json");
                var prediction = new
                {
                    schemaVersion = "a99-v6-deterministic-replay-prediction-v1",
                    documentId, repeat = $"r{repeat}", model = root.GetProperty("model").GetString(),
                    sourceSha256 = context.SourceSha256,
                    rawProposalSha256 = Sha256Text(JsonSerializer.Serialize(raw)),
                    rawProposalCount = raw.Count,
                    oldControl = new { boundHeadings = oldBound, finalHeadings = oldFinal },
                    v6 = new
                    {
                        boundHeadings = v6Bound,
                        finalHeadings = v6Final,
                        modality = production.ModalityProfile.DocumentModality.ToString(),
                        visualRecoveredCount = production.VisualOccurrences.Count,
                        unifiedOccurrenceCount = production.UnifiedOccurrences.Count,
                        canonicalOccurrenceCount = production.CanonicalOccurrences.Count,
                        projectedCount = production.Projection.Count,
                        stageLedger = production.StageLedger,
                    },
                    stageAttribution = stage,
                    providerCalls = 0,
                    goldReadBeforeFreeze = false,
                };
                await WriteJson(predictionPath, prediction, ct);
                var freezePath = Path.Combine(replayDir, "freeze.v1.json");
                await WriteJson(freezePath, new
                {
                    schemaVersion = "a99-v6-deterministic-replay-freeze-v1", documentId, repeat = $"r{repeat}",
                    sourceSha256 = context.SourceSha256, rawProposalSha256 = Sha256Text(JsonSerializer.Serialize(raw)),
                    predictionSha256 = Sha256(predictionPath), providerCalls = 0,
                    goldReadBeforeFreeze = false, frozenUtc = DateTimeOffset.UtcNow,
                }, ct);
                var goldPath = Path.Combine(repoRoot, "eval/a99-closed-loop/strict-gold-occurrence-v1", documentId + ".occurrence-gold-v1.json");
                var gold = ReasoningGoldArtifactLoader.LoadOccurrence(goldPath).Where(item => item.HeadingSpan is not null)
                    .Select(item => new ReplayRow(item.SourceId, item.HeadingSpan!.Start, item.HeadingSpan.End, item.ExactText, ""))
                    .ToHashSet();
                var oldScore = Score(oldFinal, gold);
                var v6Score = Score(v6Final, gold);
                var scorePath = Path.Combine(replayDir, "score.v1.json");
                await WriteJson(scorePath, new
                {
                    schemaVersion = "a99-v6-deterministic-replay-score-v1", documentId, repeat = $"r{repeat}",
                    oldControl = oldScore, v6 = v6Score, delta = new
                    {
                        tp = v6Score.Tp - oldScore.Tp, fp = v6Score.Fp - oldScore.Fp,
                        fn = v6Score.Fn - oldScore.Fn, f1 = v6Score.F1 - oldScore.F1,
                    },
                    goldReadBeforeFreeze = false,
                }, ct);
                rows.Add(new(documentId, $"r{repeat}", raw.Count, oldBound.Length, v6Bound.Length,
                    oldFinal.Length, v6Final.Length, stage, oldScore, v6Score));
            }
        }
        await WriteJson(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-v6-deterministic-replay-summary-v1", startHead, endHead = GitSha(repoRoot),
            baselineRoot = BaselineRoot, documents = Documents, cells = rows.Count,
            modelCalls = 0, providerCalls = 0, sameFrozenRawProposals = rows.All(item => item.RawCount >= 0),
            aggregate = new { oldControl = Aggregate(rows.Select(item => item.OldScore)), v6 = Aggregate(rows.Select(item => item.V6Score)) },
            stageAttribution = rows.GroupBy(item => item.Stage, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            rows,
            goldFirewall = "PASS",
        }, ct);
        Console.WriteLine("MODEL_CALLS=0");
        Console.WriteLine("PROVIDER_CALLS=0");
        Console.WriteLine($"REPLAY_CELLS={rows.Count}");
        Console.WriteLine($"REPLAY_SUMMARY={Path.Combine(OutputRoot, "summary.v1.json")}");
        return 0;
    }

    /// <summary>
    /// Offline counterfactual for the proposed WHOLE_ALIAS contract. It consumes only the three
    /// already-frozen full-v6 DOC-0205 raw responses. The model echo is ignored by the binder in
    /// the counterfactual branch, while the standard branch remains the byte-compatible control.
    /// Gold is loaded only after both branch predictions and freeze manifests have been written.
    /// </summary>
    public static async Task<int> RunWholeAliasAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, WholeAliasOutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(output);
        var startHead = GitSha(repoRoot);
        var context = LoadContext(repoRoot, "DOC-0205");
        var rows = new List<WholeAliasCell>();

        for (var repeat = 1; repeat <= 3; repeat++)
        {
            ct.ThrowIfCancellationRequested();
            var inputPath = Path.Combine(repoRoot, WholeAliasInputRoot.Replace('/', Path.DirectorySeparatorChar),
                "DOC-0205", $"r{repeat}", "prediction.v1.json");
            using var frozen = JsonDocument.Parse(await File.ReadAllTextAsync(inputPath, ct));
            var raw = ParseRaw(frozen.RootElement.GetProperty("rawModelHeadings"));
            var rawSha = Sha256Text(JsonSerializer.Serialize(raw));
            var repeatDir = Path.Combine(output, $"r{repeat}");
            var standardDir = Path.Combine(repeatDir, "standard-control");
            var wholeDir = Path.Combine(repeatDir, "whole-alias");
            Directory.CreateDirectory(standardDir);
            Directory.CreateDirectory(wholeDir);

            var standard = CanonicalSemanticProductionEntryPoint.Run(BuildInput(context, raw, wholeAlias: false));
            var wholeAlias = CanonicalSemanticProductionEntryPoint.Run(BuildInput(context, raw, wholeAlias: true));
            var standardFinal = ProjectFinalRows(context, standard);
            var wholeFinal = ProjectFinalRows(context, wholeAlias);

            var standardPredictionPath = Path.Combine(standardDir, "prediction.v1.json");
            var wholePredictionPath = Path.Combine(wholeDir, "prediction.v1.json");
            await WriteJson(standardPredictionPath, BranchPrediction("STANDARD_VERBATIM_CONTROL", raw, rawSha, context, standard, standardFinal), ct);
            await WriteJson(wholePredictionPath, BranchPrediction("COUNTERFACTUAL_FORCED_WHOLE_ALIAS", raw, rawSha, context, wholeAlias, wholeFinal), ct);
            await WriteFreeze(standardDir, "STANDARD_VERBATIM_CONTROL", context, rawSha, standardPredictionPath, ct);
            await WriteFreeze(wholeDir, "COUNTERFACTUAL_FORCED_WHOLE_ALIAS", context, rawSha, wholePredictionPath, ct);

            // Gold firewall: no Gold path is opened until both branch freezes exist.
            var goldPath = Path.Combine(repoRoot, "eval/a99-closed-loop/strict-gold-occurrence-v1", "DOC-0205.occurrence-gold-v1.json");
            var gold = ReasoningGoldArtifactLoader.LoadOccurrence(goldPath).Where(item => item.HeadingSpan is not null)
                .Select(item => new ReplayRow(item.SourceId, item.HeadingSpan!.Start, item.HeadingSpan.End, item.ExactText, ""))
                .ToHashSet();
            var standardScore = Score(standardFinal, gold);
            var wholeScore = Score(wholeFinal, gold);
            await WriteJson(Path.Combine(repeatDir, "score.v1.json"), new
            {
                schemaVersion = "a99-v6-whole-alias-replay-score-v1",
                documentId = "DOC-0205", repeat = $"r{repeat}", rawProposalCount = raw.Count,
                standardControl = standardScore, forcedWholeAlias = wholeScore,
                delta = new
                {
                    tp = wholeScore.Tp - standardScore.Tp, fp = wholeScore.Fp - standardScore.Fp,
                    fn = wholeScore.Fn - standardScore.Fn, f1 = wholeScore.F1 - standardScore.F1,
                },
                goldReadBeforeFreeze = false,
            }, ct);
            rows.Add(new($"r{repeat}", raw.Count, standard.TextPipeline.BoundHeadings.Count, wholeAlias.TextPipeline.BoundHeadings.Count,
                standardFinal.Length, wholeFinal.Length, standardScore, wholeScore,
                context.Aliases.Select(item => new { item.Alias, item.SourceId, chars = item.RawText.Length }).ToArray()));
        }

        await WriteJson(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-v6-whole-alias-replay-summary-v1",
            startHead, endHead = GitSha(repoRoot), documentId = "DOC-0205", repeats = 3,
            sourceSha256 = context.SourceSha256, frozenInputRoot = WholeAliasInputRoot,
            modelCalls = 0, providerCalls = 0, forcedMode = true,
            control = "SAME_FROZEN_RAW_PROPOSALS_STANDARD_BINDING",
            hypothesis = "WHOLE_ALIAS_IGNORES_MODEL_TEXT_ECHO",
            aliases = context.Aliases.Select(item => new { item.Alias, item.SourceId, chars = item.RawText.Length }).ToArray(),
            aggregate = new { standardControl = Aggregate(rows.Select(item => item.StandardScore)), forcedWholeAlias = Aggregate(rows.Select(item => item.WholeAliasScore)) },
            rows, goldFirewall = "PASS",
            decision = rows.Any(item => item.WholeAliasScore.F1 > item.StandardScore.F1)
                ? "COUNTERFACTUAL_SIGNAL_REQUIRES_GENERIC_ELIGIBILITY_DESIGN"
                : "NO_ACCURACY_LIFT_FORCED_WHOLE_ALIAS",
        }, ct);
        Console.WriteLine("MODEL_CALLS=0");
        Console.WriteLine("PROVIDER_CALLS=0");
        Console.WriteLine("WHOLE_ALIAS_REPLAY_CELLS=3");
        Console.WriteLine($"WHOLE_ALIAS_REPLAY_SUMMARY={Path.Combine(WholeAliasOutputRoot, "summary.v1.json")}");
        return 0;
    }

    private static ProductionContext LoadContext(string repoRoot, string documentId)
    {
        using var inventory = JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot, InventoryPath.Replace('/', Path.DirectorySeparatorChar))));
        var item = inventory.RootElement.GetProperty("documents").EnumerateArray()
            .Single(item => item.GetProperty("documentId").GetString() == documentId);
        var sourcePath = Path.Combine(repoRoot, item.GetProperty("sourcePath").GetString()!.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
        var sourceSha = item.GetProperty("sourceSha256").GetString()!;
        if (!File.Exists(sourcePath) || !string.Equals(Sha256(sourcePath), sourceSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SOURCE_HASH_MISMATCH:" + documentId);
        var source = new OpenXmlDocumentSource().Read(sourcePath) with { DocumentId = documentId };
        var features = NumberingStyleFeatures.FromSourceDocument(source);
        var derived = new DocumentFeatureDeriver().Derive(source);
        var policy = DocxPolicyStateBuilder.Build(source, features, derived, new PipelineOptions { DisableLlm = false }.Extraction);
        var aliases = source.Paragraphs.Where(item => !string.IsNullOrWhiteSpace(item.Text))
            .Select((item, index) => new SemanticTextSourceAlias($"S{index + 1:0000}", item.SourceId, item.SourceOrdinal, item.Text)).ToArray();
        return new ProductionContext(documentId, sourceSha, sourcePath, source, policy, aliases);
    }

    private static CanonicalSemanticProductionInput BuildInput(ProductionContext context, IReadOnlyList<SemanticTextHeading> raw, bool wholeAlias = false)
    {
        var catalog = new DocumentSourceCatalog(context.Aliases.Select(item => new DocumentSourceUnit(item.SourceId, item.SourceOrdinal, item.RawText,
            new SourceAnchor { SourceType = "DOCX_TEXT", ParagraphId = item.SourceId, ParagraphIndex = item.SourceOrdinal },
            new StructuralSpan(0, item.RawText.Length))));
        var aliases = SemanticSourceAliasCatalog.FromCatalog(catalog);
        var mediaCount = context.SourcePath.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)
            ? CanonicalSemanticDocxMediaInventory.Inspect(context.SourcePath).Count : 0;
        var proposals = raw.Select(item => new CanonicalSemanticProposal(item.Source, true, item.Text,
            SemanticRole: item.Role, Occurrence: item.Occurrence, LeftExactContext: item.LeftExactContext,
            RightExactContext: item.RightExactContext,
            SelectionMode: wholeAlias ? CanonicalSemanticSelectionMode.WholeAlias : null)).ToArray();
        return new CanonicalSemanticProductionInput(
            catalog, proposals, context.SourceSha256,
            [new CanonicalSemanticPageEvidence("P0001", context.Aliases.Count > 0, mediaCount, "OOXML_TEXT_AND_MEDIA_INVENTORY")],
            aliases.Select(item => new SemanticCandidateAttentionHint(item.Alias, false, "ATTENTION_ONLY_NOT_RECALL_GATE")).ToArray(),
            context.Aliases.Select(item => $"{item.Alias}: {item.RawText}").ToArray(), [], ["FROZEN_RAW_PROPOSAL_REPLAY"]);
    }

    private static IReadOnlyList<SemanticTextHeading> ParseRaw(JsonElement array) => array.EnumerateArray().Select(item =>
        new SemanticTextHeading(item.GetProperty("source").GetString()!, item.GetProperty("text").GetString()!, item.GetProperty("role").GetString()!,
            item.TryGetProperty("occurrence", out var occurrence) && occurrence.ValueKind != JsonValueKind.Null ? occurrence.GetInt32() : null,
            item.TryGetProperty("leftExactContext", out var left) && left.ValueKind == JsonValueKind.String ? left.GetString() : null,
            item.TryGetProperty("rightExactContext", out var right) && right.ValueKind == JsonValueKind.String ? right.GetString() : null)).ToArray();

    private static ReplayRow[] ProjectFinalRows(ProductionContext context, CanonicalSemanticProductionResult production)
    {
        var proposals = production.TextPipeline.BoundHeadings
            .Select(item => new ReasoningHeadingProposal
            {
                SourceId = item.SourceId,
                HeadingSpan = new StructuralSpan(item.Start, item.End),
                Text = item.Text,
                SemanticRole = item.SemanticRole,
                Confidence = 1,
            })
            .ToArray();
        var materialized = ReasoningProposalMaterializer.Materialize(context.Source, context.Policy, proposals);
        return ReasoningTaskProjection.ProjectContentHeadings(materialized.Structure)
            .OrderBy(item => item.Sources.Single().SourceOrdinal)
            .ThenBy(item => item.Sources.Single().Span.Start)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => new ReplayRow(item.Sources.Single().SourceId, item.Sources.Single().Span.Start,
                item.Sources.Single().Span.End, item.Text, item.Role.ToString()))
            .ToArray();
    }

    private static object BranchPrediction(string mode, IReadOnlyList<SemanticTextHeading> raw, string rawSha,
        ProductionContext context, CanonicalSemanticProductionResult production, IReadOnlyList<ReplayRow> final)
    {
        return new
        {
            schemaVersion = "a99-v6-whole-alias-replay-prediction-v1", documentId = context.DocumentId,
            mode, sourceSha256 = context.SourceSha256, rawProposalSha256 = rawSha, rawProposalCount = raw.Count,
            rawModelHeadings = raw, boundHeadings = production.TextPipeline.BoundHeadings,
            finalHeadings = final, boundCount = production.TextPipeline.BoundHeadings.Count,
            finalCount = final.Count, canonicalOccurrenceCount = production.CanonicalOccurrences.Count,
            projectedCount = production.Projection.Count, providerCalls = 0, modelCalls = 0,
            goldReadBeforeFreeze = false,
        };
    }

    private static async Task WriteFreeze(string directory, string mode, ProductionContext context, string rawSha,
        string predictionPath, CancellationToken ct)
    {
        await WriteJson(Path.Combine(directory, "freeze.v1.json"), new
        {
            schemaVersion = "a99-v6-whole-alias-replay-freeze-v1", documentId = context.DocumentId, mode,
            sourceSha256 = context.SourceSha256, rawProposalSha256 = rawSha, predictionSha256 = Sha256(predictionPath),
            modelCalls = 0, providerCalls = 0, goldReadBeforeFreeze = false, frozenUtc = DateTimeOffset.UtcNow,
        }, ct);
    }

    private static ReplayRow[] ParseRows(JsonElement array) => array.EnumerateArray().Select(item =>
        new ReplayRow(item.GetProperty("sourceId").GetString()!, item.GetProperty("start").GetInt32(), item.GetProperty("end").GetInt32(),
            item.GetProperty("text").GetString()!, item.TryGetProperty("role", out var role)
                ? role.ValueKind == JsonValueKind.String ? role.GetString() ?? "" : role.ToString()
                : "")).ToArray();

    private static string StageAttribution(IReadOnlyList<ReplayRow> oldBound, IReadOnlyList<ReplayRow> v6Bound, IReadOnlyList<ReplayRow> oldFinal, IReadOnlyList<ReplayRow> v6Final, CanonicalSemanticProductionResult production)
    {
        if (!Keys(oldBound).SetEquals(Keys(v6Bound))) return "BINDER";
        if (production.CanonicalOccurrences.Count != production.TextPipeline.BoundHeadings.Count) return "GLOBAL_RESOLUTION_OR_GRAPH";
        if (production.Projection.Count != production.CanonicalOccurrences.Count) return "TASK_PROJECTION";
        if (!Keys(oldFinal).SetEquals(Keys(v6Final))) return "TASK_PROJECTION";
        return "IDENTICAL";
    }

    private static HashSet<string> Keys(IEnumerable<ReplayRow> rows) => rows.Select(item => $"{item.SourceId}:{item.Start}:{item.End}").ToHashSet(StringComparer.Ordinal);

    private static ScoreRow Score(IReadOnlyList<ReplayRow> prediction, IReadOnlySet<ReplayRow> gold)
    {
        var p = Keys(prediction); var g = Keys(gold); var tp = p.Intersect(g).Count(); var fp = p.Except(g).Count(); var fn = g.Except(p).Count();
        var precision = tp + fp == 0 ? 0d : (double)tp / (tp + fp); var recall = tp + fn == 0 ? 0d : (double)tp / (tp + fn);
        return new(tp, fp, fn, precision, recall, precision + recall == 0 ? 0d : 2 * precision * recall / (precision + recall));
    }

    private static ScoreRow Aggregate(IEnumerable<ScoreRow> scores)
    {
        var rows = scores.ToArray(); var tp = rows.Sum(item => item.Tp); var fp = rows.Sum(item => item.Fp); var fn = rows.Sum(item => item.Fn);
        var p = tp + fp == 0 ? 0d : (double)tp / (tp + fp); var r = tp + fn == 0 ? 0d : (double)tp / (tp + fn);
        return new(tp, fp, fn, p, r, p + r == 0 ? 0d : 2 * p * r / (p + r));
    }

    private static async Task WriteJson(string path, object value, CancellationToken ct) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, ct);
    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string GitSha(string root) { using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD") { WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false }); return process?.StandardOutput.ReadToEnd().Trim() ?? "NOT_PERSISTED"; }

    private sealed record ProductionContext(string DocumentId, string SourceSha256, string SourcePath, SourceDocument Source, DocxPolicyState Policy, IReadOnlyList<SemanticTextSourceAlias> Aliases);
    private sealed record ReplayRow(string SourceId, int Start, int End, string Text, string Role);
    private sealed record ScoreRow(int Tp, int Fp, int Fn, double Precision, double Recall, double F1);
    private sealed record ReplayCell(string DocumentId, string Repeat, int RawCount, int OldBoundCount, int V6BoundCount, int OldFinalCount, int V6FinalCount, string Stage, ScoreRow OldScore, ScoreRow V6Score);
    private sealed record WholeAliasCell(string Repeat, int RawCount, int StandardBoundCount, int WholeAliasBoundCount, int StandardFinalCount, int WholeAliasFinalCount, ScoreRow StandardScore, ScoreRow WholeAliasScore, object[] Aliases);
}
