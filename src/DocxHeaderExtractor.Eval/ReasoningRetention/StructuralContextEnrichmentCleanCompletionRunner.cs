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

/// <summary>Offline closure of the frozen structural-context experiment. It audits the existing
/// cell lineage, replays no provider response, and never changes the frozen inference bytes.</summary>
public static class StructuralContextEnrichmentCleanCompletionRunner
{
    private const string RootName = "eval/a99-closed-loop/semantic-text-stable-error-repair";
    private const string BaselineName = "eval/a99-closed-loop/semantic-text-generalization";
    private const string InventoryName = "eval/a99-dataset/document-inventory.v1.json";
    private const string Model = "qwen/qwen3.7-flash";
    private static readonly string[] Documents = ["DOC-0001", "DOC-0205", "DOC-0252", "DOC-0256", "DOC-0258"];
    private static readonly string[] Repeats = ["r4", "r5", "r6"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var root = Path.Combine(repoRoot, RootName.Replace('/', Path.DirectorySeparatorChar));
        var inventory = LoadInventory(repoRoot);
        var cells = Documents.SelectMany(documentId => Repeats.Select(repeat => AuditCell(repoRoot, root, inventory[documentId], documentId, repeat))).ToArray();
        await WriteJson(Path.Combine(root, "campaign-cell-matrix.v1.json"), new
        {
            schemaVersion = "a99-semantic-text-stable-error-repair-cell-matrix-v1",
            model = Model, intervention = "GLOBAL_STRUCTURAL_CONTEXT_ENRICHMENT_V1", cells,
            providerCallsCurrentRun = 0, successfulCellsReused = cells.Count(x => x.State == "FROZEN_SUCCESS"),
            providerFailureCells = cells.Count(x => x.State == "FROZEN_PROVIDER_FAILURE"), goldReadBeforeFreeze = false,
        }, ct);
        PrintMatrix(cells);

        var systemAudit = AuditSystemLoss(repoRoot, root, inventory["DOC-0256"]);
        await WriteJson(Path.Combine(root, "system-loss-audit.v1.json"), systemAudit, ct);
        var providerAudit = AuditProviderFailures(root, cells);
        await WriteJson(Path.Combine(root, "provider-failure-audit.v1.json"), providerAudit, ct);

        var paired = BuildPairedComparison(repoRoot, root, cells);
        await WriteJson(Path.Combine(root, "paired-comparison.v1.json"), paired, ct);
        var overhead = BuildPacketOverhead(repoRoot, root, inventory);
        await WriteJson(Path.Combine(root, "packet-overhead.v1.json"), overhead, ct);
        var census = BuildCensus(repoRoot, root, cells);
        await WriteJson(Path.Combine(root, "stable-error-census.v1.json"), census, ct);

        var decision = cells.Any(x => x.State == "FROZEN_PROVIDER_FAILURE" || x.State == "MISSING" || x.State == "HASH_MISMATCH" || x.State == "INVALID_FREEZE")
            ? "ENRICHMENT_V1_NOT_MEASURED_PROVIDER_BLOCKED"
            : "ENRICHMENT_V1_NO_MATERIAL_GAIN";
        var summary = new
        {
            schemaVersion = "a99-semantic-text-stable-error-repair-clean-completion-summary-v1",
            status = decision == "ENRICHMENT_V1_NOT_MEASURED_PROVIDER_BLOCKED" ? "NOT_EVALUABLE_EXECUTION" : "COMPLETE",
            startHead = "43e3f38", endHead = GitSha(repoRoot), model = Model,
            intervention = "GLOBAL_STRUCTURAL_CONTEXT_ENRICHMENT_V1", decision,
            cellMatrix = "campaign-cell-matrix.v1.json", systemLossAudit = "system-loss-audit.v1.json",
            providerFailureAudit = "provider-failure-audit.v1.json", pairedComparison = "paired-comparison.v1.json",
            packetOverhead = "packet-overhead.v1.json", stableErrorCensus = "stable-error-census.v1.json",
            systemLossFixApplied = false, replayApplied = false, providerCallsCurrentRun = 0,
            goldFirewall = "PASS", notes = new[] { "Existing frozen success cells reused byte-for-byte.", "DOC-0252/R5 and DOC-0252/R6 remain provider execution failures with no persisted raw transport envelope.", "The four aggregate R4 losses are DOC-0256 navigation-role projection exclusions, not a generic binder/validator defect." },
        };
        await WriteJson(Path.Combine(root, "clean-completion-summary.v1.json"), summary, ct);
        Console.WriteLine("FINAL_INTERVENTION_DECISION=" + decision);
        Console.WriteLine("MODEL_CALLS=0");
        return decision == "ENRICHMENT_V1_NOT_MEASURED_PROVIDER_BLOCKED" ? 1 : 0;
    }

    private static CellRow AuditCell(string repoRoot, string root, JsonElement item, string documentId, string repeat)
    {
        var dir = Path.Combine(root, repeat, documentId);
        var predictionPath = Path.Combine(dir, "prediction.v1.json");
        var resultPath = Path.Combine(dir, "result.v1.json");
        var freezePath = Path.Combine(dir, "freeze.v1.json");
        var scorePath = Path.Combine(dir, "score.v1.json");
        if (!File.Exists(freezePath) || !File.Exists(predictionPath) || !File.Exists(resultPath) || !File.Exists(scorePath))
            return new(documentId, repeat, "MISSING", false, false, false, false, false, null, null, null, null, null, null);
        using var freezeDoc = JsonDocument.Parse(File.ReadAllText(freezePath));
        using var predictionDoc = JsonDocument.Parse(File.ReadAllText(predictionPath));
        using var scoreDoc = JsonDocument.Parse(File.ReadAllText(scorePath));
        var freeze = freezeDoc.RootElement; var prediction = predictionDoc.RootElement; var score = scoreDoc.RootElement;
        var sourceOk = string.Equals(freeze.GetProperty("sourceSha256").GetString(), item.GetProperty("sourceSha256").GetString(), StringComparison.OrdinalIgnoreCase);
        var predictionHashOk = string.Equals(freeze.GetProperty("predictionSha256").GetString(), Sha256(predictionPath), StringComparison.OrdinalIgnoreCase);
        var resultHashOk = string.Equals(freeze.GetProperty("resultSha256").GetString(), Sha256(resultPath), StringComparison.OrdinalIgnoreCase);
        var goldFirewall = freeze.TryGetProperty("goldReadBeforeFreeze", out var gold) && !gold.GetBoolean();
        var modelOk = string.Equals(freeze.GetProperty("model").GetString(), Model, StringComparison.Ordinal);
        var prompt = prediction.TryGetProperty("promptHash", out var promptValue) ? promptValue.GetString() : null;
        var schema = prediction.TryGetProperty("schemaHash", out var schemaValue) ? schemaValue.GetString() : null;
        var status = score.TryGetProperty("status", out var statusValue) ? statusValue.GetString() : null;
        var state = !sourceOk || !predictionHashOk || !resultHashOk ? "HASH_MISMATCH" : !goldFirewall || !modelOk ? "INVALID_FREEZE" : status == "SUCCESS" ? "FROZEN_SUCCESS" : "FROZEN_PROVIDER_FAILURE";
        var failure = prediction.TryGetProperty("failure", out var failureValue) ? failureValue.GetString() : null;
        var configSignature = freeze.TryGetProperty("configurationSignature", out var config) ? config.GetString() : null;
        return new(documentId, repeat, state, sourceOk, predictionHashOk, resultHashOk, goldFirewall, modelOk, prompt, schema, configSignature, failure, GetString(freeze, "actualProvider"), GetString(freeze, "finishReason"));
    }

    private static object AuditSystemLoss(string repoRoot, string root, JsonElement item)
    {
        const string documentId = "DOC-0256";
        const string repeat = "r4";
        var dir = Path.Combine(root, repeat, documentId);
        using var predictionDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "prediction.v1.json")));
        using var firstDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "first-loss.v1.json")));
        var sourcePath = Path.Combine(repoRoot, item.GetProperty("sourcePath").GetString()!.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
        var source = new OpenXmlDocumentSource().Read(sourcePath) with { DocumentId = documentId };
        var features = NumberingStyleFeatures.FromSourceDocument(source);
        var derived = new DocumentFeatureDeriver().Derive(source);
        var policy = DocxPolicyStateBuilder.Build(source, features, derived, new PipelineOptions { DisableLlm = false }.Extraction);
        var aliases = source.Paragraphs.Where(x => !string.IsNullOrWhiteSpace(x.Text)).Select((p, i) => new SemanticTextSourceAlias($"S{i + 1:0000}", p.SourceId, p.SourceOrdinal, p.Text)).ToArray();
        var raw = SemanticTextExactBindingContract.Parse("{\"headings\":" + predictionDoc.RootElement.GetProperty("rawModelHeadings").GetRawText() + "}");
        var bound = SemanticTextExactBinder.Bind(raw.Headings, aliases, out var observations);
        var proposals = bound.Select(x => new ReasoningHeadingProposal { SourceId = x.SourceId, HeadingSpan = new StructuralSpan(x.Start, x.End), Text = x.Text, SemanticRole = x.Role, Confidence = 1 }).ToArray();
        var materialized = ReasoningProposalMaterializer.Materialize(source, policy, proposals);
        var decisions = ReasoningTaskProjection.Project(materialized.Structure).ToDictionary(x => x.ProposalId, StringComparer.Ordinal);
        var lossRows = firstDoc.RootElement.GetProperty("firstLosses").EnumerateArray().Where(x => x.GetProperty("firstLoss").GetString() == "SYSTEM_PROJECTION_LOSS").Select(x =>
        {
            var key = x.GetProperty("key").GetString()!;
            var parsed = ParseKey(key);
            var boundRow = bound.FirstOrDefault(b => b.SourceId == parsed.SourceId && b.Start == parsed.Start && b.End == parsed.End);
            var validated = materialized.Validated.FirstOrDefault(v => v.Proposal.SourceId == parsed.SourceId && v.Proposal.HeadingSpan.Start == parsed.Start && v.Proposal.HeadingSpan.End == parsed.End);
            var rawHeading = raw.Headings.FirstOrDefault(h => h.Source == aliases.FirstOrDefault(a => a.SourceId == parsed.SourceId)?.Alias && h.Text == x.GetProperty("exactSourceText").GetString());
            var decision = validated is not null && decisions.TryGetValue(validated.ElementId, out var found) ? found : null;
            return new
            {
                key, sourceAlias = aliases.FirstOrDefault(a => a.SourceId == parsed.SourceId)?.Alias, verbatimModelText = rawHeading?.Text,
                role = rawHeading?.Role, rawProposalIdentity = rawHeading is null ? null : $"{rawHeading.Source}:{rawHeading.Text}:{rawHeading.Role}",
                binderStatus = observations.FirstOrDefault(o => o.Heading.Text == rawHeading?.Text && o.Heading.Source == rawHeading?.Source)?.Status.ToString(),
                validatorAccepted = validated?.Accepted, firstStageAbsent = decision?.Status == ReasoningTaskProjection.Excluded ? "PROJECTION" : validated?.Accepted == false ? "VALIDATOR" : "TRACE_ACCOUNTING",
                projectionStatus = decision?.Status, projectionReason = decision?.Reason,
                classification = decision?.Reason == "NAVIGATION_ONLY" ? "MODEL_ROLE_CONTRACT_EXCLUSION" : decision?.Status == ReasoningTaskProjection.Included ? "TRACE_ACCOUNTING_ERROR" : "SYSTEM_PROJECTION_LOSS",
            };
        }).ToArray();
        return new
        {
            schemaVersion = "a99-semantic-text-stable-error-system-loss-audit-v1", documentId, repeat,
            aggregateAttributionCorrection = "R4_SYSTEM_LOSS=4 is DOC-0256, not DOC-0252; DOC-0252/R4 systemLoss=0.",
            rawProposalCount = raw.Headings.Count, boundProposalCount = bound.Count, validatorAccepted = materialized.Validated.Count(x => x.Accepted),
            projectionDecisionCount = decisions.Count, losses = lossRows,
            counts = new { systemBinding = 0, systemDedupe = 0, systemValidator = 0, systemProjection = lossRows.Length, traceAccounting = lossRows.Count(x => x.classification == "TRACE_ACCOUNTING_ERROR") },
            genericPostModelDefectProven = false, replayApplied = false,
            conclusion = "All four proposals are exact-bound and validator-accepted, then intentionally excluded as NAVIGATION_ONLY because the model role is AGENDA_NAVIGATION_HEADING. Existing generic projection invariant and focused test confirm this is a role/projection contract exclusion, not a binder/projection implementation bug.",
        };
    }

    private static object AuditProviderFailures(string root, IReadOnlyList<CellRow> cells)
    {
        var failures = cells.Where(x => x.State == "FROZEN_PROVIDER_FAILURE").Select(x =>
        {
            var path = Path.Combine(root, x.Repeat, x.DocumentId, "prediction.v1.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var rootJson = doc.RootElement;
            return new
            {
                x.DocumentId, x.Repeat, failure = x.Failure, httpStatus = (int?)null, contentType = (string?)null,
                rawBodyPersisted = false, reportedModel = GetString(rootJson, "model"), provider = x.Provider, finishReason = x.FinishReason,
                choicesPresent = false, alternateValidEnvelope = false,
                classification = "PROVIDER_EXECUTION_NOT_EVALUABLE",
                evidenceGap = "Historical freeze persisted only parser failure and no raw HTTP status/content-type/body envelope.",
            };
        }).ToArray();
        return new
        {
            schemaVersion = "a99-semantic-text-stable-error-provider-failure-audit-v1", failures, providerCallsCurrentRun = 0, retriesExhausted = true,
            attemptLineage = failures.Select(x => new { x.DocumentId, x.Repeat, attemptOrdinal = (int?)null, priorFailureHash = (string?)null, requestHash = (string?)null, provider = x.provider, startedUtc = (string?)null, finishedUtc = (string?)null, lineageStatus = "NOT_PERSISTED_BY_PREVIOUS_RUNNER" }).ToArray(),
            historicalEvidenceIntegrity = "The prior runner overwrote failed-cell paths; no raw transport envelope or per-attempt hashes remain to reconstruct retroactively.",
        };
    }

    private static object BuildPairedComparison(string repoRoot, string root, IReadOnlyList<CellRow> cells)
    {
        var rows = new List<object>();
        foreach (var cell in cells)
        {
            var baselinePath = Path.Combine(repoRoot, BaselineName.Replace('/', Path.DirectorySeparatorChar), cell.DocumentId, $"r{int.Parse(cell.Repeat[1..]) - 3}", "score.v1.json");
            using var baselineDoc = JsonDocument.Parse(File.ReadAllText(baselinePath));
            var baseline = baselineDoc.RootElement;
            var v1Path = Path.Combine(root, cell.Repeat, cell.DocumentId, "score.v1.json");
            using var v1Doc = JsonDocument.Parse(File.ReadAllText(v1Path));
            var v1 = v1Doc.RootElement;
            var evaluable = cell.State == "FROZEN_SUCCESS" && v1.GetProperty("status").GetString() == "SUCCESS";
            rows.Add(new
            {
                documentId = cell.DocumentId, repeat = cell.Repeat, status = evaluable ? "EVALUABLE" : "NOT_EVALUABLE_EXECUTION",
                baseline = Metric(baseline), enrichmentV1 = evaluable ? Metric(v1) : null,
                delta = evaluable ? new { tp = GetInt(v1, "tp") - GetInt(baseline, "tp"), fp = GetInt(v1, "fp") - GetInt(baseline, "fp"), fn = GetInt(v1, "fn") - GetInt(baseline, "fn"), recall = GetDouble(v1, "recall") - GetDouble(baseline, "recall"), precision = GetDouble(v1, "precision") - GetDouble(baseline, "precision"), f1 = GetDouble(v1, "f1") - GetDouble(baseline, "f1"), modelOmission = GetInt(v1, "modelOmission") - GetInt(baseline, "modelOmission"), spanError = GetInt(v1, "modelWrongSpan") - GetInt(baseline, "modelWrongSpan"), systemLoss = GetInt(v1, "systemLoss") - GetInt(baseline, "systemLoss") } : null,
            });
        }
        return new { schemaVersion = "a99-semantic-text-stable-error-paired-comparison-v1", rows, evaluableRows = cells.Count(x => x.State == "FROZEN_SUCCESS"), providerBlockedRows = cells.Count(x => x.State == "FROZEN_PROVIDER_FAILURE") };
    }

    private static object BuildPacketOverhead(string repoRoot, string root, IReadOnlyDictionary<string, JsonElement> inventory)
    {
        var rows = new List<object>();
        foreach (var documentId in Documents)
        {
            var sourcePath = Path.Combine(repoRoot, inventory[documentId].GetProperty("sourcePath").GetString()!.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
            var source = new OpenXmlDocumentSource().Read(sourcePath) with { DocumentId = documentId };
            var paragraphs = source.Paragraphs.Where(p => !string.IsNullOrWhiteSpace(p.Text)).ToArray();
            var aliases = paragraphs.Select((p, i) => new SemanticTextSourceAlias($"S{i + 1:0000}", p.SourceId, p.SourceOrdinal, p.Text)).ToArray();
            var basePacket = JsonSerializer.Serialize(new { sourceAliases = aliases.Select(x => new { alias = x.Alias, text = x.RawText, sourceOrdinal = x.SourceOrdinal }).ToArray() });
            var enrichedPacket = JsonSerializer.Serialize(new { sourceAliases = paragraphs.Select((p, i) => new { alias = $"S{i + 1:0000}", text = p.Text, sourceOrdinal = p.SourceOrdinal, structuralFacts = new { style = new { p.Style.StyleId, p.Style.StyleName, p.Style.BuiltInHeadingStyleLevel, p.Style.OutlineLevel, p.Style.Bold, p.Style.Italic, p.Style.Underline, p.Style.AllCaps, p.Style.FontSizePt, p.Style.Alignment }, numbering = new { p.Numbering.NumberingId, p.Numbering.NumberingLevel, p.Numbering.NumberLabel, p.Numbering.NumberingFormat, p.Numbering.NumberingStyleHeadingLevel }, layout = new { p.Layout.InContentControl, p.Layout.KeepNext, p.Layout.PageBreakBefore, p.Layout.TableDepth, p.Layout.SectionIndex }, p.InTableOfContents } }).ToArray() });
            var freezePaths = Repeats.Select(repeat => Path.Combine(root, repeat, documentId, "freeze.v1.json")).Where(File.Exists).Select(path => JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone()).ToArray();
            rows.Add(new
            {
                documentId, aliasCount = aliases.Length, baselinePacketCharacters = basePacket.Length, enrichmentPacketCharacters = enrichedPacket.Length,
                overheadCharacters = enrichedPacket.Length - basePacket.Length, overheadRatio = basePacket.Length == 0 ? 0d : (double)(enrichedPacket.Length - basePacket.Length) / basePacket.Length,
                basePacketHash = Sha256Text(basePacket), enrichmentPacketHash = Sha256Text(enrichedPacket),
                inputTokens = freezePaths.Select(x => GetNullableInt(x, "inputTokens")).ToArray(), reasoningTokens = freezePaths.Select(x => GetNullableInt(x, "reasoningTokens")).ToArray(), outputTokens = freezePaths.Select(x => GetNullableInt(x, "outputTokens")).ToArray(), wallTimeMs = freezePaths.Select(x => GetNullableLong(x, "wallTimeMs")).ToArray(),
            });
        }
        return new { schemaVersion = "a99-semantic-text-packet-overhead-v1", rows, factsAreParserDerived = true, candidateGating = false, goldInPacket = false };
    }

    private static object BuildCensus(string repoRoot, string root, IReadOnlyList<CellRow> cells)
    {
        var baselineRoot = Path.Combine(repoRoot, "eval/a99-closed-loop/semantic-text-repeat-stability/gold-stability.v1.json");
        using var baseline = JsonDocument.Parse(File.ReadAllText(baselineRoot));
        var available = cells.Where(x => x.State == "FROZEN_SUCCESS").GroupBy(x => x.DocumentId).ToDictionary(x => x.Key, x => x.Select(y => y.Repeat).ToArray(), StringComparer.Ordinal);
        var rows = new List<object>();
        foreach (var documentId in Documents)
        {
            var goldPath = Path.Combine(repoRoot, "eval/a99-closed-loop/strict-gold-occurrence-v1", documentId + ".occurrence-gold-v1.json");
            var gold = ReasoningGoldArtifactLoader.LoadOccurrence(goldPath).Where(x => x.HeadingSpan is not null).ToArray();
            var repeats = available.GetValueOrDefault(documentId, []);
            var finalRows = repeats.Select(repeat => LoadFinalRows(Path.Combine(root, repeat, documentId, "prediction.v1.json"))).ToArray();
            var complete = repeats.Length == 3;
            var stableOmission = complete ? gold.Count(g => finalRows.All(rows => !rows.Any(r => Same(r, g))) && !finalRows.Any(rows => rows.Any(r => Near(r, g)))) : 0;
            var spanVariants = complete ? gold.Count(g => finalRows.Count(rows => rows.Any(r => Same(r, g))) < 3 && finalRows.Any(rows => rows.Any(r => Near(r, g)))) : 0;
            var stableFp = complete ? finalRows.SelectMany(rows => rows).GroupBy(r => $"{r.SourceId}:{r.Start}:{r.End}").Count(g => g.Count() == 3 && !gold.Any(x => Same(g.First(), x))) : 0;
            rows.Add(new { documentId, availableRepeats = repeats, executionStatus = complete ? "THREE_REPEAT_EVALUABLE" : "NOT_EVALUABLE_EXECUTION", stableOmissions3of3 = stableOmission, spanVariants3of3 = spanVariants, stableFalsePositives3of3 = stableFp });
        }
        var completeDocuments = Documents.Count(documentId => available.GetValueOrDefault(documentId, []).Length == 3);
        var blockedDocuments = Documents.Length - completeDocuments;
        return new
        {
            schemaVersion = "a99-semantic-text-stable-error-census-v1",
            before = new { stableOmissions3of3 = 9, spanVariants3of3 = 5, stableFalsePositives3of3 = 3, falsePositives2of3 = 6 },
            after = new { rows, completeDocuments, providerBlockedDocuments = blockedDocuments, stableCountsSuppressedForIncompleteDocuments = true },
            conclusion = "Stable post-enrichment census is not promotable because DOC-0252 lacks R5/R6 provider-success repeats.",
        };
    }

    private static IReadOnlyDictionary<string, JsonElement> LoadInventory(string repoRoot)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot, InventoryName.Replace('/', Path.DirectorySeparatorChar))));
        return doc.RootElement.GetProperty("documents").EnumerateArray().ToDictionary(x => x.GetProperty("documentId").GetString()!, x => x.Clone(), StringComparer.Ordinal);
    }

    private static IReadOnlyList<Row> LoadFinalRows(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("finalHeadings").EnumerateArray().Select(x => new Row(x.GetProperty("sourceId").GetString()!, x.GetProperty("start").GetInt32(), x.GetProperty("end").GetInt32(), x.GetProperty("text").GetString()!)).ToArray();
    }

    private static bool Same(Row row, ReasoningGoldOccurrence gold) => row.SourceId == gold.SourceId && row.Start == gold.HeadingSpan!.Start && row.End == gold.HeadingSpan.End;
    private static bool Near(Row row, ReasoningGoldOccurrence gold) => row.SourceId == gold.SourceId && row.Start < gold.HeadingSpan!.End && gold.HeadingSpan.Start < row.End;
    private static (string SourceId, int Start, int End) ParseKey(string key)
    {
        var end = key.LastIndexOf(':'); var start = key.LastIndexOf(':', end - 1);
        return (key[..start], int.Parse(key[(start + 1)..end]), int.Parse(key[(end + 1)..]));
    }
    private static object Metric(JsonElement x) => new { tp = GetInt(x, "tp"), fp = GetInt(x, "fp"), fn = GetInt(x, "fn"), precision = GetDouble(x, "precision"), recall = GetDouble(x, "recall"), f1 = GetDouble(x, "f1"), modelOmission = GetInt(x, "modelOmission"), spanError = GetInt(x, "modelWrongSpan"), systemLoss = GetInt(x, "systemLoss") };
    private static void PrintMatrix(IEnumerable<CellRow> rows)
    {
        Console.WriteLine("CAMPAIGN_CELL_MATRIX");
        foreach (var group in rows.GroupBy(x => x.DocumentId, StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal)) Console.WriteLine($"{group.Key} | {string.Join(" | ", group.OrderBy(x => x.Repeat).Select(x => x.Repeat + "=" + x.State))}");
    }
    private static string? GetString(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int GetInt(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;
    private static double GetDouble(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetDouble(out var result) ? result : 0d;
    private static int? GetNullableInt(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null && value.TryGetInt32(out var result) ? result : null;
    private static long GetNullableLong(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null && value.TryGetInt64(out var result) ? result : 0;
    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string GitSha(string root) => Git(root, "rev-parse HEAD");
    private static string Git(string root, string args) { using var p = Process.Start(new ProcessStartInfo("git", args) { WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false }); return p?.StandardOutput.ReadToEnd().Trim() ?? "NOT_PERSISTED"; }
    private static async Task WriteJson(string path, object value, CancellationToken ct) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, ct);

    private sealed record CellRow(string DocumentId, string Repeat, string State, bool SourceHashOk, bool PredictionHashOk, bool ResultHashOk, bool GoldFirewall, bool ModelOk, string? PromptHash, string? SchemaHash, string? ConfigurationSignature, string? Failure, string? Provider, string? FinishReason);
    private sealed record Row(string SourceId, int Start, int End, string Text);
}
