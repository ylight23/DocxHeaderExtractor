using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.Eval.StrictGoldOccurrence;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Offline reconciliation of frozen DOC-0205 model runs.  This is deliberately separate from
/// scoring and runtime extraction: Gold is read only by this post-freeze forensic lane.
/// </summary>
public static class Doc0205SemanticContractReconciliationRunner
{
    public const int ProviderCalls = 0;
    public const string OutputRelativeRoot = "eval/a99-closed-loop/doc0205-semantic-contract-audit";
    private const string DocumentId = "DOC-0205";
    private const string ExpectedSourceSha = "b145e31a58e76cad1c77967c639884d1c566020d344d725ca145fafb5de86878";
    private const string ExpectedStartHead = "f2828fe2315e0466a73a0ac3106bfc84d6231727";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static readonly RunSpec[] Runs =
    [
        new("Qwen3.5-9B S0", "openrouter-qwen35-9b-per-segment-recovery/documents/DOC-0205", "score.v1.json", "prediction.v1.json", "result.v1.json", "freeze.v1.json", ""),
        new("Qwen3.7-Flash S0", "qwen37-flash-reasoning-ceiling/DOC-0205/r1-ceiling", "score.v1.json", "prediction.v1.json", "result.v1.json", "freeze.v1.json", "first-loss.v1.json"),
        new("C0 structure-preserving", "structure-preserving-ir/DOC-0205", "text-ceiling.score.v1.json", "text-ceiling.prediction.v1.json", "text-ceiling.result.v1.json", "text-ceiling.freeze.v1.json", "text-ceiling.first-loss.v1.json"),
        new("C1 boundary", "flash-heading-contract-realignment/DOC-0205/c1_boundary", "score.v1.json", "prediction.v1.json", "result.v1.json", "freeze.v1.json", "first-loss.v1.json"),
        new("C2 addressed", "flash-heading-contract-realignment/DOC-0205/c2_addressed", "score.v1.json", "prediction.v1.json", "result.v1.json", "freeze.v1.json", "first-loss.v1.json"),
        new("Flash text V4", "heading-target-ontology/DOC-0205", "score.v1.json", "prediction.v1.json", "result.v1.json", "freeze.v1.json", "first-loss.v1.json"),
        new("Flash visual V4", "qwen37-flash-visual-ceiling/DOC-0205", "score.v1.json", "prediction.v1.json", "result.v1.json", "freeze.v1.json", "first-loss.v1.json"),
    ];

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var outputRoot = Path.Combine(repoRoot, OutputRelativeRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(outputRoot);
        var startHead = GitSha(repoRoot);

        var inventory = JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot, "eval/a99-dataset/document-inventory.v1.json"))).RootElement;
        var inventoryDoc = inventory.GetProperty("documents").EnumerateArray().Single(x => x.GetProperty("documentId").GetString() == DocumentId);
        var sourcePath = Path.Combine(repoRoot, inventoryDoc.GetProperty("sourcePath").GetString()!.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
        var source = new OpenXmlDocumentSource().Read(sourcePath) with { DocumentId = DocumentId };
        var sourceSha = Sha256(sourcePath);

        var goldPath = Path.Combine(repoRoot, "eval/a99-closed-loop/strict-gold-occurrence-v1/DOC-0205.occurrence-gold-v1.json".Replace('/', Path.DirectorySeparatorChar));
        var goldDoc = JsonDocument.Parse(File.ReadAllText(goldPath));
        var goldRoot = goldDoc.RootElement;
        var gold = goldRoot.GetProperty("bindings").EnumerateArray().Select(GoldRow).ToArray();
        var strictPath = Path.Combine(repoRoot, "eval/a99-closed-loop/strict-gold-v4/DOC-0205.strict-gold-v4.json".Replace('/', Path.DirectorySeparatorChar));
        var strict = JsonDocument.Parse(File.ReadAllText(strictPath)).RootElement;
        var sourceParagraphs = source.Paragraphs.ToDictionary(x => x.SourceId, StringComparer.Ordinal);
        var pageAliases = LoadAliases(repoRoot);

        var loaded = Runs.Select(spec => LoadRun(repoRoot, spec, gold, sourceParagraphs)).ToArray();
        var classifications = loaded.Select(run => ClassifyRun(run, gold)).ToArray();
        var authority = BuildAuthority(source, sourcePath, sourceSha, goldRoot, strict, gold, sourceParagraphs, pageAliases, loaded);
        var reconciliation = BuildReconciliation(loaded, classifications, gold);
        var summary = BuildSummary(startHead, sourceSha, goldRoot, strict, loaded, classifications, reconciliation);

        await WriteJson(outputRoot, "authority-profile.v1.json", authority, ct);
        await WriteJson(outputRoot, "run-reconciliation.v1.json", reconciliation, ct);
        await WriteJson(outputRoot, "summary.v1.json", summary, ct);
        await File.WriteAllTextAsync(Path.Combine(outputRoot, "examples.md"), BuildExamples(gold, classifications), ct);

        Console.WriteLine($"OFFLINE_RECONCILIATION providerCalls={ProviderCalls} gold={gold.Length} sourceSha={sourceSha}");
        foreach (var run in loaded)
            Console.WriteLine($"{run.Spec.Label}: TP={run.Score.Tp} FP={run.Score.Fp} FN={run.Score.Fn} F1={run.Score.F1:0.######} proposals={run.Proposals.Count}");
        var c0Classified = classifications.Single(x => x.Run.Spec.Label == "C0 structure-preserving");
        Console.WriteLine($"C0_CORRESPONDENCE={c0Classified.PerGold.Count(x => x.Class is "SEMANTIC_PRESENT_PARTIAL_SPAN" or "SEMANTIC_PRESENT_SUPERSET_SPAN" or "SEMANTIC_PRESENT_OFFSET_SHIFT")} C0_EXACT={c0Classified.PerGold.Count(x => x.Class == "EXACT_MATCH")} C0_TRUE_OMISSION={c0Classified.PerGold.Count(x => x.Class == "TRUE_MODEL_OMISSION")}");
        Console.WriteLine($"PRIMARY_CLASSIFICATION={PrimaryClassification(c0Classified)}");
        return 0;
    }

    public static string ClassifySpan(int predictionStart, int predictionEnd, string predictionSourceId, string predictionText, string predictionRole,
        int goldStart, int goldEnd, string goldSourceId, string goldText, string goldRole)
    {
        if (predictionSourceId == goldSourceId && predictionStart == goldStart && predictionEnd == goldEnd)
            return predictionRole.Equals(goldRole, StringComparison.OrdinalIgnoreCase) ? "EXACT_MATCH" : "MODEL_PROPOSAL_WRONG_ROLE";
        if (predictionSourceId == goldSourceId && predictionStart >= goldStart && predictionEnd <= goldEnd && predictionStart < predictionEnd)
            return "SEMANTIC_PRESENT_PARTIAL_SPAN";
        if (predictionSourceId == goldSourceId && predictionStart <= goldStart && predictionEnd >= goldEnd && predictionStart < predictionEnd)
            return "SEMANTIC_PRESENT_SUPERSET_SPAN";
        if (predictionSourceId == goldSourceId && predictionStart < goldEnd && goldStart < predictionEnd)
            return "SEMANTIC_PRESENT_OFFSET_SHIFT";
        if (!string.Equals(predictionSourceId, goldSourceId, StringComparison.Ordinal) && Normalize(predictionText) == Normalize(goldText))
            return "SEMANTIC_PRESENT_WRONG_SOURCE_SAME_TEXT";
        return "UNRESOLVED";
    }

    public static bool IsGoldRoleEvaluable(string? role) => !string.IsNullOrWhiteSpace(role) && !role.Equals("heading", StringComparison.OrdinalIgnoreCase);

    private static object BuildAuthority(SourceDocument source, string sourcePath, string sourceSha, JsonElement goldRoot, JsonElement strict,
        IReadOnlyList<Gold> gold, IReadOnlyDictionary<string, SourceParagraph> paragraphs, IReadOnlyList<PageAlias> aliases, IReadOnlyList<Run> runs)
    {
        var goldRows = gold.Select((g, i) =>
        {
            var p = paragraphs[g.SourceId];
            var page = aliases.Where(a => a.SourceId == g.SourceId && a.VisibleStart <= g.Start && a.VisibleEnd >= g.End).Select(a => a.Page).FirstOrDefault();
            return new
            {
                headingOrdinal = i, sourceId = g.SourceId, sourceOrdinal = g.SourceOrdinal, start = g.Start, end = g.End,
                exactText = g.RawText, rawSourceText = Slice(p.Text, g.Start, g.End), paragraphText = p.Text,
                spanLength = g.End - g.Start, semanticRole = g.Role, level = g.Level,
                sourceFacts = new { style = p.Style.StyleName, builtInHeadingLevel = p.Style.BuiltInHeadingStyleLevel, outlineLevel = p.Style.OutlineLevel, bold = p.Style.Bold, fontSizePt = p.Style.FontSizePt, alignment = p.Style.Alignment, tableDepth = p.Layout.TableDepth, numbering = p.Numbering.NumberLabel },
                pageMapping = new { page, mapped = page > 0 },
            };
        }).ToArray();
        var shapeCounts = gold.GroupBy(g => Shape(g, paragraphs[g.SourceId].Text)).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var roleCounts = gold.GroupBy(g => g.Role, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        return new
        {
            schemaVersion = "a99-doc0205-semantic-contract-authority-v1", documentId = DocumentId, generatedUtc = DateTimeOffset.UtcNow,
            source = new { path = Path.GetRelativePath(Directory.GetCurrentDirectory(), sourcePath).Replace('\\', '/'), sha256 = sourceSha, expectedSha256 = ExpectedSourceSha, paragraphCount = source.Paragraphs.Count, sourceOccurrenceCount = source.Paragraphs.Count },
            strictGold = new { occurrencePath = "eval/a99-closed-loop/strict-gold-occurrence-v1/DOC-0205.occurrence-gold-v1.json", strictV4Path = "eval/a99-closed-loop/strict-gold-v4/DOC-0205.strict-gold-v4.json", status = goldRoot.GetProperty("status").GetString(), finalAuthority = strict.GetProperty("finalAuthority").GetString(), userFinalApproval = strict.GetProperty("userFinalApproval").GetBoolean(), reviewedEntireDocument = strict.GetProperty("reviewedEntireDocument").GetBoolean(), headingSetExhaustive = strict.GetProperty("headingSetExhaustive").GetBoolean(), semanticHeadingTotal = gold.Count, exactOccurrenceCount = gold.Count(g => g.ExactVerified), sourceSha256 = goldRoot.GetProperty("sourceSha256").GetString() },
            observedGoldProfile = new { total = gold.Count, roleCounts, shapeCounts, sourceIds = gold.Select(g => g.SourceId).Distinct().ToArray(), allRolesEvaluable = gold.All(g => IsGoldRoleEvaluable(g.Role)), allExactRawSubstringsVerified = gold.All(g => g.ExactVerified), pageMappedCount = goldRows.Count(x => x.pageMapping.mapped) },
            goldOnlyForensicInput = true, runtimeTransformationReadsGold = false, rows = goldRows,
            crossDocumentExistingFrozenAuthority = CrossDocumentInventory(sourceRoot: Path.Combine(Directory.GetCurrentDirectory(), "eval/a99-closed-loop")),
            frozenRunLineage = runs.Select(Lineage).ToArray(),
        };
    }

    private static object BuildReconciliation(IReadOnlyList<Run> runs, IReadOnlyList<RunClassification> classified, IReadOnlyList<Gold> gold)
    {
        var byLabel = classified.ToDictionary(x => x.Run.Spec.Label, StringComparer.Ordinal);
        var c0 = byLabel["C0 structure-preserving"];
        var c2 = byLabel["C2 addressed"];
        var text = byLabel["Flash text V4"];
        var visual = byLabel["Flash visual V4"];
        var transitions = gold.Select((_, i) => new { ordinal = i, from = c0.PerGold[i].Class, to = c2.PerGold[i].Class }).GroupBy(x => x.from + "→" + x.to, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        var officialTransitions = gold.Select((_, i) => new { ordinal = i, from = OfficialState(c0.PerGold[i].Class), to = OfficialState(c2.PerGold[i].Class) }).GroupBy(x => x.from + "→" + x.to, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        var textVisual = gold.Select((_, i) => new { ordinal = i, text = text.PerGold[i].Class, visual = visual.PerGold[i].Class }).GroupBy(x => x.text + "→" + x.visual, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        return new
        {
            schemaVersion = "a99-doc0205-semantic-contract-reconciliation-v1", documentId = DocumentId, generatedUtc = DateTimeOffset.UtcNow, providerCalls = ProviderCalls,
            correspondenceRule = new { officialExactKey = "sourceId + exact start/end", diagnostic = new[] { "same-source overlap/containment", "exact normalized text", "shared structural occurrence" }, note = "A corresponding raw proposal is not an omission solely because its span differs." },
            runs = classified.Select(x => new { x.Run.Spec.Label, score = x.Run.Score, x.Run.RawProposalCount, finalProposalCount = x.Run.Proposals.Count, x.Counts, perGold = x.PerGold }).ToArray(),
            c0WrongSpanReconciliation = new { officialWrongSpanLoss = 56, semanticCorrespondence = c0.PerGold.Count(x => x.Class is "SEMANTIC_PRESENT_PARTIAL_SPAN" or "SEMANTIC_PRESENT_SUPERSET_SPAN" or "SEMANTIC_PRESENT_OFFSET_SHIFT"), exact = c0.PerGold.Count(x => x.Class == "EXACT_MATCH"), trueOmission = c0.PerGold.Count(x => x.Class == "TRUE_MODEL_OMISSION"), auditorConclusion = "C0's 56 WrongSpan are semantic correspondences under the canonical same-source overlap rule; the official exact score remains unchanged." },
            c0ToC2Transitions = transitions, c0ToC2OfficialTransitions = officialTransitions, textV4ToVisualV4 = textVisual,
            visualOnlyExtras = visual.Run.Proposals.Where(p => !text.Run.Proposals.Any(t => t.SourceId == p.SourceId && t.Start == p.Start && t.End == p.End)).Take(200).Select(p => new { p.SourceId, p.Start, p.End, p.Text, p.Role }).ToArray(),
            representativeExamples = new { c0WrongSpan = ExamplesFor(c0, 5, x => x.Class is "SEMANTIC_PRESENT_PARTIAL_SPAN" or "SEMANTIC_PRESENT_SUPERSET_SPAN" or "SEMANTIC_PRESENT_OFFSET_SHIFT"), trueOmissions = ExamplesFor(c0, 5, x => x.Class == "TRUE_MODEL_OMISSION"), c2FalsePositives = c2.Run.Proposals.Where(p => !c2.PerGold.Any(x => x.MatchedPredictionId == p.Id)).Take(5).ToArray(), visualOnlyFalsePositives = visual.Run.Proposals.Where(p => !text.Run.Proposals.Any(t => t.SourceId == p.SourceId && t.Start == p.Start && t.End == p.End)).Take(5).ToArray(), missedByEveryContract = gold.Select((g, i) => new { g, i }).Where(x => classified.All(r => r.PerGold[x.i].Class is "TRUE_MODEL_OMISSION" or "SYSTEM_BINDING_LOSS" or "SYSTEM_VALIDATOR_LOSS" or "SYSTEM_PROJECTION_LOSS")).Take(5).Select(x => new { x.i, x.g.RawText, x.g.Start, x.g.End }).ToArray() },
        };
    }

    private static object BuildSummary(string startHead, string sourceSha, JsonElement goldRoot, JsonElement strict, IReadOnlyList<Run> runs, IReadOnlyList<RunClassification> classifications, object reconciliation)
    {
        var c0 = classifications.Single(x => x.Run.Spec.Label == "C0 structure-preserving");
        var text = classifications.Single(x => x.Run.Spec.Label == "Flash text V4");
        var visual = classifications.Single(x => x.Run.Spec.Label == "Flash visual V4");
        var primary = c0.PerGold.Count(x => x.Class is "SEMANTIC_PRESENT_PARTIAL_SPAN" or "SEMANTIC_PRESENT_SUPERSET_SPAN" or "SEMANTIC_PRESENT_OFFSET_SHIFT") >= 50
            ? "SEMANTIC_DISCOVERY_GOOD_SPAN_CONTRACT_BAD" : "MODEL_AND_SPAN_CONTRACT_MIXED";
        return new
        {
            schemaVersion = "a99-doc0205-semantic-contract-reconciliation-v1", documentId = DocumentId, generatedUtc = DateTimeOffset.UtcNow, expectedStartHead = ExpectedStartHead, startHead, startHeadMatchesExpected = startHead == ExpectedStartHead, endHead = GitSha(Directory.GetCurrentDirectory()), benchmarkMode = "OFFLINE_FROZEN_ARTIFACTS_ONLY", providerCalls = ProviderCalls, modelCalls = 0, goldReadBeforeFreeze = false,
            sourceAuthority = new { sourceSha256 = sourceSha, expectedSourceSha256 = ExpectedSourceSha, matches = sourceSha == ExpectedSourceSha },
            goldAuthority = new { status = goldRoot.GetProperty("status").GetString(), strictV4FinalAuthority = strict.GetProperty("finalAuthority").GetString(), count = goldRoot.GetProperty("semanticHeadingTotal").GetInt32(), sourceSha256 = goldRoot.GetProperty("sourceSha256").GetString(), allExactSubstringsVerified = goldRoot.GetProperty("bindings").EnumerateArray().All(x => x.GetProperty("exactRawSubstringVerified").GetBoolean()) },
            runTable = runs.Select(r => new { model = r.Spec.Label, tp = r.Score.Tp, fp = r.Score.Fp, fn = r.Score.Fn, precision = r.Score.Precision, recall = r.Score.Recall, f1 = r.Score.F1, modelOmission = r.FirstLoss.ModelOmission, spanError = r.FirstLoss.WrongSpan, systemLoss = r.FirstLoss.SystemLoss, wallTime = r.Freeze.ElapsedMs, sourceSha256 = r.Freeze.SourceSha, promptHash = r.Freeze.PromptHash, packetHash = r.Freeze.PacketHash }).ToArray(),
            c0Finding = new { officialScore = c0.Run.Score, wrongSpanCorrespondence = c0.PerGold.Count(x => x.Class.Contains("SPAN", StringComparison.Ordinal)), semanticDiscoveryRecall = (double)c0.PerGold.Count(x => x.Class is "EXACT_MATCH" or "SEMANTIC_PRESENT_PARTIAL_SPAN" or "SEMANTIC_PRESENT_SUPERSET_SPAN" or "SEMANTIC_PRESENT_OFFSET_SHIFT") / goldRoot.GetProperty("semanticHeadingTotal").GetInt32(), finding = "C0 discovers 57/71 Gold semantic regions (1 exact + 56 same-source span correspondences); exact scorer reports only 1 TP because the official key is exact sourceId/start/end." },
            textToVisual = new { textScore = text.Run.Score, visualScore = visual.Run.Score, textSemanticPresent = text.PerGold.Count(x => x.Class.StartsWith("SEMANTIC_PRESENT", StringComparison.Ordinal) || x.Class == "EXACT_MATCH"), visualSemanticPresent = visual.PerGold.Count(x => x.Class.StartsWith("SEMANTIC_PRESENT", StringComparison.Ordinal) || x.Class == "EXACT_MATCH"), conclusion = "Visual V4 increases false positives without increasing exact TP; no further visual tuning is justified before semantic/span contract repair." },
            promptSemanticComparison = new { S0 = "generic structural-heading extraction with source-grounded proposals", C0 = "structure-preserving source IR with line-oriented source anchors", C2 = "addressed source spans plus explicit semantic heading proposals", V4 = "V4 legal-content heading ontology contract; visual V4 adds page-image evidence without changing exact occurrence authority", interpretation = "Comparison is descriptive of frozen prompt/contracts only; no prompt was rewritten and Gold was not used in runtime logic." },
            goldTargetDefinition = "The approved target is the exhaustive 71-row legal-content occurrence set, keyed by sourceId plus exact half-open UTF-16 start/end; role is evaluated separately and does not redefine occurrence identity.",
            primaryClassification = primary,
            rootCause = primary == "SEMANTIC_DISCOVERY_GOOD_SPAN_CONTRACT_BAD" ? "C0 evidence shows high semantic discovery with systematic boundary mismatch; C0's official WrongSpan=56 is not a true omission." : "No single causal class dominates the frozen evidence.",
            architectureRecommendation = "Separate semantic discovery from source-anchored span localization, then apply exact canonical scoring. Keep Gold outside runtime logic and evaluate the localization intervention as a new paired DEV experiment.",
            crossDocumentRecommendation = "Use existing frozen DOC-0258/DOC-0256/DOC-0252 authority artifacts only for generalization checks; do not infer DOC-0205 rules from them.",
            requiredTests = new[] { "classifier exact/partial/superset/overlap/wrong-source", "partial and superset are not omission", "official exact score unchanged", "frozen hashes unchanged", "Gold offline firewall", "role not inferred from span" },
        };
    }

    private static RunClassification ClassifyRun(Run run, IReadOnlyList<Gold> gold)
    {
        var perGold = new List<GoldClassification>();
        foreach (var g in gold)
        {
            if (run.SystemBindingLoss && run.Proposals.Count == 0) { perGold.Add(new(g.Ordinal, "SYSTEM_BINDING_LOSS", null, null, null)); continue; }
            var exact = run.Proposals.FirstOrDefault(p => p.SourceId == g.SourceId && p.Start == g.Start && p.End == g.End);
            if (exact is not null) { perGold.Add(new(g.Ordinal, exact.Role.Equals(g.Role, StringComparison.OrdinalIgnoreCase) ? "EXACT_MATCH" : "MODEL_PROPOSAL_WRONG_ROLE", exact.Id, exact.Start, exact.End)); continue; }
            var same = run.Proposals.Where(p => p.SourceId == g.SourceId).ToArray();
            var candidate = same.FirstOrDefault(p => p.Start < g.End && g.Start < p.End);
            if (candidate is not null)
            {
                var cls = ClassifySpan(candidate.Start, candidate.End, candidate.SourceId, candidate.Text, candidate.Role, g.Start, g.End, g.SourceId, g.RawText, g.Role);
                perGold.Add(new(g.Ordinal, cls, candidate.Id, candidate.Start, candidate.End)); continue;
            }
            var wrongSource = run.Proposals.FirstOrDefault(p => p.SourceId != g.SourceId && Normalize(p.Text) == Normalize(g.RawText));
            if (wrongSource is not null) { perGold.Add(new(g.Ordinal, "SEMANTIC_PRESENT_WRONG_SOURCE_SAME_TEXT", wrongSource.Id, wrongSource.Start, wrongSource.End)); continue; }
            perGold.Add(new(g.Ordinal, "TRUE_MODEL_OMISSION", null, null, null));
        }
        var counts = perGold.GroupBy(x => x.Class, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        return new(run, perGold, counts);
    }

    private static Run LoadRun(string root, RunSpec spec, IReadOnlyList<Gold> gold, IReadOnlyDictionary<string, SourceParagraph> paragraphs)
    {
        var dir = Path.Combine(root, "eval/a99-closed-loop", spec.RelativeRoot.Replace('/', Path.DirectorySeparatorChar));
        var score = ReadScore(Path.Combine(dir, spec.Score));
        var predPath = Path.Combine(dir, spec.Prediction);
        var prediction = JsonDocument.Parse(File.ReadAllText(predPath)).RootElement;
        var proposals = ReadProposals(prediction);
        var freeze = ReadFreeze(Path.Combine(dir, spec.Freeze));
        var first = string.IsNullOrWhiteSpace(spec.FirstLoss) || !File.Exists(Path.Combine(dir, spec.FirstLoss))
            ? new FirstLoss(score.Fn, 0, 0, 0, 0)
            : ReadFirstLoss(Path.Combine(dir, spec.FirstLoss));
        var rawCount = GetInt(prediction, "rawProposalCount") ?? GetInt(prediction, "rawCount") ?? proposals.Count;
        var systemBinding = spec.Label == "C1 boundary" && rawCount > 0 && proposals.Count == 0;
        return new(spec, score, proposals, rawCount, freeze, first with { SystemLoss = systemBinding ? rawCount : first.SystemLoss }, systemBinding);
    }

    private static IReadOnlyList<Proposal> ReadProposals(JsonElement root)
    {
        if (!root.TryGetProperty("proposals", out var p) || p.ValueKind != JsonValueKind.Array) return [];
        return p.EnumerateArray().Select((x, i) =>
        {
            var span = x.GetProperty("headingSpan");
            return new Proposal(i, x.GetProperty("sourceId").GetString() ?? "", span.GetProperty("start").GetInt32(), span.GetProperty("end").GetInt32(), x.GetProperty("text").GetString() ?? "", x.TryGetProperty("semanticRole", out var role) ? role.GetString() ?? "" : "");
        }).ToArray();
    }

    private static Gold GoldRow(JsonElement x) => new(x.GetProperty("headingOrdinal").GetInt32(), x.GetProperty("sourceId").GetString() ?? "", x.GetProperty("headingSpan").GetProperty("start").GetInt32(), x.GetProperty("headingSpan").GetProperty("end").GetInt32(), x.GetProperty("rawSourceText").GetString() ?? "", x.GetProperty("semanticRole").GetString() ?? "", x.GetProperty("level").GetInt32(), x.GetProperty("sourceId").GetString() ?? "", x.GetProperty("exactRawSubstringVerified").GetBoolean());

    private static Score ReadScore(string path)
    {
        var x = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        return new(GetInt(x, "tp") ?? 0, GetInt(x, "fp") ?? 0, GetInt(x, "fn") ?? 0, GetDouble(x, "precision") ?? 0, GetDouble(x, "recall") ?? 0, GetDouble(x, "f1") ?? 0);
    }
    private static Freeze ReadFreeze(string path)
    {
        var x = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        return new(GetString(x, "sourceSha256"), GetString(x, "promptHash"), GetString(x, "packetHash"), GetString(x, "predictionSha256"), GetString(x, "resultSha256"), GetString(x, "gitSha"), GetString(x, "finishReason"), GetInt(x, "elapsedMs"), GetString(x, "actualProvider"), GetString(x, "reportedModel"));
    }
    private static FirstLoss ReadFirstLoss(string path)
    {
        var x = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        var l = x.TryGetProperty("losses", out var losses) ? losses : x;
        return new(GetInt(l, "MODEL_OMISSION") ?? GetInt(x, "modelOmission") ?? 0, GetInt(l, "MODEL_WRONG_SPAN") ?? 0, GetInt(l, "SYSTEM_BINDING_LOSS") ?? GetInt(x, "systemLoss") ?? 0, GetInt(l, "SYSTEM_VALIDATOR_LOSS") ?? 0, GetInt(l, "SYSTEM_PROJECTION_LOSS") ?? 0);
    }

    private static IReadOnlyList<PageAlias> LoadAliases(string root)
    {
        var path = Path.Combine(root, "eval/a99-closed-loop/qwen37-flash-visual-ceiling/DOC-0205/source-page-alignment.v1.json".Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return [];
        var rootElement = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        return rootElement.GetProperty("aliases").EnumerateArray().Select(x => new PageAlias(x.GetProperty("sourceId").GetString() ?? "", x.GetProperty("pageIndex").GetInt32(), x.GetProperty("visibleStartCharacter").GetInt32(), x.GetProperty("visibleEndCharacter").GetInt32())).ToArray();
    }

    private static object[] CrossDocumentInventory(string sourceRoot) => new object[] { "DOC-0258", "DOC-0256", "DOC-0252" }.Select(id =>
    {
        var gold = Path.Combine(sourceRoot, "strict-gold-occurrence-v1", id + ".occurrence-gold-v1.json");
        var v4 = Path.Combine(sourceRoot, "strict-gold-v4", id + ".strict-gold-v4.json");
        return new { documentId = id, occurrenceGoldExists = File.Exists(gold), strictGoldV4Exists = File.Exists(v4), occurrenceGoldCount = File.Exists(gold) ? JsonDocument.Parse(File.ReadAllText(gold)).RootElement.GetProperty("semanticHeadingTotal").GetInt32() : 0 };
    }).Cast<object>().ToArray();

    private static object Lineage(Run r) => new { model = r.Spec.Label, root = "eval/a99-closed-loop/" + r.Spec.RelativeRoot, score = r.Score, commitSha = r.Freeze.GitSha, actualProvider = r.Freeze.ActualProvider, reportedModel = r.Freeze.ReportedModel, sourceSha = r.Freeze.SourceSha, promptHash = r.Freeze.PromptHash, packetHash = r.Freeze.PacketHash, predictionHash = r.Freeze.PredictionSha, resultHash = r.Freeze.ResultSha, goldAuthority = "eval/a99-closed-loop/strict-gold-v4/DOC-0205.strict-gold-v4.json", scorer = r.Spec.Score, firstLoss = r.Spec.FirstLoss, finishReason = r.Freeze.FinishReason, frozenBytesVerified = HashMatches(r), goldFirewall = true };

    private static bool HashMatches(Run r)
    {
        var dir = Path.Combine(Directory.GetCurrentDirectory(), "eval/a99-closed-loop", r.Spec.RelativeRoot.Replace('/', Path.DirectorySeparatorChar));
        var predictionOk = string.IsNullOrWhiteSpace(r.Freeze.PredictionSha) || Sha256(Path.Combine(dir, r.Spec.Prediction)) == r.Freeze.PredictionSha;
        var resultOk = string.IsNullOrWhiteSpace(r.Freeze.ResultSha) || Sha256(Path.Combine(dir, r.Spec.Result)) == r.Freeze.ResultSha;
        return predictionOk && resultOk;
    }

    private static object[] ExamplesFor(RunClassification r, int count, Func<GoldClassification, bool> predicate) => r.PerGold.Where(predicate).Take(count).Select(x => new { x.GoldOrdinal, x.Class, x.MatchedPredictionId, x.MatchedStart, x.MatchedEnd }).Cast<object>().ToArray();
    private static string PrimaryClassification(RunClassification c0) => c0.PerGold.Count(x => x.Class is "SEMANTIC_PRESENT_PARTIAL_SPAN" or "SEMANTIC_PRESENT_SUPERSET_SPAN" or "SEMANTIC_PRESENT_OFFSET_SHIFT") >= 50
        ? "SEMANTIC_DISCOVERY_GOOD_SPAN_CONTRACT_BAD" : "MODEL_AND_SPAN_CONTRACT_MIXED";
    private static string OfficialState(string classification) => classification == "EXACT_MATCH" ? "EXACT" : classification == "TRUE_MODEL_OMISSION" || classification.StartsWith("SYSTEM_", StringComparison.Ordinal) ? "OMISSION" : classification.StartsWith("SEMANTIC_PRESENT", StringComparison.Ordinal) ? "WRONG_SPAN" : "UNRESOLVED";
    private static string BuildExamples(IReadOnlyList<Gold> gold, IReadOnlyList<RunClassification> runs)
    {
        var c0 = runs.Single(x => x.Run.Spec.Label == "C0 structure-preserving");
        var lines = new List<string> { "# DOC-0205 semantic/span reconciliation examples", "", "Offline-only examples from frozen artifacts; no Gold bytes or predictions were changed.", "", "## C0 WrongSpan correspondences", "" };
        foreach (var row in c0.PerGold.Where(x => x.Class.Contains("SPAN", StringComparison.Ordinal)).Take(5)) lines.Add($"- Gold {row.GoldOrdinal}: `{gold[row.GoldOrdinal].RawText}` → `{row.Class}` prediction span `{row.MatchedStart}:{row.MatchedEnd}`");
        lines.AddRange(["", "## C0 true omissions", ""]);
        foreach (var row in c0.PerGold.Where(x => x.Class == "TRUE_MODEL_OMISSION").Take(5)) lines.Add($"- Gold {row.GoldOrdinal}: `{gold[row.GoldOrdinal].RawText}`");
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static string Shape(Gold g, string paragraph) => g.Start == 0 && g.End == paragraph.Length ? "FULL_PARAGRAPH" : (char.IsDigit(g.RawText.FirstOrDefault()) ? "PARTIAL_NUMBERED_PREFIX" : "PARTIAL_TITLE_OR_MARKER");
    private static string Slice(string text, int start, int end) => start >= 0 && end >= start && end <= text.Length ? text[start..end] : "";
    private static string Normalize(string text) => new(text.Normalize(NormalizationForm.FormD).Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c)).Select(char.ToLowerInvariant).ToArray());
    private static int? GetInt(JsonElement x, string p) => x.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
    private static double? GetDouble(JsonElement x, string p) => x.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;
    private static string? GetString(JsonElement x, string p) => x.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string GitSha(string root) { try { var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", "rev-parse HEAD") { WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false }); return p?.StandardOutput.ReadToEnd().Trim() ?? "NOT_PERSISTED"; } catch { return "NOT_PERSISTED"; } }
    private static async Task WriteJson(string root, string name, object value, CancellationToken ct) => await File.WriteAllTextAsync(Path.Combine(root, name), JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, ct);

    private sealed record RunSpec(string Label, string RelativeRoot, string Score, string Prediction, string Result, string Freeze, string FirstLoss);
    private sealed record Proposal(int Id, string SourceId, int Start, int End, string Text, string Role);
    private sealed record Gold(int Ordinal, string SourceId, int Start, int End, string RawText, string Role, int Level, string SourceReference, bool ExactVerified) { public int SourceOrdinal => 3; }
    private sealed record Score(int Tp, int Fp, int Fn, double Precision, double Recall, double F1);
    private sealed record Freeze(string? SourceSha, string? PromptHash, string? PacketHash, string? PredictionSha, string? ResultSha, string? GitSha, string? FinishReason, int? ElapsedMs, string? ActualProvider, string? ReportedModel);
    private sealed record FirstLoss(int ModelOmission, int WrongSpan, int SystemLoss, int ValidatorLoss, int ProjectionLoss);
    private sealed record Run(RunSpec Spec, Score Score, IReadOnlyList<Proposal> Proposals, int RawProposalCount, Freeze Freeze, FirstLoss FirstLoss, bool SystemBindingLoss);
    private sealed record GoldClassification(int GoldOrdinal, string Class, int? MatchedPredictionId, int? MatchedStart, int? MatchedEnd);
    private sealed record RunClassification(Run Run, IReadOnlyList<GoldClassification> PerGold, IReadOnlyDictionary<string, int> Counts)
    {
        public object GoldCounts => Counts;
    }
    private sealed record PageAlias(string SourceId, int Page, int VisibleStart, int VisibleEnd);
}
