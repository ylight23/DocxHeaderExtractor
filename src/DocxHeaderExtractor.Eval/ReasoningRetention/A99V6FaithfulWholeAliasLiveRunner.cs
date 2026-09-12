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
/// Interleaved live paired contract test on the faithful DOCX source. V uses the existing
/// VERBATIM_TEXT contract and W uses the same semantic task with WHOLE_ALIAS output. Both branches
/// use the same source packet, model, provider setup, and downstream exact binder.
/// </summary>
public static class A99V6FaithfulWholeAliasLiveRunner
{
    private const string Model = "qwen/qwen3.7-flash";
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const string OutputRoot = "eval/a99-closed-loop/source-fidelity-whole-alias-live/DOC-0205";
    private const string SourcePath = "eval/a99-closed-loop/source-fidelity-audit/DOC-0205/converted-docx/025_ND_47-2020_Chia_se_du_lieu_so.docx";
    private const string FidelityGoldPath = "eval/a99-closed-loop/source-fidelity-audit/DOC-0205/gold-representability.v1.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string WholeAliasSystem = $"""
You identify every structurally real document heading or structural label in the supplied source.
A single source occurrence may contain zero, one, or many independent headings. Decide semantic
existence and role yourself from the complete source. Formatting, numbering, and layout are
evidence, not rules. Return only headings you discover in the supplied source.

For every heading, return:
- source: the supplied short source alias, copied exactly
- selectionMode: WHOLE_ALIAS
- role: one allowed semantic role

WHOLE_ALIAS means the harness will use the complete text of the selected source alias. Do not
return text, character offsets, source IDs, hierarchy, confidence, explanations, or chain-of-thought.
Do not invent or normalize source text. Do not use Gold. Return each semantic heading at most once.

Allowed roles: {string.Join(", ", CeilingSemanticRole.AllowedRoles)}.
Return only the JSON object described by the schema.
""";

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(output);
        var context = LoadContext(repoRoot);
        var packet = BuildPacket(context);
        var startHead = GitSha(repoRoot);
        await WriteJson(Path.Combine(output, "manifest.v1.json"), new
        {
            schemaVersion = "a99-v6-faithful-whole-alias-live-manifest-v1",
            documentId = "DOC-0205", model = Model, sourceSha256 = context.SourceSha256,
            sourceAliasCount = context.Aliases.Count, packetCharacters = packet.Length,
            executionOrder = new[] { "V1", "W1", "V2", "W2", "V3", "W3" },
            repeats = 3, interleaved = true, modelCalls = 0, providerCalls = 0,
            causalDelta = "OUTPUT_CONTRACT_ONLY",
            controlContract = "VERBATIM_TEXT",
            challengerContract = "WHOLE_ALIAS",
            modelTextRequiredForBinding = new { control = true, challenger = false },
            modelNumericOffsets = false, candidateGeneration = false, recoveryPass = false,
            goldReadBeforeFreeze = false,
            semanticDefinitionControlSha256 = Sha256Text(SemanticTextExactBindingContract.System),
            semanticDefinitionChallengerSha256 = Sha256Text(WholeAliasSystem),
            controlSchemaSha256 = Sha256Text(JsonSerializer.Serialize(SemanticTextExactBindingContract.Schema())),
            challengerSchemaSha256 = Sha256Text(JsonSerializer.Serialize(WholeAliasSchema())),
        }, ct);

        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) return await Blocked(output, startHead, "OPENROUTER_API_KEY_MISSING", ct);
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
        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(repoRoot, "faithful-whole-alias-live", "DOC-0205", ct);
        using var model = new OpenRouterCeilingReasoningModel(options, capability.Capability, http);
        var branches = new List<BranchRun>();

        // Interleaved pair: V1, W1, V2, W2, V3, W3.
        for (var repeat = 1; repeat <= 3; repeat++)
        {
            var pairTask = $"A99V6FaithfulWholeAliasLive:DOC-0205:R{repeat}";
            var verbatim = await RunBranchAsync(context, packet, repeat, "FAITHFUL_VERBATIM_TEXT", pairTask, model, false, ct);
            await PersistAsync(output, verbatim, ct);
            branches.Add(verbatim);
            var whole = await RunBranchAsync(context, packet, repeat, "FAITHFUL_WHOLE_ALIAS", pairTask, model, true, ct);
            await PersistAsync(output, whole, ct);
            branches.Add(whole);
        }

        // Gold is opened only after all six live predictions have been frozen.
        var gold = LoadComparableGold(repoRoot);
        var scored = branches.Select(branch =>
        {
            var score = Score(branch.Final, gold.Rows, gold.ExcludedSourceIds);
            var metrics = Metrics(branch);
            return new ScoredBranch(branch, score with
            {
                BindingFailure = metrics.BindingFailure,
                UnknownAliasSelections = metrics.UnknownAliasSelections,
                SystemLoss = metrics.SystemLoss,
            });
        }).ToArray();
        foreach (var branch in scored)
        {
            await WriteJson(Path.Combine(output, $"r{branch.Branch.Repeat}", branch.Branch.IsWholeAlias ? "whole-alias" : "verbatim-text", "score.v1.json"), new
            {
                schemaVersion = "a99-v6-faithful-whole-alias-live-score-v1",
                documentId = "DOC-0205", repeat = $"r{branch.Branch.Repeat}",
                mode = branch.Branch.Mode, comparableGoldCount = gold.Rows.Count,
                faithfulCrossParagraphGold = gold.CrossParagraphCount, score = branch.Score,
                goldReadBeforeFreeze = false,
            }, ct);
        }
        var verbatimAggregate = Aggregate(scored.Where(item => !item.Branch.IsWholeAlias).Select(item => item.Score));
        var wholeAggregate = Aggregate(scored.Where(item => item.Branch.IsWholeAlias).Select(item => item.Score));
        await WriteJson(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-v6-faithful-whole-alias-live-summary-v1",
            documentId = "DOC-0205", model = Model, startHead, endHead = GitSha(repoRoot),
            sourceSha256 = context.SourceSha256, sourceAliasCount = context.Aliases.Count,
            packetCharacters = packet.Length, interleaved = true, repeats = 3,
            modelCalls = model.ProviderCalls, providerCalls = model.ProviderCalls,
            executionOrder = new[] { "V1", "W1", "V2", "W2", "V3", "W3" },
            control = new { contract = "VERBATIM_TEXT", aggregate = verbatimAggregate },
            challenger = new { contract = "WHOLE_ALIAS", aggregate = wholeAggregate },
            // Keep the campaign summary forensic but bounded. BranchRun carries the full
            // source context and packet for execution; serializing it here duplicated the
            // 144K-character packet six times and produced a 41 MB summary artifact.
            rows = scored.Select(item => new SummaryRow(
                item.Branch.Repeat, item.Branch.Mode, item.Branch.IsWholeAlias,
                item.Score, Metrics(item.Branch), item.Branch.RawResponseSha256,
                item.Branch.Telemetry.ProviderRoute, item.Branch.Telemetry.FinishReason,
                item.Branch.Telemetry.ReportedInputTokens, item.Branch.Telemetry.ReportedReasoningTokens,
                item.Branch.Telemetry.ReportedOutputTokens)).ToArray(),
            acceptance = new
            {
                unknownAlias = wholeAggregate.UnknownAliasSelections == 0,
                bindFailure = wholeAggregate.BindingFailure == 0,
                numericOffsets = false,
                modelTextRequired = false,
                systemLoss = wholeAggregate.SystemLoss == 0,
                comparableRecallAtLeast95 = wholeAggregate.Recall >= .95,
                precisionNoSignificantRegression = wholeAggregate.Precision >= verbatimAggregate.Precision - .05,
            },
            goldFirewall = "PASS",
            decision = wholeAggregate.Recall >= .95 && wholeAggregate.Precision >= verbatimAggregate.Precision - .05 &&
                       wholeAggregate.BindingFailure == 0 && wholeAggregate.SystemLoss == 0
                ? "WHOLE_ALIAS_LIVE_KEEP_CANDIDATE"
                : "WHOLE_ALIAS_LIVE_REQUIRES_REVIEW",
        }, ct);
        Console.WriteLine($"MODEL_CALLS={model.ProviderCalls}");
        Console.WriteLine($"PROVIDER_CALLS={model.ProviderCalls}");
        Console.WriteLine("INTERLEAVED_ORDER=V1,W1,V2,W2,V3,W3");
        Console.WriteLine($"FAITHFUL_WHOLE_ALIAS_LIVE_SUMMARY={Path.Combine(OutputRoot, "summary.v1.json")}");
        return 0;
    }

    private static async Task<BranchRun> RunBranchAsync(SourceContext context, string packet, int repeat, string mode, string task, OpenRouterCeilingReasoningModel model, bool wholeAlias, CancellationToken ct)
    {
        var result = await model.CompleteRawStructuredSemanticAsync(
            "DOC-0205", ReasoningRoute.ModelCapabilityCeiling.ToString(), task, packet,
            context.Aliases.Sum(alias => alias.Text.Length), context.Aliases.Count, context.Aliases.Count,
            wholeAlias ? WholeAliasSystem : SemanticTextExactBindingContract.System,
            wholeAlias ? BuildWholeAliasUser(packet) : SemanticTextExactBindingContract.BuildUser(packet, ReasoningRoute.ModelCapabilityCeiling.ToString()),
            wholeAlias ? WholeAliasSchema() : SemanticTextExactBindingContract.Schema(),
            "faithful-whole-alias-live", ct);
        var proposals = wholeAlias ? ParseWholeAlias(result.Content) : ParseVerbatim(result.Content);
        var production = RunProduction(context, proposals);
        var final = ProjectFinal(context, production);
        return new(repeat, wholeAlias ? "WHOLE_ALIAS" : "VERBATIM_TEXT", wholeAlias, proposals, final, production,
            result.Telemetry, Sha256Text(result.Content)) { Context = context, Packet = packet };
    }

    private static IReadOnlyList<CanonicalSemanticProposal> ParseVerbatim(string content)
    {
        var parsed = SemanticTextExactBindingContract.Parse(content);
        return parsed.Headings.Select(item => new CanonicalSemanticProposal(item.Source, true, item.Text,
            SemanticRole: item.Role, Occurrence: item.Occurrence, LeftExactContext: item.LeftExactContext,
            RightExactContext: item.RightExactContext)).ToArray();
    }

    private static IReadOnlyList<CanonicalSemanticProposal> ParseWholeAlias(string content)
    {
        using var document = JsonDocument.Parse(JsonObjectText(content));
        var array = document.RootElement.GetProperty("headings");
        var result = new List<CanonicalSemanticProposal>();
        foreach (var item in array.EnumerateArray())
        {
            var source = item.GetProperty("source").GetString()!;
            var isHeading = item.GetProperty("isHeading").GetBoolean();
            var role = item.GetProperty("role").GetString()!;
            var selectionMode = item.GetProperty("selectionMode").GetString();
            if (!CeilingSemanticRole.IsAllowed(role) || !string.Equals(selectionMode, CanonicalSemanticSelectionMode.WholeAlias, StringComparison.Ordinal))
                throw new FormatException("whole-alias-response-schema-invalid");
            result.Add(new CanonicalSemanticProposal(source, isHeading, null, SemanticRole: role,
                SelectionMode: CanonicalSemanticSelectionMode.WholeAlias));
        }
        return result;
    }

    private static string JsonObjectText(string content)
    {
        var start = content.IndexOf('{'); var end = content.LastIndexOf('}');
        if (start < 0 || end < start) throw new FormatException("whole-alias-response-json-incomplete");
        return content[start..(end + 1)];
    }

    private static object WholeAliasSchema() => new
    {
        type = "object", additionalProperties = false,
        properties = new
        {
            headings = new
            {
                type = "array",
                items = new
                {
                    type = "object", additionalProperties = false,
                    properties = new
                    {
                        source = new { type = "string", minLength = 1 },
                        selectionMode = new { type = "string", @enum = new[] { CanonicalSemanticSelectionMode.WholeAlias } },
                        isHeading = new { type = "boolean" },
                        role = new { type = "string", @enum = CeilingSemanticRole.AllowedRoles },
                    },
                    required = new[] { "source", "selectionMode", "isHeading", "role" },
                },
            },
        },
        required = new[] { "headings" },
    };

    private static string BuildWholeAliasUser(string packet) => $"TASK=a99-faithful-whole-alias-v1\nroute={ReasoningRoute.ModelCapabilityCeiling}\n{packet}";

    private static SourceContext LoadContext(string repoRoot)
    {
        var path = Path.Combine(repoRoot, SourcePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) throw new InvalidDataException("FAITHFUL_SOURCE_MISSING");
        var source = new OpenXmlDocumentSource().Read(path) with { DocumentId = "DOC-0205" };
        var hash = Sha256(path);
        var features = NumberingStyleFeatures.FromSourceDocument(source);
        var derived = new DocumentFeatureDeriver().Derive(source);
        var policy = DocxPolicyStateBuilder.Build(source, features, derived, new PipelineOptions { DisableLlm = false }.Extraction);
        var aliases = source.Paragraphs.Where(item => !string.IsNullOrWhiteSpace(item.Text))
            .Select((item, index) => new SemanticSourceAlias($"S{index + 1:0000}", item.SourceId, item.SourceOrdinal, item.Text,
                new StructuralSpan(0, item.Text.Length), new SourceAnchor { SourceType = "DOCX_TEXT", ParagraphId = item.SourceId, ParagraphIndex = item.SourceOrdinal })).ToArray();
        var catalog = new DocumentSourceCatalog(aliases.Select(alias => new DocumentSourceUnit(alias.SourceId, alias.SourceOrdinal, alias.Text, alias.SourceAnchor!, alias.SourceSpan)));
        return new(hash, source, policy, catalog, aliases);
    }

    private static string BuildPacket(SourceContext context) => JsonSerializer.Serialize(new
    {
        sourceAliases = context.Aliases.Select(alias => new { alias = alias.Alias, text = alias.Text, sourceOrdinal = alias.SourceOrdinal }).ToArray(),
    });

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
        var gold = new List<ExactRow>(); var excluded = new HashSet<string>(StringComparer.Ordinal); var cross = 0;
        foreach (var row in json.RootElement.GetProperty("rows").EnumerateArray())
        {
            var converted = row.GetProperty("converted");
            if (converted.GetProperty("status").GetString() == "EXACT_SINGLE_UNIT")
            {
                var sourceId = converted.GetProperty("sourceIds")[0].GetString()!;
                var text = row.GetProperty("text").GetString()!;
                gold.Add(new(sourceId, 0, text.Length, text));
            }
            else
            {
                cross++;
                foreach (var sourceId in converted.GetProperty("sourceIds").EnumerateArray()) excluded.Add(sourceId.GetString()!);
            }
        }
        return new(gold, cross, excluded);
    }

    private static ScoreRow Score(IReadOnlyList<ExactRow> prediction, IReadOnlyList<ExactRow> gold, IReadOnlySet<string> excludedSourceIds)
    {
        var p = prediction.Where(row => !excludedSourceIds.Contains(row.SourceId)).Select(row => Key(row.SourceId, row.Start, row.End)).ToHashSet(StringComparer.Ordinal);
        var g = gold.Select(row => Key(row.SourceId, row.Start, row.End)).ToHashSet(StringComparer.Ordinal);
        var tp = p.Intersect(g).Count(); var fp = p.Except(g).Count(); var fn = g.Except(p).Count();
        var precision = tp + fp == 0 ? 0d : (double)tp / (tp + fp); var recall = tp + fn == 0 ? 0d : (double)tp / (tp + fn);
        return new(tp, fp, fn, precision, recall, precision + recall == 0 ? 0d : 2 * precision * recall / (precision + recall),
            prediction.Where(row => excludedSourceIds.Contains(row.SourceId)).Select(row => Key(row.SourceId, row.Start, row.End)).Distinct(StringComparer.Ordinal).Count());
    }

    private static BranchMetrics Metrics(BranchRun branch)
    {
        var observations = branch.Production.TextPipeline.BindingObservations;
        var bound = branch.Production.TextPipeline.BoundHeadings.Select(item => Key(item.SourceId, item.Start, item.End)).ToHashSet(StringComparer.Ordinal);
        var final = branch.Final.Select(item => Key(item.SourceId, item.Start, item.End)).ToHashSet(StringComparer.Ordinal);
        return new(branch.Proposals.Count, branch.Production.TextPipeline.BoundHeadings.Count,
            branch.Final.Count, branch.Production.TextPipeline.BindingFailureCount,
            observations.Count(item => item.Status == CanonicalSemanticBindingStatus.UnknownAlias),
            bound.Except(final).Count(), branch.IsWholeAlias ? 0 : TextDrift(branch));
    }

    private static int TextDrift(BranchRun branch) => branch.Proposals.Count(proposal =>
        proposal.IsHeading && (!branch.Context.Aliases.Any(alias => alias.Alias == proposal.SourceAlias) ||
            proposal.VerbatimText is null || !branch.Context.Aliases.Single(alias => alias.Alias == proposal.SourceAlias).Text.Contains(proposal.VerbatimText, StringComparison.Ordinal)));

    private static ScoreRow Aggregate(IEnumerable<ScoreRow> rows)
    {
        var values = rows.ToArray(); var tp = values.Sum(item => item.Tp); var fp = values.Sum(item => item.Fp); var fn = values.Sum(item => item.Fn);
        var p = tp + fp == 0 ? 0d : (double)tp / (tp + fp); var r = tp + fn == 0 ? 0d : (double)tp / (tp + fn);
        return new(tp, fp, fn, p, r, p + r == 0 ? 0d : 2 * p * r / (p + r), values.Sum(item => item.ExcludedPredictions))
        {
            BindingFailure = values.Sum(item => item.BindingFailure),
            UnknownAliasSelections = values.Sum(item => item.UnknownAliasSelections),
            SystemLoss = values.Sum(item => item.SystemLoss),
        };
    }

    private static string Key(string sourceId, int start, int end) => $"{sourceId}:{start}:{end}";
    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string GitSha(string root) { using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD") { WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false }); return process?.StandardOutput.ReadToEnd().Trim() ?? "NOT_PERSISTED"; }
    private static async Task WriteJson(string path, object value, CancellationToken ct) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, new UTF8Encoding(false), ct);
    private static async Task<int> Blocked(string output, string head, string reason, CancellationToken ct)
    {
        await WriteJson(Path.Combine(output, "summary.v1.json"), new { status = "BLOCKED", reason, startHead = head, modelCalls = 0, providerCalls = 0, goldReadBeforeFreeze = false }, ct);
        return 1;
    }

    private static async Task PersistAsync(string output, BranchRun branch, CancellationToken ct)
    {
        var directory = Path.Combine(output, $"r{branch.Repeat}", branch.IsWholeAlias ? "whole-alias" : "verbatim-text");
        Directory.CreateDirectory(directory);
        var predictionPath = Path.Combine(directory, "prediction.v1.json");
        await WriteJson(predictionPath, new
        {
            schemaVersion = "a99-v6-faithful-whole-alias-live-prediction-v1",
            documentId = "DOC-0205", repeat = $"r{branch.Repeat}", mode = branch.Mode, model = Model,
            sourceSha256 = branch.Context.SourceSha256, sourceAliasCount = branch.Context.Aliases.Count,
            packetCharacters = branch.Packet.Length, modelCalls = 1, providerCalls = 1,
            rawResponseSha256 = branch.RawResponseSha256, rawProposalCount = branch.Proposals.Count,
            modelTextRequiredForBinding = !branch.IsWholeAlias, modelNumericOffsets = false,
            proposals = branch.Proposals, boundHeadings = branch.Production.TextPipeline.BoundHeadings,
            finalHeadings = branch.Final, telemetry = branch.Telemetry, metrics = Metrics(branch),
            goldReadBeforeFreeze = false,
        }, ct);
        await WriteJson(Path.Combine(directory, "freeze.v1.json"), new
        {
            schemaVersion = "a99-v6-faithful-whole-alias-live-freeze-v1",
            documentId = "DOC-0205", repeat = $"r{branch.Repeat}", mode = branch.Mode, model = Model,
            sourceSha256 = branch.Context.SourceSha256, predictionSha256 = Sha256(predictionPath),
            rawResponseSha256 = branch.RawResponseSha256, modelCalls = 1, providerCalls = 1,
            goldReadBeforeFreeze = false, frozenUtc = DateTimeOffset.UtcNow,
        }, ct);
    }

    private sealed record SourceContext(string SourceSha256, SourceDocument Source, DocxPolicyState Policy, DocumentSourceCatalog Catalog, IReadOnlyList<SemanticSourceAlias> Aliases);
    private sealed record ExactRow(string SourceId, int Start, int End, string Text);
    private sealed record GoldScope(IReadOnlyList<ExactRow> Rows, int CrossParagraphCount, IReadOnlySet<string> ExcludedSourceIds);
    private sealed record BranchRun(int Repeat, string Mode, bool IsWholeAlias, IReadOnlyList<CanonicalSemanticProposal> Proposals, IReadOnlyList<ExactRow> Final, CanonicalSemanticProductionResult Production, RequestPacketTelemetry Telemetry, string RawResponseSha256)
    {
        public SourceContext Context { get; init; } = null!;
        public string Packet { get; init; } = "";
    }
    private sealed record BranchMetrics(int RawProposalCount, int BoundCount, int FinalCount, int BindingFailure, int UnknownAliasSelections, int SystemLoss, int ModelTextDrift);
    private sealed record ScoreRow(int Tp, int Fp, int Fn, double Precision, double Recall, double F1, int ExcludedPredictions)
    {
        public int BindingFailure { get; init; }
        public int UnknownAliasSelections { get; init; }
        public int SystemLoss { get; init; }
    }
    private sealed record ScoredBranch(BranchRun Branch, ScoreRow Score);
    private sealed record SummaryRow(
        int Repeat, string Mode, bool IsWholeAlias, ScoreRow Score, BranchMetrics Metrics,
        string RawResponseSha256, string? ActualProvider, string? FinishReason,
        int? InputTokens, int? ReasoningTokens, int? OutputTokens);
}
