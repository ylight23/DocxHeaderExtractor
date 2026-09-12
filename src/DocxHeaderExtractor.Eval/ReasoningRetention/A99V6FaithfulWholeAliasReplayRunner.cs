using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Policy;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Offline counterfactual for the faithful DOCX source. It reuses only the three already-frozen
/// faithful model proposals, ignores their verbatimText, and asks the existing WHOLE_ALIAS binder
/// to take the exact text and span from the selected paragraph alias.
/// </summary>
public static class A99V6FaithfulWholeAliasReplayRunner
{
    private const string OutputRoot = "eval/a99-closed-loop/source-fidelity-whole-alias-replay/DOC-0205";
    private const string FrozenInputRoot = "eval/a99-closed-loop/source-fidelity-paired-live/DOC-0205";
    private const string FaithfulSourcePath = "eval/a99-closed-loop/source-fidelity-audit/DOC-0205/converted-docx/025_ND_47-2020_Chia_se_du_lieu_so.docx";
    private const string FidelityGoldPath = "eval/a99-closed-loop/source-fidelity-audit/DOC-0205/gold-representability.v1.json";
    private const string OccurrenceGoldPath = "eval/a99-closed-loop/strict-gold-occurrence-v1/DOC-0205.occurrence-gold-v1.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(output);
        var startHead = GitSha(repoRoot);
        var context = LoadContext(repoRoot);
        var rows = new List<ReplayRow>();

        await WriteJson(Path.Combine(output, "manifest.v1.json"), new
        {
            schemaVersion = "a99-v6-faithful-whole-alias-replay-manifest-v1",
            documentId = "DOC-0205", sourceSha256 = context.SourceSha256,
            faithfulSourcePath = FaithfulSourcePath, frozenInputRoot = FrozenInputRoot,
            repeats = 3, modelCalls = 0, providerCalls = 0,
            causalDelta = "BINDING_MODE_ONLY",
            control = "FAITHFUL_FROZEN_VERBATIM_TEXT_PROPOSALS",
            challenger = "FAITHFUL_FROZEN_PROPOSALS_WHOLE_ALIAS_BINDING",
            modelTextIgnored = true, modelNumericOffsets = false,
            goldReadBeforeFreeze = false,
            multiParagraphGoldPolicy = "EXCLUDED_FROM_COMPARABLE_66_AND_REPORTED_SEPARATELY",
        }, ct);

        // Phase 1: build and freeze all counterfactual predictions without opening Gold.
        for (var repeat = 1; repeat <= 3; repeat++)
        {
            ct.ThrowIfCancellationRequested();
            var inputPrediction = Path.Combine(repoRoot, FrozenInputRoot.Replace('/', Path.DirectorySeparatorChar),
                $"r{repeat}", "faithful", "prediction.v1.json");
            var inputFreeze = Path.Combine(repoRoot, FrozenInputRoot.Replace('/', Path.DirectorySeparatorChar),
                $"r{repeat}", "faithful", "freeze.v1.json");
            var raw = LoadFrozenProposals(inputPrediction, inputFreeze, context);
            var transformed = raw.Select(ToWholeAlias).ToArray();
            var production = RunProduction(context, transformed);
            var final = ProjectFinal(context, production);
            var directory = Path.Combine(output, $"r{repeat}", "whole-alias");
            Directory.CreateDirectory(directory);
            var predictionPath = Path.Combine(directory, "prediction.v1.json");
            var freezePath = Path.Combine(directory, "freeze.v1.json");
            var rawSha = Sha256Text(JsonSerializer.Serialize(raw, JsonOptions));

            if (File.Exists(predictionPath) || File.Exists(freezePath))
            {
                if (!File.Exists(predictionPath) || !File.Exists(freezePath))
                    throw new InvalidDataException($"PARTIAL_WHOLE_ALIAS_FREEZE:r{repeat}");
                using var existingFreeze = JsonDocument.Parse(await File.ReadAllTextAsync(freezePath, ct));
                var freeze = existingFreeze.RootElement;
                if (freeze.GetProperty("goldReadBeforeFreeze").GetBoolean() ||
                    !string.Equals(freeze.GetProperty("sourceSha256").GetString(), context.SourceSha256, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(freeze.GetProperty("rawProposalSha256").GetString(), rawSha, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(freeze.GetProperty("predictionSha256").GetString(), Sha256(predictionPath), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"WHOLE_ALIAS_FREEZE_LINEAGE_MISMATCH:r{repeat}");
            }
            else
            {
                await WriteJson(predictionPath, new
                {
                    schemaVersion = "a99-v6-faithful-whole-alias-replay-prediction-v1",
                    documentId = "DOC-0205", repeat = $"r{repeat}", mode = "FAITHFUL_WHOLE_ALIAS_COUNTERFACTUAL",
                    sourceSha256 = context.SourceSha256, rawProposalSha256 = rawSha,
                    rawProposalCount = raw.Count, modelCalls = 0, providerCalls = 0,
                    modelTextIgnored = true, proposals = transformed,
                    boundHeadings = production.TextPipeline.BoundHeadings, finalHeadings = final,
                    boundCount = production.TextPipeline.BoundHeadings.Count, finalCount = final.Count,
                    bindingFailure = production.TextPipeline.BindingFailureCount,
                    goldReadBeforeFreeze = false,
                }, ct);
                await WriteJson(freezePath, new
                {
                    schemaVersion = "a99-v6-faithful-whole-alias-replay-freeze-v1",
                    documentId = "DOC-0205", repeat = $"r{repeat}", mode = "FAITHFUL_WHOLE_ALIAS_COUNTERFACTUAL",
                    sourceSha256 = context.SourceSha256, rawProposalSha256 = rawSha,
                    predictionSha256 = Sha256(predictionPath), modelCalls = 0, providerCalls = 0,
                    goldReadBeforeFreeze = false, frozenUtc = DateTimeOffset.UtcNow,
                }, ct);
            }

            rows.Add(new($"r{repeat}", raw.Count, transformed.Length,
                production.TextPipeline.BoundHeadings.Count, final.Count, production.TextPipeline.BindingFailureCount,
                raw.Count(proposal => proposal.IsHeading && !context.Aliases.Any(alias => alias.Alias == proposal.SourceAlias))));
        }

        // Phase 2: only after every counterfactual prediction has been frozen, open comparable Gold.
        var gold = LoadComparableGold(repoRoot);
        var scored = new List<ScoredRow>();
        for (var repeat = 1; repeat <= 3; repeat++)
        {
            var predictionPath = Path.Combine(output, $"r{repeat}", "whole-alias", "prediction.v1.json");
            using var prediction = JsonDocument.Parse(await File.ReadAllTextAsync(predictionPath, ct));
            var final = ParseRows(prediction.RootElement.GetProperty("finalHeadings"));
            var score = ScoreComparable(final, gold.Rows, gold.ExcludedSourceIds);
            var row = rows.Single(item => item.Repeat == $"r{repeat}");
            var scoredRow = new ScoredRow(row, score);
            scored.Add(scoredRow);
            await WriteJson(Path.Combine(output, $"r{repeat}", "score.v1.json"), new
            {
                schemaVersion = "a99-v6-faithful-whole-alias-replay-score-v1",
                documentId = "DOC-0205", repeat = $"r{repeat}",
                comparableGoldCount = gold.Rows.Count, excludedMultiParagraphGold = gold.CrossParagraphCount,
                score, goldReadBeforeFreeze = false,
            }, ct);
        }

        var aggregate = Aggregate(scored.Select(item => item.Score));
        await WriteAliasDiagnosis(repoRoot, output, context, gold, ct);
        await WriteJson(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-v6-faithful-whole-alias-replay-summary-v1",
            documentId = "DOC-0205", startHead, endHead = GitSha(repoRoot),
            sourceSha256 = context.SourceSha256, modelCalls = 0, providerCalls = 0,
            frozenRepeats = 3, comparableGoldCount = gold.Rows.Count,
            faithfulCrossParagraphGold = gold.CrossParagraphCount,
            aggregate, rows = scored,
            diagnostics = new
            {
                selectedAliasCount = rows.Sum(item => item.SelectedAliasCount),
                boundCount = rows.Sum(item => item.BoundCount),
                finalCount = rows.Sum(item => item.FinalCount),
                bindingFailure = rows.Sum(item => item.BindingFailure),
                unknownAliasSelections = rows.Sum(item => item.UnknownAliasSelections),
                modelTextIgnored = true,
            },
            goldFirewall = "PASS",
            decision = aggregate.Tp > 28
                ? "WHOLE_ALIAS_ON_FAITHFUL_SOURCE_RECOVERS_BIND_FAILURES"
                : "WHOLE_ALIAS_ON_FAITHFUL_SOURCE_NO_RECALL_LIFT",
        }, ct);
        Console.WriteLine("MODEL_CALLS=0");
        Console.WriteLine("PROVIDER_CALLS=0");
        Console.WriteLine("GOLD_READ_BEFORE_FREEZE=0");
        Console.WriteLine("FAITHFUL_WHOLE_ALIAS_REPLAY_CELLS=3");
        Console.WriteLine($"FAITHFUL_WHOLE_ALIAS_REPLAY_SUMMARY={Path.Combine(OutputRoot, "summary.v1.json")}");
        return 0;
    }

    private static CanonicalSemanticProposal ToWholeAlias(CanonicalSemanticProposal proposal) => new(
        proposal.SourceAlias, proposal.IsHeading, null,
        SemanticRole: proposal.SemanticRole, StructuralType: proposal.StructuralType, Scope: proposal.Scope,
        RelationHints: proposal.RelationHints, Occurrence: proposal.Occurrence,
        SelectionMode: CanonicalSemanticSelectionMode.WholeAlias);

    private static IReadOnlyList<CanonicalSemanticProposal> LoadFrozenProposals(string predictionPath, string freezePath, SourceContext context)
    {
        if (!File.Exists(predictionPath) || !File.Exists(freezePath)) throw new FileNotFoundException("FAITHFUL_FROZEN_PREDICTION_MISSING", predictionPath);
        using var freeze = JsonDocument.Parse(File.ReadAllText(freezePath));
        var freezeRoot = freeze.RootElement;
        if (freezeRoot.GetProperty("goldReadBeforeFreeze").GetBoolean() ||
            !string.Equals(freezeRoot.GetProperty("sourceSha256").GetString(), context.SourceSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(freezeRoot.GetProperty("predictionSha256").GetString(), Sha256(predictionPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("FAITHFUL_FROZEN_INPUT_LINEAGE_MISMATCH");
        using var prediction = JsonDocument.Parse(File.ReadAllText(predictionPath));
        return JsonSerializer.Deserialize<IReadOnlyList<CanonicalSemanticProposal>>(
            prediction.RootElement.GetProperty("proposals").GetRawText(), JsonOptions) ?? [];
    }

    private static SourceContext LoadContext(string repoRoot)
    {
        var path = Path.Combine(repoRoot, FaithfulSourcePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) throw new InvalidDataException("FAITHFUL_SOURCE_MISSING");
        var source = new OpenXmlDocumentSource().Read(path) with { DocumentId = "DOC-0205" };
        var hash = Sha256(path);
        var features = NumberingStyleFeatures.FromSourceDocument(source);
        var derived = new DocumentFeatureDeriver().Derive(source);
        var policy = DocxPolicyStateBuilder.Build(source, features, derived, new PipelineOptions { DisableLlm = false }.Extraction);
        var aliases = source.Paragraphs.Where(paragraph => !string.IsNullOrWhiteSpace(paragraph.Text))
            .Select((paragraph, index) => new SemanticSourceAlias($"S{index + 1:0000}", paragraph.SourceId, paragraph.SourceOrdinal, paragraph.Text,
                new StructuralSpan(0, paragraph.Text.Length), new SourceAnchor { SourceType = "DOCX_TEXT", ParagraphId = paragraph.SourceId, ParagraphIndex = paragraph.SourceOrdinal }))
            .ToArray();
        var catalog = new DocumentSourceCatalog(aliases.Select(alias => new DocumentSourceUnit(alias.SourceId, alias.SourceOrdinal, alias.Text, alias.SourceAnchor!, alias.SourceSpan)));
        return new(hash, source, policy, catalog, aliases);
    }

    private static CanonicalSemanticProductionResult RunProduction(SourceContext context, IReadOnlyList<CanonicalSemanticProposal> proposals) =>
        CanonicalSemanticProductionEntryPoint.Run(new CanonicalSemanticProductionInput(
            context.Catalog, proposals, context.SourceSha256,
            [new CanonicalSemanticPageEvidence("P0001", true, 0, "DOCX_TEXT")], [], [], [], [], null, null, null, context.SourceSha256, "DOC-0205"));

    private static IReadOnlyList<ExactRow> ProjectFinal(SourceContext context, CanonicalSemanticProductionResult production)
    {
        var proposals = production.TextPipeline.BoundHeadings.Select(item => new ReasoningHeadingProposal
        {
            SourceId = item.SourceId, HeadingSpan = new StructuralSpan(item.Start, item.End), Text = item.Text,
            SemanticRole = item.SemanticRole, Confidence = 1,
        }).ToArray();
        var materialized = ReasoningProposalMaterializer.Materialize(context.Source, context.Policy, proposals);
        return ReasoningTaskProjection.ProjectContentHeadings(materialized.Structure)
            .OrderBy(item => item.Sources.Single().SourceOrdinal).ThenBy(item => item.Sources.Single().Span.Start)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => new ExactRow(item.Sources.Single().SourceId, item.Sources.Single().Span.Start,
                item.Sources.Single().Span.End, item.Text)).ToArray();
    }

    private static GoldScope LoadComparableGold(string repoRoot)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot, FidelityGoldPath.Replace('/', Path.DirectorySeparatorChar))));
        var rows = json.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        var gold = new List<ExactRow>();
        var excludedSourceIds = new HashSet<string>(StringComparer.Ordinal);
        var crossParagraph = 0;
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            var converted = row.GetProperty("converted");
            var status = converted.GetProperty("status").GetString();
            if (status == "EXACT_SINGLE_UNIT")
            {
                var sourceId = converted.GetProperty("sourceIds")[0].GetString()!;
                var text = row.GetProperty("text").GetString()!;
                gold.Add(new(sourceId, 0, text.Length, text));
            }
            else if (status == "EXACT_CONTIGUOUS_MULTI_UNIT")
            {
                crossParagraph++;
                foreach (var sourceId in converted.GetProperty("sourceIds").EnumerateArray())
                    excludedSourceIds.Add(sourceId.GetString()!);
            }
            else throw new InvalidDataException("FAITHFUL_GOLD_REPRESENTABILITY_NOT_EXACT");
        }
        return new(gold, crossParagraph, excludedSourceIds);
    }

    private static async Task WriteAliasDiagnosis(string repoRoot, string output, SourceContext context, GoldScope gold, CancellationToken ct)
    {
        var goldKeys = gold.Rows.Select(row => Key(row.SourceId, row.Start, row.End)).ToHashSet(StringComparer.Ordinal);
        var repeats = new List<object>();
        var tpSets = new List<object[]>();
        var fnSets = new List<object[]>();
        var fpSets = new List<object[]>();
        for (var repeat = 1; repeat <= 3; repeat++)
        {
            var path = Path.Combine(output, $"r{repeat}", "whole-alias", "prediction.v1.json");
            using var prediction = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
            var bound = prediction.RootElement.GetProperty("boundHeadings").EnumerateArray()
                .Select(item => new DiagnosticBound(
                    item.GetProperty("alias").GetString()!, item.GetProperty("sourceId").GetString()!,
                    item.GetProperty("start").GetInt32(), item.GetProperty("end").GetInt32(),
                    item.GetProperty("text").GetString()!, item.GetProperty("semanticRole").GetString()!)).ToArray();
            var selectedKeys = bound.Select(item => Key(item.SourceId, item.Start, item.End)).ToHashSet(StringComparer.Ordinal);
            var tp = bound.Where(item => goldKeys.Contains(Key(item.SourceId, item.Start, item.End)))
                .Select(item => AliasDiagnostic(context.Aliases, item.Alias, item.SourceId, item.Text, item.Role, "TP")).ToArray();
            var fn = gold.Rows.Where(row => !selectedKeys.Contains(Key(row.SourceId, row.Start, row.End)))
                .Select(row => AliasDiagnostic(context.Aliases, AliasFor(context.Aliases, row.SourceId), row.SourceId, row.Text, "NOT_EMITTED", "FN")).ToArray();
            var fp = bound.Where(item => !goldKeys.Contains(Key(item.SourceId, item.Start, item.End)) && !gold.ExcludedSourceIds.Contains(item.SourceId))
                .Select(item => AliasDiagnostic(context.Aliases, item.Alias, item.SourceId, item.Text, item.Role, "HISTORICAL_LANE_FP_UNRESOLVED")).ToArray();
            var ignored = bound.Where(item => gold.ExcludedSourceIds.Contains(item.SourceId))
                .Select(item => AliasDiagnostic(context.Aliases, item.Alias, item.SourceId, item.Text, item.Role, "MULTI_PARAGRAPH_GOLD_EXCLUDED")).ToArray();
            tpSets.Add(tp); fnSets.Add(fn); fpSets.Add(fp);
            repeats.Add(new { repeat = $"r{repeat}", tp, fn, fp, ignored });
        }
        await WriteJson(Path.Combine(output, "alias-selection-diagnosis.v1.json"), new
        {
            schemaVersion = "a99-v6-faithful-whole-alias-diagnosis-v1",
            documentId = "DOC-0205", sourceSha256 = context.SourceSha256,
            comparableGoldCount = gold.Rows.Count, crossParagraphGold = gold.CrossParagraphCount,
            sourceAliasCount = context.Aliases.Count,
            classification = new
            {
                tp = "historical-comparable-occurrence-match",
                fn = "historical-comparable-occurrence-not-selected",
                fp = "not-in-historical-comparable-scope; semantic validity remains UNRESOLVED",
                ignored = "multi-paragraph Gold excluded from comparable-66",
            },
            stableAcrossRepeats = new { tp = StableAliasSet(tpSets), fn = StableAliasSet(fnSets), fp = StableAliasSet(fpSets) },
            repeats,
            modelCalls = 0, providerCalls = 0,
            goldFirewall = "PREDICTIONS_FROZEN_BEFORE_DIAGNOSIS_GOLD_READ",
        }, ct);
    }

    private static object AliasDiagnostic(IReadOnlyList<SemanticSourceAlias> aliases, string alias, string sourceId, string text, string role, string status)
    {
        var index = aliases.Select((item, position) => (item, position)).Single(value => value.item.Alias == alias).position;
        return new
        {
            status, sourceAlias = alias, sourceId, paragraphText = text, semanticRole = role,
            neighbors = Enumerable.Range(Math.Max(0, index - 2), Math.Min(aliases.Count - 1, index + 2) - Math.Max(0, index - 2) + 1)
                .Select(position => new { offset = position - index, aliases[position].Alias, aliases[position].SourceId, paragraphText = aliases[position].Text }).ToArray(),
        };
    }

    private static string AliasFor(IReadOnlyList<SemanticSourceAlias> aliases, string sourceId) =>
        aliases.Single(alias => alias.SourceId == sourceId).Alias;

    private static object StableAliasSet(IEnumerable<object[]> rows)
    {
        var sets = rows.Select(row => row.Select(item => JsonSerializer.Serialize(item, JsonOptions)).ToHashSet(StringComparer.Ordinal)).ToArray();
        return new { sameAcrossRepeats = sets.Length > 0 && sets.All(set => set.SetEquals(sets[0])), count = sets.Length == 0 ? 0 : sets[0].Count };
    }

    private static ScoreRow ScoreComparable(IReadOnlyList<ExactRow> prediction, IReadOnlyList<ExactRow> gold, IReadOnlySet<string> excludedSourceIds)
    {
        var p = prediction.Where(row => !excludedSourceIds.Contains(row.SourceId)).Select(row => Key(row.SourceId, row.Start, row.End)).ToHashSet(StringComparer.Ordinal);
        var g = gold.Select(row => Key(row.SourceId, row.Start, row.End)).ToHashSet(StringComparer.Ordinal);
        var tp = p.Intersect(g).Count(); var fp = p.Except(g).Count(); var fn = g.Except(p).Count();
        var precision = tp + fp == 0 ? 0d : (double)tp / (tp + fp); var recall = tp + fn == 0 ? 0d : (double)tp / (tp + fn);
        return new(tp, fp, fn, precision, recall, precision + recall == 0 ? 0d : 2 * precision * recall / (precision + recall),
            prediction.Where(row => excludedSourceIds.Contains(row.SourceId)).Select(row => Key(row.SourceId, row.Start, row.End)).Distinct(StringComparer.Ordinal).Count());
    }

    private static ScoreRow Aggregate(IEnumerable<ScoreRow> scores)
    {
        var rows = scores.ToArray(); var tp = rows.Sum(item => item.Tp); var fp = rows.Sum(item => item.Fp); var fn = rows.Sum(item => item.Fn);
        var p = tp + fp == 0 ? 0d : (double)tp / (tp + fp); var r = tp + fn == 0 ? 0d : (double)tp / (tp + fn);
        return new(tp, fp, fn, p, r, p + r == 0 ? 0d : 2 * p * r / (p + r), rows.Sum(item => item.ExcludedPredictions));
    }

    private static ExactRow[] ParseRows(JsonElement array) => array.EnumerateArray().Select(item =>
        new ExactRow(item.GetProperty("sourceId").GetString()!, item.GetProperty("start").GetInt32(), item.GetProperty("end").GetInt32(), item.GetProperty("text").GetString()!)).ToArray();

    private static string Key(string sourceId, int start, int end) => $"{sourceId}:{start}:{end}";
    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string GitSha(string root) { using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD") { WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false }); return process?.StandardOutput.ReadToEnd().Trim() ?? "NOT_PERSISTED"; }
    private static async Task WriteJson(string path, object value, CancellationToken ct) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, new UTF8Encoding(false), ct);

    private sealed record SourceContext(string SourceSha256, SourceDocument Source, DocxPolicyState Policy, DocumentSourceCatalog Catalog, IReadOnlyList<SemanticSourceAlias> Aliases);
    private sealed record ExactRow(string SourceId, int Start, int End, string Text);
    private sealed record GoldScope(IReadOnlyList<ExactRow> Rows, int CrossParagraphCount, IReadOnlySet<string> ExcludedSourceIds);
    private sealed record ScoreRow(int Tp, int Fp, int Fn, double Precision, double Recall, double F1, int ExcludedPredictions);
    private sealed record ReplayRow(string Repeat, int RawProposalCount, int SelectedAliasCount, int BoundCount, int FinalCount, int BindingFailure, int UnknownAliasSelections);
    private sealed record ScoredRow(ReplayRow Replay, ScoreRow Score);
    private sealed record DiagnosticBound(string Alias, string SourceId, int Start, int End, string Text, string Role);
}
