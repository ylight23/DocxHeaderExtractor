using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>A99-I6 isolates the joint discovery/role task. Pass A is the only authority for
/// heading existence and identity; Pass B can attach an allowed role to those frozen identities,
/// but cannot create, remove, or relocate one.</summary>
public static partial class SemanticTextGeneralizationRunner
{
    private const string I6OutputRoot = "eval/a99-closed-loop/task-decomposition/i6";
    private const string I6Experiment = "A99-I6";
    private const string I6DiscoveryVersion = "a99-i6-discovery-only-v1";
    private const string I6RoleVersion = "a99-i6-role-only-v1";
    private const string I6RoleSentinel = "OTHER_STRUCTURAL_LABEL";

    private static readonly string I6DiscoverySystem = """
You identify every structurally real document heading or structural label in the supplied source.
A single source occurrence may contain zero, one, or many independent headings. Decide semantic
existence yourself from the complete source. Formatting, numbering, and layout are evidence, not
rules. Return only headings you discover in the supplied source.

For every heading, return:
- source: the supplied short source alias, copied exactly
- text: the complete heading text copied verbatim from that source occurrence

The text field is a quotation used for exact deterministic binding. Do not normalize spelling,
punctuation, whitespace, numbering, or case. Do not generate a title. Do not return character
offsets, source IDs, hierarchy, role, confidence, explanations, or chain-of-thought. Do not return
text that is not an exact substring of the referenced source occurrence. Do not use Gold.

If identical text occurs more than once in the same source occurrence, either provide the 1-based
occurrence ordinal or provide exact leftExactContext/rightExactContext strings. Never guess an
ambiguous occurrence. Return each semantic heading at most once.

Return only the JSON object described by the schema.
""";

    private static readonly string I6RoleSystem = $"""
You classify roles for a frozen set of already-discovered document headings.
The heading identities are authoritative. Return exactly one role for every supplied headingId.
Do not add, remove, rename, relocate, or rewrite any heading. Do not return source, text, spans,
hierarchy, confidence, explanations, or chain-of-thought. Do not use Gold.

Allowed roles: {string.Join(", ", CeilingSemanticRole.AllowedRoles)}.
Return only the JSON object described by the schema.
""";

    private static object I6DiscoverySchema() => new
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
                        text = new { type = "string", minLength = 1 },
                        occurrence = new { type = "integer", minimum = 1 },
                        leftExactContext = new { type = "string" },
                        rightExactContext = new { type = "string" },
                    },
                    required = new[] { "source", "text" },
                },
            },
        },
        required = new[] { "headings" },
    };

    private static object I6RoleSchema() => new
    {
        type = "object", additionalProperties = false,
        properties = new
        {
            roles = new
            {
                type = "array",
                items = new
                {
                    type = "object", additionalProperties = false,
                    properties = new
                    {
                        headingId = new { type = "string", minLength = 1 },
                        role = new { type = "string", @enum = CeilingSemanticRole.AllowedRoles },
                    },
                    required = new[] { "headingId", "role" },
                },
            },
        },
        required = new[] { "roles" },
    };

    public static Task<int> RunTaskDecompositionE4Async(string repoRoot, CancellationToken ct = default)
    {
        var decision = Path.Combine(repoRoot, "eval/a99-closed-loop/task-decomposition/e4/decision.v1.json");
        if (!File.Exists(decision)) throw new InvalidDataException("E4_DECISION_MISSING");
        using var json = JsonDocument.Parse(File.ReadAllText(decision));
        if (json.RootElement.GetProperty("terminalClassification").GetString() != "DISCOVERY_ROLE_COUPLING_TEST_JUSTIFIED")
            throw new InvalidDataException("E4_DID_NOT_AUTHORIZE_I6");
        Console.WriteLine("E4_MODEL_CALLS=0");
        Console.WriteLine("E4_PROVIDER_CALLS=0");
        Console.WriteLine("E4_CLASSIFICATION=DISCOVERY_ROLE_COUPLING_TEST_JUSTIFIED");
        return Task.FromResult(0);
    }

    public static async Task<int> RunTaskDecompositionI6Async(string repoRoot, CancellationToken ct = default)
    {
        return await RunTaskDecompositionI6CoreAsync(repoRoot, resume: false, ct);
    }

    public static async Task<int> ResumeTaskDecompositionI6Async(string repoRoot, CancellationToken ct = default)
    {
        return await RunTaskDecompositionI6CoreAsync(repoRoot, resume: true, ct);
    }

    private static async Task<int> RunTaskDecompositionI6CoreAsync(string repoRoot, bool resume, CancellationToken ct)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, I6OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(output);
        var inventory = LoadInventory(repoRoot);
        var selected = inventory.Select(item => (item, eligibility: ReasoningGoldEligibilityEvaluator.EvaluateMetadataOnly(repoRoot, item.GetProperty("documentId").GetString()!)))
            .Where(x => x.eligibility.Eligible).OrderBy(x => x.item.GetProperty("documentId").GetString(), StringComparer.Ordinal).ToArray();
        if (selected.Length != 5) throw new InvalidDataException($"I6_COHORT_EXPECTED_5_GOT_{selected.Length}");
        var contexts = selected.Select(x => Prepare(repoRoot, x.item)).ToArray();
        var startHead = GitSha(repoRoot);
        var b0Root = Path.Combine(repoRoot, DefaultOutputRoot.Replace('/', Path.DirectorySeparatorChar));
        var sourcePacketHashes = contexts.Select(context =>
        {
            var freezes = Enumerable.Range(1, RepeatCount).Select(repeat => JsonDocument.Parse(File.ReadAllText(Path.Combine(b0Root, context.DocumentId, $"r{repeat}", "freeze.v1.json")))).ToArray();
            var source = freezes[0].RootElement.GetProperty("sourceSha256").GetString();
            var packet = freezes[0].RootElement.GetProperty("packetHash").GetString();
            if (freezes.Any(x => x.RootElement.GetProperty("sourceSha256").GetString() != source || x.RootElement.GetProperty("packetHash").GetString() != packet))
                throw new InvalidDataException($"B0_LINEAGE_DRIFT:{context.DocumentId}");
            foreach (var freeze in freezes) freeze.Dispose();
            if (source != context.SourceSha256 || packet != context.PacketHash) throw new InvalidDataException($"I6_SOURCE_PACKET_DELTA:{context.DocumentId}");
            return new { documentId = context.DocumentId, sourceSha256 = source, packetHash = packet };
        }).ToArray();
        var b0PromptHash = contexts.Select(x => x.PromptHash).Distinct(StringComparer.Ordinal).Single();
        var b0SchemaHash = contexts.Select(x => x.SchemaHash).Distinct(StringComparer.Ordinal).Single();
        var passAPromptHash = Sha256Text(I6DiscoveryVersion + "\n" + I6DiscoverySystem);
        var passASchemaHash = Sha256Text(JsonSerializer.Serialize(I6DiscoverySchema()));
        var passBPromptHash = Sha256Text(I6RoleVersion + "\n" + I6RoleSystem);
        var passBSchemaHash = Sha256Text(JsonSerializer.Serialize(I6RoleSchema()));
        var manifestPath = Path.Combine(output, "manifest.v1.json");
        if (!resume || !File.Exists(manifestPath))
        {
            await WriteJson(manifestPath, new
            {
                schemaVersion = "a99-a99-i6-discovery-role-decomposition-manifest-v1", experimentId = I6Experiment,
                parentCommit = startHead, behavioralParent = "B0@76c4e01", model = ControlModel,
                providerPolicy = new { provider = "OpenRouter", endpoint = Endpoint, route = "MODEL_DEFAULT", fallback = false, reasoningEnabled = true, structuredOutputRequired = true },
                sourcePacketHashes, sourceSha256 = contexts.Select(x => new { documentId = x.DocumentId, sha256 = x.SourceSha256 }).ToArray(),
                b0SemanticDefinitionHash = Sha256Text(SemanticTextExactBindingContract.System), b0PromptHash, b0SchemaHash,
                passAPromptHash, passASchemaHash, passBPromptHash, passBSchemaHash,
                binder = new { version = "deterministic-exact-utf16-binder-v1", hash = Sha256Text("SemanticTextExactBindingContract|deterministic UTF-16 exact binder|sourceAlias+verbatimText") },
                validator = new { version = "ReasoningProposalMaterializer-validator-v1", hash = Sha256Text("ReasoningProposalMaterializer|ReasoningTaskProjection|validator-v1") },
                cohort = new { documents = contexts.Select(x => x.DocumentId).ToArray(), documentCount = 5, cells = 15, repeats = 3, goldOccurrences = 153 },
                candidateGeneration = false, recoveryPass = false, contextExpansion = false,
                onlyCausalDelta = "JOINT_DISCOVERY_ROLE_TASK -> DISCOVERY_THEN_ROLE_CLASSIFICATION",
                preInferenceInvariants = new { modelDelta = 0, sourcePacketDelta = 0, sourceCoverageDelta = 0, contextDelta = 0, binderDelta = 0, goldDelta = 0, semanticDefinitionDelta = 0, taskDecompositionDelta = 1 },
                goldFirewall = new { goldReadBeforeFreeze = false, goldSuppliedToModel = false, evaluationAfterFinalFreezeOnly = true },
                repeats = 3, freshProviderCallsExpected = 30, resumeGranularity = "PASS",
            }, ct);
            Console.WriteLine($"I6_MANIFEST_FROZEN={Path.Combine(I6OutputRoot, "manifest.v1.json")}");
        }
        else
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (manifest.RootElement.GetProperty("experimentId").GetString() != I6Experiment ||
                manifest.RootElement.GetProperty("onlyCausalDelta").GetString() != "JOINT_DISCOVERY_ROLE_TASK -> DISCOVERY_THEN_ROLE_CLASSIFICATION")
                throw new InvalidDataException("I6_MANIFEST_MISMATCH");
        }

        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            await WriteJson(Path.Combine(output, "summary.v1.json"), new { schemaVersion = "a99-a99-i6-summary-v1", experimentId = I6Experiment, status = "PROVIDER_BLOCKED", reason = "OPENROUTER_API_KEY_MISSING", modelCalls = 0, providerCalls = 0, goldReadBeforeFreeze = false }, ct);
            return 1;
        }
        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
        var options = new RemoteInferenceOptions
        {
            Endpoint = new Uri(Endpoint), Model = ControlModel, ApiKey = apiKey, ContextSize = 1_000_000,
            MaxOutputTokens = 48_000, RequestTimeoutSeconds = 600, TransientRequestRetries = 0,
            MaxParallelRequests = 1, SendChatTemplateKwargs = false, OpenRouterAllowNonZdrPublicBenchmark = true,
        };
        var capabilityResult = await OpenRouterModelCapabilityResolver.ResolveAsync(options, http, ct);
        var capability = capabilityResult.Capability;
        if (capability is null || capability.ModelId != ControlModel || !capability.ReasoningSupported || !capability.StructuredOutputSupported)
        {
            await WriteJson(Path.Combine(output, "summary.v1.json"), new { schemaVersion = "a99-a99-i6-summary-v1", experimentId = I6Experiment, status = "PROVIDER_BLOCKED", reason = "MODEL_CAPABILITY_MISMATCH", capability = capabilityResult, modelCalls = 0, providerCalls = 0, goldReadBeforeFreeze = false }, ct);
            return 1;
        }
        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(repoRoot, I6OutputRoot, string.Join(',', contexts.Select(x => x.DocumentId)), ct);
        using var model = new OpenRouterCeilingReasoningModel(options, capability, http);
        var cells = new List<I6CellMetric>();
        foreach (var context in contexts)
            foreach (var repeat in Enumerable.Range(1, RepeatCount))
            {
                Console.WriteLine($"RUNNING={context.DocumentId}/R{repeat}");
                cells.Add(await RunI6CellAsync(repoRoot, output, context, repeat, model, startHead, ct));
            }

        var complete = cells.Count == 15 && cells.All(x => x.Status == "SUCCESS");
        var passA = cells.Where(x => x.Status == "SUCCESS").Select(x => x.PassA).ToArray();
        var final = cells.Where(x => x.Status == "SUCCESS").Select(x => x.Final).ToArray();
        var baseline = LoadI6Baseline(repoRoot, contexts);
        var targetKeys = I6StableTargets();
        var persistentMisses = complete ? targetKeys.Count(target => cells.Where(x => x.DocumentId == target.DocumentId).All(x => !x.PassAKeys.Contains(target.Key))) : -1;
        var persistentRecovered = complete ? targetKeys.Where(target => cells.Where(x => x.DocumentId == target.DocumentId).All(x => x.PassAKeys.Contains(target.Key))).Select(x => x.Key).ToArray() : Array.Empty<string>();
        var baselinePersistent = targetKeys.Select(x => x.Key).ToArray();
        var noRegression = complete && cells.Zip(baseline, (i, b) => i.PassA.F1 >= b.F1).All(x => x);
        var ambiguous = cells.Sum(x => x.BindAmbiguous);
        var systemLoss = cells.Sum(x => x.SystemLoss);
        var persistentFp = complete ? cells.SelectMany(x => x.PassAKeys.Except(x.GoldKeys)).GroupBy(x => x, StringComparer.Ordinal).Count(g => g.Count() == 15) : -1;
        var keep = complete && persistentMisses < 7 && persistentRecovered.Length > 0 && systemLoss == 0 && cells.Sum(x => x.BindFailure) == 0 && ambiguous <= baseline.Sum(x => x.BindAmbiguous) && persistentFp <= baseline.Sum(x => x.PersistentFp) && noRegression && cells.All(x => x.PassBIdentityMutation == false);
        var classification = keep ? "DISCOVERY_ROLE_DECOMPOSITION_KEEP" : complete ? "DISCOVERY_ROLE_DECOMPOSITION_REVERT" : "I6_PROVIDER_BLOCKED_INCOMPLETE";
        await WriteJson(Path.Combine(output, "per-cell.v1.json"), cells, ct);
        await WriteJson(Path.Combine(output, "pass-a-comparison.v1.json"), new
        {
            schemaVersion = "a99-a99-i6-pass-a-comparison-v1", experimentId = I6Experiment, baseline = baseline.Select(x => RepeatTable(x.Metric)).ToArray(),
            passA = passA.Select(x => x.ToReport()).ToArray(), micro = BuildI6Micro(cells, x => x.PassA), persistentB0OmissionCount = 7,
            persistentTargetMisses = persistentMisses, recoveredPersistentTargets = persistentRecovered, baselineTargets = baselinePersistent,
        }, ct);
        await WriteJson(Path.Combine(output, "role-diagnostics.v1.json"), new
        {
            schemaVersion = "a99-a99-i6-role-diagnostics-v1", rolePassCompleted = cells.Count(x => x.RolePassCompleted), rolePassBlocked = cells.Count(x => !x.RolePassCompleted),
            roleDistribution = cells.SelectMany(x => x.Roles).GroupBy(x => x, StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal),
            sameHeadingRoleStabilityAcrossRepeats = RoleStability(cells), modelRoleError = "NOT_MEASURED",
        }, ct);
        await WriteJson(Path.Combine(output, "comparison.v1.json"), new
        {
            schemaVersion = "a99-a99-i6-comparison-v1", experimentId = I6Experiment, behavioralParent = "B0@76c4e01",
            perCell = cells.Zip(baseline, (i, b) => new { documentId = i.DocumentId, repeat = i.Repeat, b0 = RepeatTable(b.Metric, "FULL_CONTEXT"), i6PassA = i.PassA.ToReport(), i6Final = i.Final.ToReport(), passBRoleCompleted = i.RolePassCompleted, passBIdentityMutation = i.PassBIdentityMutation }).ToArray(),
            b0Micro = BuildRepeatMicro(baseline.Select(x => x.Metric)), passAMicro = BuildI6Micro(cells, x => x.PassA), finalMicro = BuildI6Micro(cells, x => x.Final),
        }, ct);
        await WriteJson(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-a99-i6-summary-v1", experimentId = I6Experiment, status = complete ? "COMPLETE" : "INCOMPLETE",
            classification, startHead, endHead = GitSha(repoRoot), model = ControlModel, modelCalls = model.ProviderCalls, providerCalls = model.ProviderCalls,
            expectedProviderCalls = 30, completedCells = cells.Count(x => x.Status == "SUCCESS"), totalCells = 15, passACalls = cells.Count(x => x.PassACallCompleted), passBCalls = cells.Count(x => x.RolePassCompleted),
            persistentB0Omissions = 7, persistentPassAMisses = persistentMisses, recoveredPersistentTargets = persistentRecovered,
            bindAmbiguous = ambiguous, bindFailure = cells.Sum(x => x.BindFailure), systemLoss, persistentFalsePositives = persistentFp,
            modelRoleError = "NOT_MEASURED", goldFirewall = "PASS", keepGate = new { targetRecovery = persistentRecovered.Length > 0, noRegression, systemLoss, bindFailure = cells.Sum(x => x.BindFailure), classification },
        }, ct);
        Console.WriteLine($"I6_PROVIDER_CALLS={model.ProviderCalls}");
        Console.WriteLine($"I6_CLASSIFICATION={classification}");
        return keep ? 0 : complete ? 1 : 1;
    }

    private static async Task<I6CellMetric> RunI6CellAsync(string repoRoot, string output, DocumentContext context, int repeat, OpenRouterCeilingReasoningModel model, string gitSha, CancellationToken ct)
    {
        var repeatName = $"r{repeat}";
        var dir = Path.Combine(output, context.DocumentId, repeatName);
        var passADir = Path.Combine(dir, "pass-a");
        var passBDir = Path.Combine(dir, "pass-b");
        Directory.CreateDirectory(passADir); Directory.CreateDirectory(passBDir);
        var stopwatch = Stopwatch.StartNew();
        RequestPacketTelemetry? passATelemetry = null;
        RequestPacketTelemetry? passBTelemetry = null;
        var passACall = false; var passBCompleted = false; var roles = new List<string>();
        IReadOnlyList<SemanticTextBoundHeading> bound;
        IReadOnlyList<SemanticTextHeading> discovery;
        IReadOnlyList<SemanticTextBindingObservation> observations;
        try
        {
            var passAPredictionPath = Path.Combine(passADir, "prediction.v1.json");
            if (File.Exists(Path.Combine(passADir, "freeze.v1.json")) && File.Exists(passAPredictionPath))
            {
                using var frozen = JsonDocument.Parse(File.ReadAllText(Path.Combine(passADir, "freeze.v1.json")));
                if (Sha256(passAPredictionPath) != frozen.RootElement.GetProperty("predictionSha256").GetString()) throw new InvalidDataException("I6_PASS_A_FREEZE_HASH_MISMATCH");
                using var saved = JsonDocument.Parse(File.ReadAllText(passAPredictionPath));
                discovery = ParseI6DiscoveryFromPersisted(saved.RootElement.GetProperty("headings"));
                bound = SemanticTextExactBinder.Bind(discovery, context.SourceRows, out observations);
                Console.WriteLine($"REUSE_PASS_A={context.DocumentId}/R{repeat}");
            }
            else
            {
                var requestId = $"{I6DiscoveryVersion}:{context.DocumentId}:{repeat}:{context.PacketHash}";
                var provider = await model.CompleteRawStructuredSemanticAsync(context.DocumentId, ReasoningRoute.ModelCapabilityCeiling.ToString(), requestId, context.Packet, context.SourceRows.Sum(x => x.RawText.Length), context.SourceRows.Count, context.SourceRows.Count, I6DiscoverySystem, $"TASK={I6DiscoveryVersion}\nroute={ReasoningRoute.ModelCapabilityCeiling}\n{context.Packet}", I6DiscoverySchema(), I6DiscoveryVersion, ct);
                passATelemetry = provider.Telemetry; passACall = true;
                discovery = ParseI6Discovery(provider.Content);
                var sentinel = discovery.Select(x => x with { Role = I6RoleSentinel }).ToArray();
                bound = SemanticTextExactBinder.Bind(sentinel, context.SourceRows, out observations);
                await WriteJson(Path.Combine(passADir, "raw-response.v1.json"), new { schemaVersion = "a99-a99-i6-pass-a-raw-response-v1", context.DocumentId, repeat = repeatName, content = provider.Content, goldReadBeforeFreeze = false }, ct);
                await WriteJson(passAPredictionPath, new { schemaVersion = "a99-a99-i6-pass-a-prediction-v1", experimentId = I6Experiment, context.DocumentId, repeat = repeatName, model = ControlModel, sourceSha256 = context.SourceSha256, packetHash = context.PacketHash, promptHash = Sha256Text(I6DiscoveryVersion + "\n" + I6DiscoverySystem), schemaHash = Sha256Text(JsonSerializer.Serialize(I6DiscoverySchema())), headings = discovery, boundHeadings = bound, bindingObservations = observations, roleSentinelHarnessOnly = true, goldReadBeforeFreeze = false }, ct);
                await WriteJson(Path.Combine(passADir, "result.v1.json"), new { schemaVersion = "a99-a99-i6-pass-a-result-v1", context.DocumentId, repeat = repeatName, headings = bound.Select((x, i) => new { headingId = $"H{i + 1:0000}", sourceId = x.SourceId, start = x.Start, end = x.End, text = x.Text }).ToArray(), goldReadBeforeFreeze = false }, ct);
                await WriteJson(Path.Combine(passADir, "freeze.v1.json"), new { schemaVersion = "a99-a99-i6-pass-a-freeze-v1", experimentId = I6Experiment, context.DocumentId, repeat = repeatName, gitSha, model = ControlModel, sourceSha256 = context.SourceSha256, packetHash = context.PacketHash, predictionSha256 = Sha256(passAPredictionPath), resultSha256 = Sha256(Path.Combine(passADir, "result.v1.json")), headingIdentityCount = bound.Count, provider = provider.Telemetry.ProviderRoute, finishReason = provider.Telemetry.FinishReason, inputTokens = provider.Telemetry.ReportedInputTokens, reasoningTokens = provider.Telemetry.ReportedReasoningTokens, outputTokens = provider.Telemetry.ReportedOutputTokens, roleSentinelHarnessOnly = true, goldReadBeforeFreeze = false, frozenUtc = DateTimeOffset.UtcNow }, ct);
                if (Sha256(passAPredictionPath) != JsonDocument.Parse(File.ReadAllText(Path.Combine(passADir, "freeze.v1.json"))).RootElement.GetProperty("predictionSha256").GetString()) throw new InvalidDataException("I6_PASS_A_FREEZE_VERIFY_FAILED");
            }

            var identities = bound.Select((x, i) => new { headingId = $"H{i + 1:0000}", source = x.Alias, text = x.Text }).ToArray();
            var rolePredictionPath = Path.Combine(passBDir, "prediction.v1.json");
            if (File.Exists(Path.Combine(passBDir, "freeze.v1.json")) && File.Exists(rolePredictionPath))
            {
                using var saved = JsonDocument.Parse(File.ReadAllText(rolePredictionPath));
                roles = ParseI6Roles(saved.RootElement.GetProperty("roles"), identities.Select(x => x.headingId).ToArray());
                passBCompleted = true;
                Console.WriteLine($"REUSE_PASS_B={context.DocumentId}/R{repeat}");
            }
            else
            {
                var user = $"TASK={I6RoleVersion}\nroute={ReasoningRoute.ModelCapabilityCeiling}\nSOURCE_CONTEXT={context.Packet}\nFROZEN_HEADINGS={JsonSerializer.Serialize(identities)}";
                var requestId = $"{I6RoleVersion}:{context.DocumentId}:{repeat}:{context.PacketHash}:{bound.Count}";
                var provider = await model.CompleteRawStructuredSemanticAsync(context.DocumentId, ReasoningRoute.ModelCapabilityCeiling.ToString(), requestId, context.Packet, context.SourceRows.Sum(x => x.RawText.Length), context.SourceRows.Count, context.SourceRows.Count, I6RoleSystem, user, I6RoleSchema(), I6RoleVersion, ct);
                passBTelemetry = provider.Telemetry;
                roles = ParseI6Roles(ParseJsonObject(provider.Content).GetProperty("roles"), identities.Select(x => x.headingId).ToArray());
                passBCompleted = true;
                await WriteJson(Path.Combine(passBDir, "raw-response.v1.json"), new { schemaVersion = "a99-a99-i6-pass-b-raw-response-v1", context.DocumentId, repeat = repeatName, content = provider.Content, goldReadBeforeFreeze = false }, ct);
                await WriteJson(rolePredictionPath, new { schemaVersion = "a99-a99-i6-pass-b-prediction-v1", experimentId = I6Experiment, context.DocumentId, repeat = repeatName, roles = identities.Zip(roles, (identity, role) => new { headingId = identity.headingId, role }).ToArray(), goldReadBeforeFreeze = false }, ct);
                await WriteJson(Path.Combine(passBDir, "result.v1.json"), new { schemaVersion = "a99-a99-i6-pass-b-result-v1", context.DocumentId, repeat = repeatName, roles = identities.Zip(roles, (identity, role) => new { headingId = identity.headingId, role }).ToArray(), headingIdentityMutation = false, goldReadBeforeFreeze = false }, ct);
                await WriteJson(Path.Combine(passBDir, "freeze.v1.json"), new { schemaVersion = "a99-a99-i6-pass-b-freeze-v1", experimentId = I6Experiment, context.DocumentId, repeat = repeatName, gitSha, model = ControlModel, sourceSha256 = context.SourceSha256, packetHash = context.PacketHash, predictionSha256 = Sha256(rolePredictionPath), resultSha256 = Sha256(Path.Combine(passBDir, "result.v1.json")), frozenHeadingCount = bound.Count, roleCount = roles.Count, provider = provider.Telemetry.ProviderRoute, finishReason = provider.Telemetry.FinishReason, inputTokens = provider.Telemetry.ReportedInputTokens, reasoningTokens = provider.Telemetry.ReportedReasoningTokens, outputTokens = provider.Telemetry.ReportedOutputTokens, headingIdentityMutation = false, goldReadBeforeFreeze = false, frozenUtc = DateTimeOffset.UtcNow }, ct);
            }

            if (roles.Count != bound.Count) throw new FormatException("ROLE_PASS_COUNT_MISMATCH");
            var finalHeadings = bound.Select((x, i) => new SemanticTextHeading(x.Alias, x.Text, roles[i])).ToArray();
            var proposals = finalHeadings.Select(x =>
            {
                var row = context.SourceRows.Single(r => r.Alias == x.Source);
                var start = row.RawText.IndexOf(x.Text, StringComparison.Ordinal);
                return new ReasoningHeadingProposal { SourceId = row.SourceId, HeadingSpan = new StructuralSpan(start, start + x.Text.Length), Text = x.Text, SemanticRole = x.Role, Confidence = 1 };
            }).Where(x => x.HeadingSpan.Start >= 0).ToArray();
            var materialized = ReasoningProposalMaterializer.Materialize(context.Source, context.Policy, proposals);
            var finalElements = ReasoningTaskProjection.ProjectContentHeadings(materialized.Structure).OrderBy(x => x.Sources.Single().SourceOrdinal).ThenBy(x => x.Sources.Single().Span.Start).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
            var finalPath = Path.Combine(dir, "prediction.v1.json");
            var resultPath = Path.Combine(dir, "result.v1.json");
            await WriteJson(finalPath, new { schemaVersion = "a99-a99-i6-final-prediction-v1", experimentId = I6Experiment, context.DocumentId, repeat = repeatName, model = ControlModel, sourceSha256 = context.SourceSha256, packetHash = context.PacketHash, passAHeadingCount = bound.Count, passBRoleCount = roles.Count, passAHeadings = bound.Select((x, i) => new { headingId = $"H{i + 1:0000}", sourceId = x.SourceId, start = x.Start, end = x.End, text = x.Text }).ToArray(), passBRoles = roles.Select((role, i) => new { headingId = $"H{i + 1:0000}", role }).ToArray(), finalHeadings = finalElements.Select(x => new { sourceId = x.Sources.Single().SourceId, start = x.Sources.Single().Span.Start, end = x.Sources.Single().Span.End, text = x.Text, role = x.Role }).ToArray(), validatorAccepted = materialized.Validated.Count(x => x.Accepted), validatorRejected = materialized.Validated.Count(x => !x.Accepted), passBIdentityMutation = false, goldReadBeforeFreeze = false }, ct);
            await WriteJson(resultPath, new { schemaVersion = "a99-a99-i6-final-result-v1", experimentId = I6Experiment, context.DocumentId, repeat = repeatName, headings = finalElements.Select(x => new { sourceId = x.Sources.Single().SourceId, start = x.Sources.Single().Span.Start, end = x.Sources.Single().Span.End, text = x.Text, role = x.Role }).ToArray(), goldReadBeforeFreeze = false }, ct);
            var finalFreezePath = Path.Combine(dir, "freeze.v1.json");
            await WriteJson(finalFreezePath, new { schemaVersion = "a99-a99-i6-final-freeze-v1", experimentId = I6Experiment, context.DocumentId, repeat = repeatName, gitSha, model = ControlModel, sourceSha256 = context.SourceSha256, packetHash = context.PacketHash, predictionSha256 = Sha256(finalPath), resultSha256 = Sha256(resultPath), passAFreeze = Sha256(Path.Combine(passADir, "freeze.v1.json")), passBFreeze = Sha256(Path.Combine(passBDir, "freeze.v1.json")), passAHeadingCount = bound.Count, finalCount = finalElements.Length, validatorRejected = materialized.Validated.Count(x => !x.Accepted), passBIdentityMutation = false, goldReadBeforeFreeze = false, frozenUtc = DateTimeOffset.UtcNow }, ct);
            if (Sha256(finalPath) != JsonDocument.Parse(File.ReadAllText(finalFreezePath)).RootElement.GetProperty("predictionSha256").GetString()) throw new InvalidDataException("I6_FINAL_FREEZE_VERIFY_FAILED");
            var goldPath = Path.Combine(repoRoot, "eval/a99-closed-loop/strict-gold-occurrence-v1", context.DocumentId + ".occurrence-gold-v1.json");
            var gold = ReasoningGoldArtifactLoader.LoadOccurrence(goldPath).Where(x => x.HeadingSpan is not null).ToArray();
            var passAKeys = bound.Select(x => Key(x.SourceId, new StructuralSpan(x.Start, x.End))).ToHashSet(StringComparer.Ordinal);
            var finalKeys = finalElements.Select(x => Key(x.Sources.Single().SourceId, x.Sources.Single().Span)).ToHashSet(StringComparer.Ordinal);
            var passAScore = I6Score(passAKeys, bound.Select(x => (x.SourceId, x.Start, x.End, x.Text)), gold, context.DocumentId, "PASS_A");
            var finalScore = I6Score(finalKeys, finalElements.Select(x => (x.Sources.Single().SourceId, x.Sources.Single().Span.Start, x.Sources.Single().Span.End, x.Text)), gold, context.DocumentId, "FINAL");
            await WriteJson(Path.Combine(dir, "score.v1.json"), new { schemaVersion = "a99-a99-i6-score-v1", context.DocumentId, repeat = repeatName, status = "SUCCESS", passA = passAScore, final = finalScore, goldReadBeforeFreeze = false }, ct);
            await WriteJson(Path.Combine(dir, "first-loss.v1.json"), new { schemaVersion = "a99-a99-i6-first-loss-v1", context.DocumentId, repeat = repeatName, passA = passAScore.FirstLosses, final = finalScore.FirstLosses, bindAmbiguous = observations.Count(x => x.Status == SemanticTextBindingStatus.AMBIGUOUS_EXACT_TEXT), bindFailure = observations.Count(x => x.Status is not SemanticTextBindingStatus.BOUND), systemLoss = materialized.Validated.Count(x => !x.Accepted) + Math.Max(0, bound.Count - finalElements.Length), goldReadBeforeFreeze = false }, ct);
            stopwatch.Stop();
            return new I6CellMetric(context.DocumentId, repeatName, "SUCCESS", passAScore, finalScore, passAKeys, finalKeys, gold.Select(x => Key(x.SourceId, x.HeadingSpan!)).ToHashSet(StringComparer.Ordinal), observations.Count(x => x.Status == SemanticTextBindingStatus.AMBIGUOUS_EXACT_TEXT), observations.Count(x => x.Status is not SemanticTextBindingStatus.BOUND), materialized.Validated.Count(x => !x.Accepted) + Math.Max(0, bound.Count - finalElements.Length), passACall || File.Exists(Path.Combine(passADir, "freeze.v1.json")), passBCompleted, false, roles, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            await WriteJson(Path.Combine(dir, "score.v1.json"), new { schemaVersion = "a99-a99-i6-score-v1", context.DocumentId, repeat = repeatName, status = "BLOCKED", failure = ex.GetType().Name + ":" + ex.Message, goldReadBeforeFreeze = false }, ct);
            await WriteJson(Path.Combine(dir, "blocked.v1.json"), new { schemaVersion = "a99-a99-i6-blocked-v1", context.DocumentId, repeat = repeatName, failure = ex.GetType().Name + ":" + ex.Message, passAFrozen = File.Exists(Path.Combine(passADir, "freeze.v1.json")), passBFrozen = File.Exists(Path.Combine(passBDir, "freeze.v1.json")), goldReadBeforeFreeze = false }, ct);
            return I6CellMetric.Blocked(context.DocumentId, repeatName, stopwatch.ElapsedMilliseconds, passACall, passBCompleted, passATelemetry, passBTelemetry);
        }
    }

    private static I6IdentityScore I6Score(HashSet<string> predictionKeys, IEnumerable<(string SourceId, int Start, int End, string Text)> predictions, IReadOnlyList<ReasoningGoldOccurrence> gold, string documentId, string stage)
    {
        var goldRows = gold.Select(x => (Key: Key(x.SourceId, x.HeadingSpan!), x.SourceId, Start: x.HeadingSpan!.Start, End: x.HeadingSpan.End, x.ExactText)).ToArray();
        var tp = goldRows.Count(x => predictionKeys.Contains(x.Key));
        var fp = predictionKeys.Except(goldRows.Select(x => x.Key), StringComparer.Ordinal).Count();
        var fn = goldRows.Count(x => !predictionKeys.Contains(x.Key));
        var p = tp + fp == 0 ? 0d : (double)tp / (tp + fp); var r = tp + fn == 0 ? 0d : (double)tp / (tp + fn); var f1 = p + r == 0 ? 0d : 2 * p * r / (p + r);
        var rows = predictions.ToArray();
        var losses = goldRows.Where(x => !predictionKeys.Contains(x.Key)).Select(x =>
        {
            var sameText = rows.Any(y => y.SourceId == x.SourceId && y.Text == x.ExactText);
            return new { key = x.Key, exactSourceText = x.ExactText, firstLoss = sameText ? "MODEL_WRONG_SPAN" : "MODEL_OMISSION", found = false };
        }).ToArray();
        var extras = rows.Where(x => !goldRows.Any(g => g.Key == Key(x.SourceId, new StructuralSpan(x.Start, x.End))))
            .Select(x => new { key = Key(x.SourceId, new StructuralSpan(x.Start, x.End)), exactSourceText = x.Text, firstLoss = "MODEL_FALSE_POSITIVE", found = true }).ToArray();
        var counts = losses.Concat(extras).GroupBy(x => x.firstLoss, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        return new I6IdentityScore(stage, goldRows.Length, tp, fp, fn, p, r, f1, counts, losses, extras);
    }

    private static JsonElement ParseJsonObject(string raw)
    {
        var start = raw.IndexOf('{'); var end = raw.LastIndexOf('}');
        if (start < 0 || end < start) throw new FormatException("i6-response-json-incomplete");
        using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
        return doc.RootElement.Clone();
    }

    private static IReadOnlyList<SemanticTextHeading> ParseI6Discovery(string raw)
    {
        var root = ParseJsonObject(raw);
        if (!root.TryGetProperty("headings", out var array) || array.ValueKind != JsonValueKind.Array) throw new FormatException("i6-discovery-headings-missing");
        return ParseI6DiscoveryFromPersisted(array);
    }

    private static IReadOnlyList<SemanticTextHeading> ParseI6DiscoveryFromPersisted(JsonElement array)
    {
        var result = new List<SemanticTextHeading>();
        foreach (var item in array.EnumerateArray())
        {
            if (!item.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(source.GetString()) ||
                !item.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(text.GetString()))
                throw new FormatException("i6-discovery-heading-schema-invalid");
            int? occurrence = item.TryGetProperty("occurrence", out var occ) && occ.ValueKind != JsonValueKind.Null ? occ.GetInt32() : null;
            var left = item.TryGetProperty("leftExactContext", out var leftValue) && leftValue.ValueKind == JsonValueKind.String ? leftValue.GetString() : null;
            var right = item.TryGetProperty("rightExactContext", out var rightValue) && rightValue.ValueKind == JsonValueKind.String ? rightValue.GetString() : null;
            result.Add(new SemanticTextHeading(source.GetString()!, text.GetString()!, I6RoleSentinel, occurrence, left, right));
        }
        return result;
    }

    private static List<string> ParseI6Roles(JsonElement array, IReadOnlyList<string> expectedIds)
    {
        if (array.ValueKind != JsonValueKind.Array) throw new FormatException("i6-role-array-missing");
        var rows = array.EnumerateArray().Select(x =>
        {
            if (!x.TryGetProperty("headingId", out var id) || !x.TryGetProperty("role", out var role) || id.ValueKind != JsonValueKind.String || role.ValueKind != JsonValueKind.String || !CeilingSemanticRole.IsAllowed(role.GetString())) throw new FormatException("i6-role-schema-invalid");
            return (Id: id.GetString()!, Role: role.GetString()!);
        }).ToArray();
        if (rows.Length != expectedIds.Count || rows.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != expectedIds.Count || rows.Any(x => !expectedIds.Contains(x.Id, StringComparer.Ordinal))) throw new FormatException("i6-role-count-or-id-mismatch");
        return expectedIds.Select(id => rows.Single(x => x.Id == id).Role).ToList();
    }

    private static object BuildI6Micro(IEnumerable<I6CellMetric> cells, Func<I6CellMetric, I6IdentityScore> selector)
    {
        var rows = cells.Select(selector).ToArray(); var tp = rows.Sum(x => x.Tp); var fp = rows.Sum(x => x.Fp); var fn = rows.Sum(x => x.Fn); var p = tp + fp == 0 ? 0d : (double)tp / (tp + fp); var r = tp + fn == 0 ? 0d : (double)tp / (tp + fn); var f1 = p + r == 0 ? 0d : 2 * p * r / (p + r); return new { cells = rows.Length, gold = rows.Sum(x => x.Gold), tp, fp, fn, precision = p, recall = r, f1, systemLoss = cells.Sum(x => x.SystemLoss) };
    }

    private static object BuildRepeatMicro(IEnumerable<RepeatMetric> metrics)
    {
        var rows = metrics.ToArray(); var tp = rows.Sum(x => x.Tp); var fp = rows.Sum(x => x.Fp); var fn = rows.Sum(x => x.Fn); var p = tp + fp == 0 ? 0d : (double)tp / (tp + fp); var r = tp + fn == 0 ? 0d : (double)tp / (tp + fn); var f1 = p + r == 0 ? 0d : 2 * tp / (double)(2 * tp + fp + fn); return new { cells = rows.Length, gold = rows.Sum(x => x.Gold), tp, fp, fn, precision = p, recall = r, f1, systemLoss = rows.Sum(x => x.SystemLoss) };
    }

    private static object RoleStability(IReadOnlyList<I6CellMetric> cells) => cells.GroupBy(x => x.DocumentId, StringComparer.Ordinal).Select(g => new { documentId = g.Key, headings = g.SelectMany(x => x.Roles.Select((role, i) => (Id: $"H{i + 1:0000}", Role: role))).GroupBy(x => x.Id, StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new { headingId = x.Key, roles = x.Select(y => y.Role).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), stable = x.Select(y => y.Role).Distinct(StringComparer.Ordinal).Count() == 1 }).ToArray() }).ToArray();

    private static IReadOnlyList<I6BaselineMetric> LoadI6Baseline(string repoRoot, IReadOnlyList<DocumentContext> contexts)
    {
        return contexts.SelectMany(context => Enumerable.Range(1, RepeatCount).Select(repeat =>
        {
            var metric = LoadMetricAtDir(Path.Combine(repoRoot, DefaultOutputRoot.Replace('/', Path.DirectorySeparatorChar), context.DocumentId, $"r{repeat}"));
            var persistentFp = metric.FalsePositives.Count;
            var ambiguous = metric.FirstLossCounts.GetValueOrDefault("AMBIGUOUS_EXACT_TEXT");
            return new I6BaselineMetric(context.DocumentId, $"r{repeat}", metric.F1, metric.SystemLoss, ambiguous, persistentFp, metric);
        })).OrderBy(x => x.DocumentId, StringComparer.Ordinal).ThenBy(x => x.Repeat, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<(string DocumentId, string Key)> I6StableTargets() => new[]
    {
        ("DOC-0252", "body[1]/tbl[2]/tr[1]/tc[2]/p[6]:182:194"),
        ("DOC-0258", "body[1]/p[11]:0:6"), ("DOC-0258", "body[1]/p[12]:0:20"),
        ("DOC-0258", "body[1]/p[17]:0:25"), ("DOC-0258", "body[1]/p[18]:0:34"),
        ("DOC-0258", "body[1]/p[19]:0:31"), ("DOC-0258", "body[1]/p[24]:0:12"),
    };

    private sealed record I6IdentityScore(string Stage, int Gold, int Tp, int Fp, int Fn, double Precision, double Recall, double F1, IReadOnlyDictionary<string, int> FirstLossCounts, object FirstLosses, object FalsePositives)
    {
        public object ToReport() => new { stage = Stage, gold = Gold, tp = Tp, fp = Fp, fn = Fn, precision = Precision, recall = Recall, f1 = F1, firstLossCounts = FirstLossCounts };
    }
    private sealed record I6CellMetric(string DocumentId, string Repeat, string Status, I6IdentityScore PassA, I6IdentityScore Final, HashSet<string> PassAKeys, HashSet<string> FinalKeys, HashSet<string> GoldKeys, int BindAmbiguous, int BindFailure, int SystemLoss, bool PassACallCompleted, bool RolePassCompleted, bool PassBIdentityMutation, IReadOnlyList<string> Roles, long WallTimeMs)
    {
        public bool PassACompleted => PassA.Gold > 0;
        public bool PassBCompleted => RolePassCompleted;
        public int PersistentFp => 0;
        public static I6CellMetric Blocked(string documentId, string repeat, long wallTimeMs, bool passA, bool passB, RequestPacketTelemetry? a, RequestPacketTelemetry? b) => new(documentId, repeat, "BLOCKED", new I6IdentityScore("PASS_A", 0, 0, 0, 0, 0, 0, 0, new Dictionary<string, int>(), Array.Empty<object>(), Array.Empty<object>()), new I6IdentityScore("FINAL", 0, 0, 0, 0, 0, 0, 0, new Dictionary<string, int>(), Array.Empty<object>(), Array.Empty<object>()), [], [], [], 0, 0, 0, passA, passB, false, [], wallTimeMs);
    }
    private sealed record I6BaselineMetric(string DocumentId, string Repeat, double F1, int SystemLoss, int BindAmbiguous, int PersistentFp, RepeatMetric Metric);
}
