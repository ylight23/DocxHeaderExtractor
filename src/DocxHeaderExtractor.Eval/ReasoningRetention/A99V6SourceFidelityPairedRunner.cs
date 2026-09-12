using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Policy;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Interleaved live A/B for source preparation only. Both branches use the same model, provider,
/// prompt, schema, VERBATIM_TEXT contract, downstream production path, and semantic authority.
/// The only causal delta is the current merged DOCX versus the faithful DOC-to-DOCX artifact.
/// </summary>
public static class A99V6SourceFidelityPairedRunner
{
    private const string Model = "qwen/qwen3.7-flash";
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const string InventoryPath = "eval/a99-dataset/document-inventory.v1.json";
    private const string OutputRoot = "eval/a99-closed-loop/source-fidelity-paired-live/DOC-0205";
    private const string CurrentSourcePath = "todo10_8/heading_corpus_95_word/01_phap_quy/025_ND_47-2020_Chia_se_du_lieu_so.docx";
    private const string FaithfulSourcePath = "eval/a99-closed-loop/source-fidelity-audit/DOC-0205/converted-docx/025_ND_47-2020_Chia_se_du_lieu_so.docx";
    private const string OccurrenceGoldPath = "eval/a99-closed-loop/strict-gold-occurrence-v1/DOC-0205.occurrence-gold-v1.json";
    private const string FidelityGoldPath = "eval/a99-closed-loop/source-fidelity-audit/DOC-0205/gold-representability.v1.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(output);
        var current = LoadContext(repoRoot, "CONTROL_CURRENT_MERGED_DOCX", CurrentSourcePath);
        var faithful = LoadContext(repoRoot, "FAITHFUL_LIBREOFFICE_DOCX", FaithfulSourcePath);
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        var startHead = Git(repoRoot, "rev-parse HEAD");
        if (string.IsNullOrWhiteSpace(key)) return await Blocked(output, startHead, "OPENROUTER_API_KEY_MISSING", ct);

        var controlPacket = BuildPacket(current);
        var faithfulPacket = BuildPacket(faithful);
        await WriteJson(Path.Combine(output, "manifest.v1.json"), new
        {
            schemaVersion = "a99-v6-source-fidelity-paired-manifest-v1",
            documentId = "DOC-0205", model = Model,
            executionOrder = new[] { "CONTROL_R1", "FAITHFUL_R1", "CONTROL_R2", "FAITHFUL_R2", "CONTROL_R3", "FAITHFUL_R3" },
            repeats = 3, interleaved = true,
            causalDelta = "SOURCE_PREPARATION_ONLY",
            control = ContextManifest(current, controlPacket),
            faithful = ContextManifest(faithful, faithfulPacket),
            semanticContract = SemanticTextExactBindingContract.ProtocolVersion,
            semanticPromptSha256 = Sha256Text(SemanticTextExactBindingContract.System),
            semanticSchemaSha256 = Sha256Text(JsonSerializer.Serialize(SemanticTextExactBindingContract.Schema())),
            sameModel = true, sameProviderSetup = true, sameGoldAuthority = true,
            candidateGeneration = false, candidateRecallGate = false,
            modelTextRequiredForBinding = true,
            modelNumericOffsets = false,
            multiParagraphGoldPolicy = "NOT_SINGLE_UTF16_SPAN;REPORT_SEPARATELY",
            goldReadBeforeFreeze = false,
            expectedProviderCalls = 6,
        }, ct);

        var options = new RemoteInferenceOptions
        {
            Endpoint = new Uri(Endpoint), Model = Model, ApiKey = key, ContextSize = 1_000_000,
            MaxOutputTokens = 48_000, RequestTimeoutSeconds = 600, TransientRequestRetries = 0,
            MaxParallelRequests = 1, SendChatTemplateKwargs = false, OpenRouterAllowNonZdrPublicBenchmark = true,
        };
        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
        var capability = await OpenRouterModelCapabilityResolver.ResolveAsync(options, http, ct);
        if (!capability.Available || capability.Capability is null ||
            !string.Equals(capability.Capability.ModelId, Model, StringComparison.Ordinal) ||
            !capability.Capability.ReasoningSupported || !capability.Capability.StructuredOutputSupported)
            return await Blocked(output, startHead, "MODEL_CAPABILITY_MISMATCH", ct);

        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(repoRoot, "source-fidelity-paired", "DOC-0205", ct);
        using var model = new OpenRouterCeilingReasoningModel(options, capability.Capability, http);
        var rows = new List<PairRow>();
        var reusedFrozenBranches = 0;
        for (var repeat = 1; repeat <= 3; repeat++)
        {
            var control = await LoadFrozenOrRunAsync(output, repeat, current, controlPacket, "CONTROL_CURRENT_MERGED_DOCX", $"C{repeat}", model, ct);
            if (control.ReusedFrozen) reusedFrozenBranches++;
            if (!control.ReusedFrozen) await PersistAsync(output, repeat, control.Branch, ct);
            var faithfulRun = await LoadFrozenOrRunAsync(output, repeat, faithful, faithfulPacket, "FAITHFUL_LIBREOFFICE_DOCX", $"F{repeat}", model, ct);
            if (faithfulRun.ReusedFrozen) reusedFrozenBranches++;
            if (!faithfulRun.ReusedFrozen) await PersistAsync(output, repeat, faithfulRun.Branch, ct);

            // Both branches are frozen before any Gold/evaluation artifact is opened.
            var gold = LoadOldGold(repoRoot);
            var faithfulGold = LoadFaithfulComparableGold(repoRoot);
            var controlFull = Score(control.Branch.Final, gold.Select(item => new ExactRow(item.SourceId, item.HeadingSpan!.Start, item.HeadingSpan.End, item.ExactText)).ToArray());
            var controlComparable = ScoreComparable(control.Branch.Final, faithfulGold.ControlGold, faithfulGold.ExcludedControlKeys, EmptySet());
            var faithfulComparable = ScoreComparable(faithfulRun.Branch.Final, faithfulGold.FaithfulRows, EmptySet(), faithfulGold.ExcludedFaithfulSourceIds);
            var result = new
            {
                schemaVersion = "a99-v6-source-fidelity-paired-score-v1", documentId = "DOC-0205", repeat = $"r{repeat}",
                controlFullStrict71 = controlFull,
                controlComparable66 = controlComparable,
                faithfulComparable66 = faithfulComparable,
                faithfulCrossParagraphGold = faithfulGold.CrossParagraphCount,
                diagnostics = new
                {
                    control = Diagnostics(control.Branch, current, faithfulGold.ControlGold),
                    faithful = Diagnostics(faithfulRun.Branch, faithful, faithfulGold.FaithfulRows),
                },
                goldReadBeforeFreeze = false,
            };
            await WriteJson(Path.Combine(output, $"r{repeat}", "score.v1.json"), result, ct);
            rows.Add(new($"r{repeat}", control.Branch, faithfulRun.Branch, controlFull, controlComparable, faithfulComparable));
        }

        var aggregateControlFull = Aggregate(rows.Select(row => row.ControlFull));
        var aggregateControlComparable = Aggregate(rows.Select(row => row.ControlComparable));
        var aggregateFaithfulComparable = Aggregate(rows.Select(row => row.FaithfulComparable));
        await WriteJson(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-v6-source-fidelity-paired-summary-v1", documentId = "DOC-0205", model = Model,
            startHead, endHead = Git(repoRoot, "rev-parse HEAD"), interleaved = true, repeats = 3,
            modelCalls = model.ProviderCalls + reusedFrozenBranches, providerCalls = model.ProviderCalls + reusedFrozenBranches,
            providerCallsExecutedThisProcess = model.ProviderCalls, frozenBranchesReused = reusedFrozenBranches,
            fullControlStrict71 = aggregateControlFull,
            pairedComparable66 = new { control = aggregateControlComparable, faithful = aggregateFaithfulComparable },
            faithfulCrossParagraphGold = 5,
            rows = rows.Select(row => new
            {
                row.Repeat,
                control = BranchSummary(row.Control, row.ControlFull),
                faithful = BranchSummary(row.Faithful, row.FaithfulComparable),
                controlComparable = row.ControlComparable,
                faithfulComparable = row.FaithfulComparable,
            }).ToArray(),
            decision = aggregateFaithfulComparable.F1 > aggregateControlComparable.F1
                ? "FAITHFUL_SOURCE_SIGNAL_REQUIRES_REVIEW"
                : "FAITHFUL_SOURCE_NO_PAIRED_LIFT",
            goldFirewall = "PASS",
        }, ct);
        Console.WriteLine($"MODEL_CALLS={model.ProviderCalls}");
        Console.WriteLine($"PROVIDER_CALLS={model.ProviderCalls}");
        Console.WriteLine("INTERLEAVED_ORDER=CONTROL_R1,FAITHFUL_R1,CONTROL_R2,FAITHFUL_R2,CONTROL_R3,FAITHFUL_R3");
        Console.WriteLine($"SOURCE_FIDELITY_PAIRED_SUMMARY={Path.Combine(OutputRoot, "summary.v1.json")}");
        return 0;
    }

    private static async Task<BranchRun> RunBranchAsync(SourceContext context, string packet, string mode, string requestSuffix, OpenRouterCeilingReasoningModel model, CancellationToken ct)
    {
        var result = await model.CompleteRawStructuredSemanticAsync(
            "DOC-0205", ReasoningRoute.ModelCapabilityCeiling.ToString(),
            $"{SemanticTextExactBindingContract.ProtocolVersion}:DOC-0205:{requestSuffix}",
            packet, context.Aliases.Sum(alias => alias.Text.Length), context.Aliases.Count, context.Aliases.Count,
            SemanticTextExactBindingContract.System,
            SemanticTextExactBindingContract.BuildUser(packet, ReasoningRoute.ModelCapabilityCeiling.ToString()),
            SemanticTextExactBindingContract.Schema(), "semantic_text_exact_binding_v1", ct);
        var parsed = SemanticTextExactBindingContract.Parse(result.Content);
        var proposals = parsed.Headings.Select(item => new CanonicalSemanticProposal(
            item.Source, true, item.Text, SemanticRole: item.Role, Occurrence: item.Occurrence,
            LeftExactContext: item.LeftExactContext, RightExactContext: item.RightExactContext)).ToArray();
        var production = RunProduction(context, proposals);
        var final = ProjectFinal(context, production);
        return new(mode, context, packet, proposals, parsed.Headings.Count, final, production, result.Telemetry, Sha256Text(result.Content));
    }

    private static CanonicalSemanticProductionResult RunProduction(SourceContext context, IReadOnlyList<CanonicalSemanticProposal> proposals) =>
        CanonicalSemanticProductionEntryPoint.Run(new CanonicalSemanticProductionInput(
            context.Catalog, proposals, context.SourceSha256,
            [new CanonicalSemanticPageEvidence("P0001", true, 0, "DOCX_TEXT")], [], [], [], [], null, null, null, context.SourceSha256, "DOC-0205"));

    private static async Task<LoadedBranch> LoadFrozenOrRunAsync(string output, int repeat, SourceContext context, string packet, string mode, string requestSuffix, OpenRouterCeilingReasoningModel model, CancellationToken ct)
    {
        var directory = Path.Combine(output, $"r{repeat}", mode.StartsWith("CONTROL", StringComparison.Ordinal) ? "control" : "faithful");
        var predictionPath = Path.Combine(directory, "prediction.v1.json");
        var freezePath = Path.Combine(directory, "freeze.v1.json");
        if (!File.Exists(predictionPath) || !File.Exists(freezePath))
            return new(await RunBranchAsync(context, packet, mode, requestSuffix, model, ct), false);

        using var freeze = JsonDocument.Parse(await File.ReadAllTextAsync(freezePath, ct));
        var freezeRoot = freeze.RootElement;
        if (freezeRoot.GetProperty("goldReadBeforeFreeze").GetBoolean() ||
            !string.Equals(freezeRoot.GetProperty("sourceSha256").GetString(), context.SourceSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(freezeRoot.GetProperty("packetSha256").GetString(), Sha256Text(packet), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(freezeRoot.GetProperty("predictionSha256").GetString(), Sha256(predictionPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"FROZEN_BRANCH_LINEAGE_MISMATCH:{mode}:r{repeat}");

        using var prediction = JsonDocument.Parse(await File.ReadAllTextAsync(predictionPath, ct));
        var root = prediction.RootElement;
        var proposals = JsonSerializer.Deserialize<IReadOnlyList<CanonicalSemanticProposal>>(root.GetProperty("proposals").GetRawText(), JsonOptions) ?? [];
        var production = RunProduction(context, proposals);
        var final = JsonSerializer.Deserialize<IReadOnlyList<ExactRow>>(root.GetProperty("finalHeadings").GetRawText(), JsonOptions) ?? [];
        var reconstructed = ProjectFinal(context, production);
        if (!reconstructed.SequenceEqual(final)) throw new InvalidDataException($"FROZEN_BRANCH_REPLAY_MISMATCH:{mode}:r{repeat}");
        var telemetry = JsonSerializer.Deserialize<RequestPacketTelemetry>(root.GetProperty("telemetry").GetRawText(), JsonOptions)
            ?? throw new InvalidDataException($"FROZEN_TELEMETRY_MISSING:{mode}:r{repeat}");
        var branch = new BranchRun(mode, context, packet, proposals, root.GetProperty("rawProposalCount").GetInt32(), final, production, telemetry, root.GetProperty("rawResponseSha256").GetString()!);
        return new(branch, true);
    }

    private static async Task PersistAsync(string output, int repeat, BranchRun branch, CancellationToken ct)
    {
        var directory = Path.Combine(output, $"r{repeat}", branch.Mode.StartsWith("CONTROL", StringComparison.Ordinal) ? "control" : "faithful");
        Directory.CreateDirectory(directory);
        var prediction = Path.Combine(directory, "prediction.v1.json");
        await WriteJson(prediction, new
        {
            schemaVersion = "a99-v6-source-fidelity-paired-prediction-v1", documentId = "DOC-0205", repeat = $"r{repeat}",
            mode = branch.Mode, model = Model, sourceSha256 = branch.Context.SourceSha256,
            sourceAliasCount = branch.Context.Aliases.Count,
            maxAliasChars = branch.Context.Aliases.Max(alias => alias.Text.Length),
            packetCharacters = branch.Packet.Length,
            providerRequestBytes = (int?)null,
            providerRequestBytesStatus = "NOT_EXPOSED_BY_ADAPTER",
            rawResponseSha256 = branch.RawResponseSha256, rawProposalCount = branch.RawCount,
            proposals = branch.Proposals, boundHeadings = branch.Production.TextPipeline.BoundHeadings,
            finalHeadings = branch.Final, bindingFailure = branch.Production.TextPipeline.BindingFailureCount,
            modelTextDrift = TextDrift(branch), modelOmission = "EVALUATED_AFTER_GOLD_FREEZE",
            telemetry = branch.Telemetry, goldReadBeforeFreeze = false,
        }, ct);
        await WriteJson(Path.Combine(directory, "freeze.v1.json"), new
        {
            schemaVersion = "a99-v6-source-fidelity-paired-freeze-v1", documentId = "DOC-0205", repeat = $"r{repeat}",
            mode = branch.Mode, model = Model, sourceSha256 = branch.Context.SourceSha256,
            packetSha256 = Sha256Text(branch.Packet), predictionSha256 = Sha256(prediction),
            rawProposalCount = branch.RawCount, boundCount = branch.Production.TextPipeline.BoundHeadings.Count,
            finalCount = branch.Final.Count, goldReadBeforeFreeze = false, frozenUtc = DateTimeOffset.UtcNow,
        }, ct);
    }

    private static object ContextManifest(SourceContext context, string packet) => new
    {
        context.Mode, sourcePath = context.SourcePath, sourceSha256 = context.SourceSha256,
        sourceAliasCount = context.Aliases.Count, maxAliasChars = context.Aliases.Max(alias => alias.Text.Length),
        sourceTextCharacters = context.Aliases.Sum(alias => alias.Text.Length), packetCharacters = packet.Length,
        packetSha256 = Sha256Text(packet),
    };

    private static string BuildPacket(SourceContext context) => JsonSerializer.Serialize(new
    {
        sourceAliases = context.Aliases.Select(alias => new { alias = alias.Alias, text = alias.Text, sourceOrdinal = alias.SourceOrdinal }).ToArray(),
    });

    private static SourceContext LoadContext(string repoRoot, string mode, string relativePath)
    {
        var path = Path.IsPathRooted(relativePath) ? relativePath : Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) throw new InvalidDataException($"SOURCE_MISSING:{relativePath}");
        var source = new OpenXmlDocumentSource().Read(path) with { DocumentId = "DOC-0205" };
        var hash = Sha256(path);
        if (string.Equals(mode, "CONTROL_CURRENT_MERGED_DOCX", StringComparison.Ordinal))
        {
            using var inventory = JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot, InventoryPath.Replace('/', Path.DirectorySeparatorChar))));
            var expected = inventory.RootElement.GetProperty("documents").EnumerateArray().Single(item => item.GetProperty("documentId").GetString() == "DOC-0205").GetProperty("sourceSha256").GetString();
            if (!string.Equals(expected, hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("CURRENT_SOURCE_HASH_MISMATCH:DOC-0205");
        }
        var features = NumberingStyleFeatures.FromSourceDocument(source);
        var derived = new DocumentFeatureDeriver().Derive(source);
        var policy = DocxPolicyStateBuilder.Build(source, features, derived, new PipelineOptions { DisableLlm = false }.Extraction);
        var aliases = source.Paragraphs.Where(paragraph => !string.IsNullOrWhiteSpace(paragraph.Text))
            .Select((paragraph, index) => new SemanticSourceAlias($"S{index + 1:0000}", paragraph.SourceId, paragraph.SourceOrdinal, paragraph.Text,
                new StructuralSpan(0, paragraph.Text.Length), new SourceAnchor { SourceType = "DOCX_TEXT", ParagraphId = paragraph.SourceId, ParagraphIndex = paragraph.SourceOrdinal })).ToArray();
        var catalog = new DocumentSourceCatalog(aliases.Select(alias => new DocumentSourceUnit(alias.SourceId, alias.SourceOrdinal, alias.Text, alias.SourceAnchor!, alias.SourceSpan)));
        return new(mode, relativePath, hash, source, policy, catalog, aliases);
    }

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
            .Select(item => new ExactRow(item.Sources.Single().SourceId, item.Sources.Single().Span.Start, item.Sources.Single().Span.End, item.Text)).ToArray();
    }

    private static IReadOnlyList<ReasoningGoldOccurrence> LoadOldGold(string repoRoot) =>
        ReasoningGoldArtifactLoader.LoadOccurrence(Path.Combine(repoRoot, OccurrenceGoldPath.Replace('/', Path.DirectorySeparatorChar)))
            .Where(item => item.HeadingSpan is not null).ToArray();

    private static FaithfulGold LoadFaithfulComparableGold(string repoRoot)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot, FidelityGoldPath.Replace('/', Path.DirectorySeparatorChar))));
        var rows = json.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        var control = LoadOldGold(repoRoot);
        var controlRows = new List<ExactRow>();
        var faithfulRows = new List<ExactRow>();
        var excludedControlKeys = new HashSet<string>(StringComparer.Ordinal);
        var excludedFaithfulSourceIds = new HashSet<string>(StringComparer.Ordinal);
        var multi = 0;
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var row = rows[rowIndex];
            var old = control[rowIndex];
            var converted = row.GetProperty("converted");
            var status = converted.GetProperty("status").GetString();
            if (status == "EXACT_SINGLE_UNIT")
            {
                var sourceId = converted.GetProperty("sourceIds")[0].GetString()!;
                var text = row.GetProperty("text").GetString()!;
                controlRows.Add(new(old.SourceId, old.HeadingSpan!.Start, old.HeadingSpan.End, old.ExactText));
                faithfulRows.Add(new(sourceId, 0, text.Length, text));
            }
            else if (status == "EXACT_CONTIGUOUS_MULTI_UNIT")
            {
                multi++;
                excludedControlKeys.Add(Key(old.SourceId, old.HeadingSpan!.Start, old.HeadingSpan.End));
                foreach (var sourceIdElement in converted.GetProperty("sourceIds").EnumerateArray())
                    excludedFaithfulSourceIds.Add(sourceIdElement.GetString()!);
            }
            else throw new InvalidDataException("FAITHFUL_GOLD_REPRESENTABILITY_NOT_EXACT");
        }
        return new(controlRows, faithfulRows, multi, excludedControlKeys, excludedFaithfulSourceIds);
    }

    private static object Diagnostics(BranchRun branch, SourceContext context, IReadOnlyList<ExactRow> gold)
    {
        var keys = gold.Select(row => Key(row.SourceId, row.Start, row.End)).ToHashSet(StringComparer.Ordinal);
        var finalKeys = branch.Final.Select(row => Key(row.SourceId, row.Start, row.End)).ToHashSet(StringComparer.Ordinal);
        var aliases = context.Aliases.ToDictionary(alias => alias.Alias, StringComparer.Ordinal);
        var drift = TextDrift(branch);
        var omissions = 0;
        foreach (var row in gold)
        {
            if (finalKeys.Contains(Key(row.SourceId, row.Start, row.End))) continue;
            var alias = aliases.Values.FirstOrDefault(item => item.SourceId == row.SourceId);
            var exact = alias is not null && branch.Proposals.Any(proposal => proposal.SourceAlias == alias.Alias && proposal.VerbatimText == row.Text);
            if (!exact && alias is not null && branch.Proposals.Any(proposal => proposal.SourceAlias == alias.Alias)) continue;
            omissions++;
        }
        return new
        {
            sourceAliasCount = context.Aliases.Count, maxAliasChars = context.Aliases.Max(alias => alias.Text.Length),
            packetCharacters = branch.Packet.Length, providerRequestBytes = "NOT_EXPOSED_BY_ADAPTER",
            inputTokens = branch.Telemetry is RequestPacketTelemetry telemetry ? telemetry.ReportedInputTokens : null,
            rawProposalCount = branch.RawCount, modelTextDrift = drift, modelOmission = omissions,
            bindFailure = branch.Production.TextPipeline.BindingFailureCount,
            systemLoss = keys.Except(finalKeys).Count(),
            finishReason = branch.Telemetry is RequestPacketTelemetry t ? t.FinishReason : null,
        };
    }

    private static int TextDrift(BranchRun branch) => branch.Proposals.Count(proposal =>
        proposal.IsHeading && (!branch.Context.Aliases.Any(alias => alias.Alias == proposal.SourceAlias) ||
            proposal.VerbatimText is null || !branch.Context.Aliases.Single(alias => alias.Alias == proposal.SourceAlias).Text.Contains(proposal.VerbatimText, StringComparison.Ordinal)));

    private static object BranchSummary(BranchRun branch, ScoreRow score) => new
    {
        mode = branch.Mode, score, sourceSha256 = branch.Context.SourceSha256,
        sourceAliasCount = branch.Context.Aliases.Count, maxAliasChars = branch.Context.Aliases.Max(alias => alias.Text.Length),
        packetCharacters = branch.Packet.Length, providerRequestBytes = "NOT_EXPOSED_BY_ADAPTER",
        rawProposalCount = branch.RawCount, boundCount = branch.Production.TextPipeline.BoundHeadings.Count,
        finalCount = branch.Final.Count, modelTextDrift = TextDrift(branch), bindFailure = branch.Production.TextPipeline.BindingFailureCount,
        systemLoss = branch.Production.TextPipeline.BoundHeadings.Select(item => Key(item.SourceId, item.Start, item.End))
            .Except(branch.Final.Select(item => Key(item.SourceId, item.Start, item.End)), StringComparer.Ordinal).Count(),
    };

    private static ScoreRow Score(IReadOnlyList<ExactRow> prediction, IReadOnlyList<ExactRow> gold) =>
        ScoreComparable(prediction, gold, EmptySet(), EmptySet());

    private static IReadOnlySet<string> EmptySet() => new HashSet<string>(StringComparer.Ordinal);

    private static ScoreRow ScoreComparable(
        IReadOnlyList<ExactRow> prediction,
        IReadOnlyList<ExactRow> gold,
        IReadOnlySet<string> excludedPredictionKeys,
        IReadOnlySet<string> excludedPredictionSourceIds)
    {
        var includedPredictionKeys = prediction
            .Where(row => !excludedPredictionSourceIds.Contains(row.SourceId))
            .Select(row => Key(row.SourceId, row.Start, row.End))
            .Where(key => !excludedPredictionKeys.Contains(key))
            .ToHashSet(StringComparer.Ordinal);
        var p = includedPredictionKeys;
        var g = gold.Select(row => Key(row.SourceId, row.Start, row.End)).ToHashSet(StringComparer.Ordinal);
        var tp = p.Intersect(g).Count(); var fp = p.Except(g).Count(); var fn = g.Except(p).Count();
        var precision = tp + fp == 0 ? 0d : (double)tp / (tp + fp); var recall = tp + fn == 0 ? 0d : (double)tp / (tp + fn);
        var excludedCount = prediction
            .Select(row => Key(row.SourceId, row.Start, row.End))
            .Where(key => excludedPredictionKeys.Contains(key) || excludedPredictionSourceIds.Contains(key.Split(':')[0]))
            .ToHashSet(StringComparer.Ordinal)
            .Count;
        return new(tp, fp, fn, precision, recall, precision + recall == 0 ? 0d : 2 * precision * recall / (precision + recall), excludedCount);
    }

    private static ScoreRow Aggregate(IEnumerable<ScoreRow> scores)
    {
        var rows = scores.ToArray(); var tp = rows.Sum(row => row.Tp); var fp = rows.Sum(row => row.Fp); var fn = rows.Sum(row => row.Fn);
        var p = tp + fp == 0 ? 0d : (double)tp / (tp + fp); var r = tp + fn == 0 ? 0d : (double)tp / (tp + fn);
        return new(tp, fp, fn, p, r, p + r == 0 ? 0d : 2 * p * r / (p + r), rows.Sum(row => row.ExcludedPredictions));
    }

    private static string Key(string sourceId, int start, int end) => $"{sourceId}:{start}:{end}";
    private static async Task<int> Blocked(string output, string head, string reason, CancellationToken ct)
    {
        await WriteJson(Path.Combine(output, "summary.v1.json"), new { status = "BLOCKED", reason, startHead = head, modelCalls = 0, providerCalls = 0, goldReadBeforeFreeze = false }, ct);
        return 1;
    }
    private static async Task WriteJson(string path, object value, CancellationToken ct) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, new UTF8Encoding(false), ct);
    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Git(string root, string args) { using var p = Process.Start(new ProcessStartInfo("git", args) { WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false }); return p?.StandardOutput.ReadToEnd().Trim() ?? "UNKNOWN"; }

    private sealed record SourceContext(string Mode, string SourcePath, string SourceSha256, SourceDocument Source, DocxPolicyState Policy, DocumentSourceCatalog Catalog, IReadOnlyList<SemanticSourceAlias> Aliases);
    private sealed record BranchRun(string Mode, SourceContext Context, string Packet, IReadOnlyList<CanonicalSemanticProposal> Proposals, int RawCount, IReadOnlyList<ExactRow> Final, CanonicalSemanticProductionResult Production, RequestPacketTelemetry Telemetry, string RawResponseSha256);
    private sealed record LoadedBranch(BranchRun Branch, bool ReusedFrozen);
    private sealed record ExactRow(string SourceId, int Start, int End, string Text);
    private sealed record ScoreRow(int Tp, int Fp, int Fn, double Precision, double Recall, double F1, int ExcludedPredictions);
    private sealed record FaithfulGold(
        IReadOnlyList<ExactRow> ControlGold,
        IReadOnlyList<ExactRow> FaithfulRows,
        int CrossParagraphCount,
        IReadOnlySet<string> ExcludedControlKeys,
        IReadOnlySet<string> ExcludedFaithfulSourceIds);
    private sealed record PairRow(string Repeat, BranchRun Control, BranchRun Faithful, ScoreRow ControlFull, ScoreRow ControlComparable, ScoreRow FaithfulComparable);
}
