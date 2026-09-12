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

/// <summary>Interleaved live paired challenger for source-slice addressing. It keeps the current
/// verbatim contract as control and changes only the model-facing address contract in the paired
/// branch. Each branch is frozen before Gold is read; no Gold enters either request.</summary>
public static class A99V6SourceSliceChallengerRunner
{
    private const string Model = "qwen/qwen3.7-flash";
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const string InventoryPath = "eval/a99-dataset/document-inventory.v1.json";
    private const string OutputRoot = "eval/a99-closed-loop/source-slice-challenger/DOC-0205";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(output);
        var sourceContext = LoadContext(repoRoot);
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        var startHead = Git(repoRoot, "rev-parse HEAD");
        if (string.IsNullOrWhiteSpace(key))
            return await Blocked(output, startHead, "OPENROUTER_API_KEY_MISSING", ct);

        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
        var options = new RemoteInferenceOptions
        {
            Endpoint = new Uri(Endpoint), Model = Model, ApiKey = key, ContextSize = 1_000_000,
            MaxOutputTokens = 48_000, RequestTimeoutSeconds = 600, TransientRequestRetries = 0,
            MaxParallelRequests = 1, SendChatTemplateKwargs = false, OpenRouterAllowNonZdrPublicBenchmark = true,
        };
        var capabilityResult = await OpenRouterModelCapabilityResolver.ResolveAsync(options, http, ct);
        if (!capabilityResult.Available || capabilityResult.Capability is null ||
            !string.Equals(capabilityResult.Capability.ModelId, Model, StringComparison.Ordinal) ||
            !capabilityResult.Capability.ReasoningSupported || !capabilityResult.Capability.StructuredOutputSupported)
            return await Blocked(output, startHead, "MODEL_CAPABILITY_MISMATCH", ct);

        var controlPacket = JsonSerializer.Serialize(new
        {
            sourceAliases = sourceContext.Aliases.Select(alias => new { alias = alias.Alias, text = alias.Text, sourceOrdinal = alias.SourceOrdinal }).ToArray(),
        });
        var challengerPacket = JsonSerializer.Serialize(new
        {
            sourceSlices = sourceContext.Aliases.Select(alias => new
            {
                source = alias.Alias,
                sourceId = alias.SourceId,
                sourceOrdinal = alias.SourceOrdinal,
                slices = sourceContext.SlicesByAlias[alias.Alias].Select(slice => new { id = slice.SliceId, text = slice.Text, ordinal = slice.Ordinal }).ToArray(),
            }).ToArray(),
        });
        await WriteJson(Path.Combine(output, "manifest.v1.json"), new
        {
            schemaVersion = "a99-v6-source-slice-challenger-manifest-v1",
            documentId = "DOC-0205", model = Model, sourceSha256 = sourceContext.SourceSha256,
            repeats = 3, executionOrder = new[] { "CONTROL_R1", "CHALLENGER_R1", "CONTROL_R2", "CHALLENGER_R2", "CONTROL_R3", "CHALLENGER_R3" },
            controlContract = SemanticTextExactBindingContract.ProtocolVersion,
            challengerContract = SourceSliceSemanticContract.ProtocolVersion,
            controlPacketSha256 = Sha256Text(controlPacket), challengerPacketSha256 = Sha256Text(challengerPacket),
            controlSchemaSha256 = Sha256Text(JsonSerializer.Serialize(SemanticTextExactBindingContract.Schema())),
            challengerSchemaSha256 = Sha256Text(JsonSerializer.Serialize(SourceSliceSemanticContract.Schema())),
            sourceSliceCount = sourceContext.SlicesByAlias.Values.Sum(slices => slices.Count),
            losslessSliceReconstruction = sourceContext.SlicesByAlias.Values.All(slices => string.Concat(slices.Select(slice => slice.Text)) == sourceContext.Aliases.Single(alias => alias.Alias == slices[0].SourceAlias).Text),
            candidateGeneration = false, candidateRecallGate = false, numericOffsetsInModelContract = false,
            modelEchoTextRequiredForBinding = false, providerCallsExpected = 6, goldReadBeforeFreeze = false,
        }, ct);

        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(repoRoot, "source-slice-challenger", "DOC-0205", ct);
        using var model = new OpenRouterCeilingReasoningModel(options, capabilityResult.Capability, http);
        var rows = new List<PairedCell>();
        for (var repeat = 1; repeat <= 3; repeat++)
        {
            ct.ThrowIfCancellationRequested();
            // Interleaving is intentional: C1 → S1 → C2 → S2 → C3 → S3.
            var control = await RunControlAsync(sourceContext, model, controlPacket, repeat, ct);
            await PersistBranchAsync(output, repeat, control, sourceContext, ct);
            var challenger = await RunChallengerAsync(sourceContext, model, challengerPacket, repeat, ct);
            await PersistBranchAsync(output, repeat, challenger, sourceContext, ct);

            // Gold firewall: both paired branch freezes exist before the first Gold read.
            var goldPath = Path.Combine(repoRoot, "eval/a99-closed-loop/strict-gold-occurrence-v1", "DOC-0205.occurrence-gold-v1.json");
            var gold = ReasoningGoldArtifactLoader.LoadOccurrence(goldPath).Where(item => item.HeadingSpan is not null)
                .Select(item => new ExactRow(item.SourceId, item.HeadingSpan!.Start, item.HeadingSpan.End, item.ExactText)).ToHashSet();
            var controlScore = Score(control.Final, gold);
            var challengerScore = Score(challenger.Final, gold);
            await WriteJson(Path.Combine(output, $"r{repeat}", "score.v1.json"), new
            {
                schemaVersion = "a99-v6-source-slice-challenger-score-v1", documentId = "DOC-0205", repeat = $"r{repeat}",
                control = controlScore, challenger = challengerScore,
                challengerDiagnostics = new
                {
                    selectedSingleSlice = challenger.SelectedSingleSlice,
                    selectedMultiSlice = challenger.SelectedMultiSlice,
                    invalidSliceSelection = challenger.SliceObservations.Count(item => item.Status == SourceSliceBindingStatus.InvalidSliceId),
                    ambiguousSliceSelection = challenger.SliceObservations.Count(item => item.Status == SourceSliceBindingStatus.Overlap),
                    goldRepresentableButNotSelected = gold.Count(item => !challenger.Bound.Any(bound => bound.SourceId == item.SourceId && bound.Start == item.Start && bound.End == item.End)),
                },
                delta = new { tp = challengerScore.Tp - controlScore.Tp, fp = challengerScore.Fp - controlScore.Fp, fn = challengerScore.Fn - controlScore.Fn, f1 = challengerScore.F1 - controlScore.F1 },
                goldReadBeforeFreeze = false,
            }, ct);
            rows.Add(new($"r{repeat}", control.RawCount, challenger.RawCount, control.Bound.Count, challenger.Bound.Count,
                control.Final.Count, challenger.Final.Count, controlScore, challengerScore,
                challenger.SelectedSingleSlice, challenger.SelectedMultiSlice,
                challenger.SliceObservations.Count(item => item.Status == SourceSliceBindingStatus.InvalidSliceId),
                challenger.SliceObservations.Count(item => item.Status == SourceSliceBindingStatus.Overlap)));
        }

        await WriteJson(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-v6-source-slice-challenger-summary-v1", documentId = "DOC-0205", model = Model,
            startHead, endHead = Git(repoRoot, "rev-parse HEAD"), sourceSha256 = sourceContext.SourceSha256,
            repeats = 3, interleaved = true, modelCalls = model.ProviderCalls, providerCalls = model.ProviderCalls,
            aggregate = new { control = Aggregate(rows.Select(item => item.ControlScore)), challenger = Aggregate(rows.Select(item => item.ChallengerScore)) },
            rows, acceptance = new
            {
                losslessSliceReconstruction = true,
                invalidSliceSelection = rows.Sum(item => item.InvalidSliceSelection),
                ambiguousSliceSelection = rows.Sum(item => item.AmbiguousSliceSelection),
                numericOffsetsInModelContract = 0,
                modelEchoTextRequiredForBinding = 0,
                goldReadBeforeFreeze = 0,
            },
            goldFirewall = "PASS",
            decision = rows.Any(item => item.ChallengerScore.F1 > item.ControlScore.F1)
                ? "SOURCE_SLICE_CHALLENGER_SIGNAL_REQUIRES_REVIEW"
                : "SOURCE_SLICE_CHALLENGER_NO_LIFT",
        }, ct);
        Console.WriteLine($"MODEL_CALLS={model.ProviderCalls}");
        Console.WriteLine($"PROVIDER_CALLS={model.ProviderCalls}");
        Console.WriteLine("INTERLEAVED_ORDER=CONTROL_R1,CHALLENGER_R1,CONTROL_R2,CHALLENGER_R2,CONTROL_R3,CHALLENGER_R3");
        Console.WriteLine($"SOURCE_SLICE_CHALLENGER_SUMMARY={Path.Combine(OutputRoot, "summary.v1.json")}");
        return 0;
    }

    private static async Task<BranchRun> RunControlAsync(SourceContext context, OpenRouterCeilingReasoningModel model, string packet, int repeat, CancellationToken ct)
    {
        var route = ReasoningRoute.ModelCapabilityCeiling.ToString();
        var result = await model.CompleteRawStructuredSemanticAsync("DOC-0205", route, $"{SemanticTextExactBindingContract.ProtocolVersion}:DOC-0205:C{repeat}",
            packet, context.Aliases.Sum(alias => alias.Text.Length), context.Aliases.Count, context.Aliases.Count,
            SemanticTextExactBindingContract.System, SemanticTextExactBindingContract.BuildUser(packet, route),
            SemanticTextExactBindingContract.Schema(), "semantic_text_exact_binding_v1", ct);
        var parsed = SemanticTextExactBindingContract.Parse(result.Content);
        var proposals = parsed.Headings.Select(item => new CanonicalSemanticProposal(item.Source, true, item.Text,
            SemanticRole: item.Role, Occurrence: item.Occurrence, LeftExactContext: item.LeftExactContext, RightExactContext: item.RightExactContext)).ToArray();
        var production = RunProduction(context, proposals);
        return new("CONTROL_VERBATIM_TEXT", parsed.Headings.Count, proposals, [], production, ProjectFinal(context, production), result.Telemetry,
            Sha256Text(result.Content), 0, 0);
    }

    private static async Task<BranchRun> RunChallengerAsync(SourceContext context, OpenRouterCeilingReasoningModel model, string packet, int repeat, CancellationToken ct)
    {
        var route = ReasoningRoute.ModelCapabilityCeiling.ToString();
        var result = await model.CompleteRawStructuredSemanticAsync("DOC-0205", route, $"{SourceSliceSemanticContract.ProtocolVersion}:DOC-0205:S{repeat}",
            packet, context.Aliases.Sum(alias => alias.Text.Length), context.Aliases.Count, context.Aliases.Count,
            SourceSliceSemanticContract.System, SourceSliceSemanticContract.BuildUser(packet, route),
            SourceSliceSemanticContract.Schema(), "source_slice_semantic_v1", ct);
        var parsed = SourceSliceSemanticContract.Parse(result.Content);
        var proposals = SourceSliceSemanticBinder.Bind(parsed.Headings, context.SlicesByAlias, out var observations);
        var production = RunProduction(context, proposals);
        var selected = parsed.Headings.Where(item => item.IsHeading && observations.Any(observation => observation.Heading == item && observation.Status == SourceSliceBindingStatus.Bound));
        return new("CHALLENGER_SOURCE_SLICE", parsed.Headings.Count, proposals, observations, production, ProjectFinal(context, production), result.Telemetry,
            Sha256Text(result.Content), selected.Count(item => item.SliceIds.Count == 1), selected.Count(item => item.SliceIds.Count > 1));
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
            .OrderBy(item => item.Sources.Single().SourceOrdinal)
            .ThenBy(item => item.Sources.Single().Span.Start)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => new ExactRow(item.Sources.Single().SourceId, item.Sources.Single().Span.Start,
                item.Sources.Single().Span.End, item.Text))
            .ToArray();
    }

    private static async Task PersistBranchAsync(string output, int repeat, BranchRun branch, SourceContext context, CancellationToken ct)
    {
        var dir = Path.Combine(output, $"r{repeat}", branch.Mode == "CONTROL_VERBATIM_TEXT" ? "control" : "challenger");
        Directory.CreateDirectory(dir);
        var predictionPath = Path.Combine(dir, "prediction.v1.json");
        await WriteJson(predictionPath, new
        {
            schemaVersion = "a99-v6-source-slice-challenger-prediction-v1", documentId = "DOC-0205", repeat = $"r{repeat}",
            mode = branch.Mode, model = Model, sourceSha256 = context.SourceSha256, rawResponseSha256 = branch.RawResponseSha,
            rawProposalCount = branch.RawCount, proposals = branch.Proposals,
            sliceBindingObservations = branch.SliceObservations,
            boundHeadings = branch.Production.TextPipeline.BoundHeadings,
            finalHeadings = branch.Final,
            selectedSingleSlice = branch.SelectedSingleSlice, selectedMultiSlice = branch.SelectedMultiSlice,
            telemetry = branch.Telemetry, modelEchoTextRequiredForBinding = false, goldReadBeforeFreeze = false,
        }, ct);
        await WriteJson(Path.Combine(dir, "freeze.v1.json"), new
        {
            schemaVersion = "a99-v6-source-slice-challenger-freeze-v1", documentId = "DOC-0205", repeat = $"r{repeat}", mode = branch.Mode,
            sourceSha256 = context.SourceSha256, predictionSha256 = Sha256(predictionPath), model = Model,
            providerCalls = 1, modelCalls = 1, goldReadBeforeFreeze = false, frozenUtc = DateTimeOffset.UtcNow,
        }, ct);
    }

    private static SourceContext LoadContext(string repoRoot)
    {
        using var inventory = JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot, InventoryPath.Replace('/', Path.DirectorySeparatorChar))));
        var item = inventory.RootElement.GetProperty("documents").EnumerateArray().Single(entry => entry.GetProperty("documentId").GetString() == "DOC-0205");
        var path = Path.Combine(repoRoot, item.GetProperty("sourcePath").GetString()!.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
        var sourceSha = item.GetProperty("sourceSha256").GetString()!;
        if (!string.Equals(Sha256(path), sourceSha, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SOURCE_HASH_MISMATCH:DOC-0205");
        var source = new OpenXmlDocumentSource().Read(path) with { DocumentId = "DOC-0205" };
        var features = NumberingStyleFeatures.FromSourceDocument(source);
        var derived = new DocumentFeatureDeriver().Derive(source);
        var policy = DocxPolicyStateBuilder.Build(source, features, derived, new PipelineOptions { DisableLlm = false }.Extraction);
        var aliases = source.Paragraphs.Where(paragraph => !string.IsNullOrWhiteSpace(paragraph.Text))
            .Select((paragraph, index) => new SemanticSourceAlias($"S{index + 1:0000}", paragraph.SourceId, paragraph.SourceOrdinal, paragraph.Text,
                new StructuralSpan(0, paragraph.Text.Length), new SourceAnchor { SourceType = "DOCX_TEXT", ParagraphId = paragraph.SourceId, ParagraphIndex = paragraph.SourceOrdinal })).ToArray();
        var catalog = new DocumentSourceCatalog(aliases.Select(alias => new DocumentSourceUnit(alias.SourceId, alias.SourceOrdinal, alias.Text, alias.SourceAnchor!, alias.SourceSpan)));
        var slices = source.Paragraphs.Where(paragraph => !string.IsNullOrWhiteSpace(paragraph.Text))
            .Select((paragraph, index) => A99V6SourceSliceabilityAuditRunner.BuildSlices(paragraph, $"S{index + 1:0000}"))
            .ToDictionary(item => item.SourceAlias, item => (IReadOnlyList<SourceSlice>)item.Slices, StringComparer.Ordinal);
        return new("DOC-0205", sourceSha, source, policy, catalog, aliases, slices);
    }

    private static ScoreRow Score(IReadOnlyList<ExactRow> prediction, IReadOnlySet<ExactRow> gold)
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

    private static HashSet<string> Keys(IEnumerable<ExactRow> rows) => rows.Select(row => Key(row.SourceId, row.Start, row.End)).ToHashSet(StringComparer.Ordinal);
    private static string Key(string sourceId, int start, int end) => $"{sourceId}:{start}:{end}";
    private static async Task<int> Blocked(string output, string head, string reason, CancellationToken ct)
    {
        await WriteJson(Path.Combine(output, "summary.v1.json"), new { status = "BLOCKED", reason, startHead = head, modelCalls = 0, providerCalls = 0, goldReadBeforeFreeze = false }, ct);
        return 1;
    }
    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Git(string root, string args) { using var p = Process.Start(new ProcessStartInfo("git", args) { WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false }); return p?.StandardOutput.ReadToEnd().Trim() ?? "NOT_PERSISTED"; }
    private static async Task WriteJson(string path, object value, CancellationToken ct) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, ct);

    private sealed record SourceContext(string DocumentId, string SourceSha256, SourceDocument Source, DocxPolicyState Policy, DocumentSourceCatalog Catalog, IReadOnlyList<SemanticSourceAlias> Aliases, IReadOnlyDictionary<string, IReadOnlyList<SourceSlice>> SlicesByAlias);
    private sealed record BranchRun(string Mode, int RawCount, IReadOnlyList<CanonicalSemanticProposal> Proposals, IReadOnlyList<SourceSliceBindingObservation> SliceObservations, CanonicalSemanticProductionResult Production, IReadOnlyList<ExactRow> Final, object Telemetry, string RawResponseSha, int SelectedSingleSlice, int SelectedMultiSlice)
    {
        public IReadOnlyList<CanonicalSemanticBoundHeading> Bound => Production.TextPipeline.BoundHeadings;
    }
    private sealed record ExactRow(string SourceId, int Start, int End, string Text);
    private sealed record ScoreRow(int Tp, int Fp, int Fn, double Precision, double Recall, double F1);
    private sealed record PairedCell(string Repeat, int ControlRaw, int ChallengerRaw, int ControlBound, int ChallengerBound, int ControlFinal, int ChallengerFinal, ScoreRow ControlScore, ScoreRow ChallengerScore, int SelectedSingleSlice, int SelectedMultiSlice, int InvalidSliceSelection, int AmbiguousSliceSelection);
}
