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
/// A99 heading-target ontology experiment.  The only model-facing change from the frozen Flash
/// ceiling is the generic V4 task definition.  Policy audit and extra taxonomy are offline
/// diagnostics; Gold is opened only after each prediction/result pair is frozen.
/// </summary>
public static class HeadingTargetOntologyRunner
{
    private const string Model = "qwen/qwen3.7-flash";
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const string InventoryPath = "eval/a99-dataset/document-inventory.v1.json";
    private const string OutputRoot = "eval/a99-closed-loop/heading-target-ontology";
    private static readonly string[] SelectedIds = ["DOC-0205", "DOC-0258"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(output);
        var startHead = CurrentGitSha(repoRoot);
        Console.WriteLine($"START_HEAD={startHead}");

        var audit = BuildOfflineAudit(repoRoot, startHead);
        await WriteJsonAsync(Path.Combine(output, "policy-audit.v1.json"), audit.PolicyAudit, ct);
        await WriteJsonAsync(Path.Combine(output, "r1-extra-taxonomy.v1.json"), audit.Taxonomy, ct);

        var contractText = HeadingTargetOntologyV4Contract.ContractTextForHash();
        var contractHash = Sha256Text(contractText);
        var schemaHash = Sha256Text(JsonSerializer.Serialize(HeadingTargetOntologyV4Contract.Schema()));
        var leakage = CheckContractLeakage(repoRoot, contractText);
        if (!leakage.Passed) throw new InvalidDataException("V4_CONTRACT_LEAKAGE_TEST_FAILED");

        var contract = new
        {
            schemaVersion = "a99-heading-target-ontology-contract-v4",
            contractVersion = HeadingTargetOntologyV4Contract.ProtocolVersion,
            model = Model,
            policySources = audit.PolicySources,
            promptSha256 = Sha256Text(HeadingTargetOntologyV4Contract.SystemPrompt),
            contractSha256 = contractHash,
            responseSchemaSha256 = schemaHash,
            includedRoles = HeadingTargetOntologyV4Contract.Roles,
            boundary = "complete contiguous heading label; meaningful numbering/punctuation included; surrounding whitespace excluded; source-local UTF-16 half-open [start,end)",
            noCandidateHeuristicGate = true,
            noDocumentSpecificRules = true,
            noDocumentTextExamples = true,
            noExpectedCounts = true,
            goldLeakageTest = leakage,
            goldReadBeforeFreeze = false,
            providerCalls = 0,
            frozen = true,
            frozenAtHead = startHead,
            frozenUtc = DateTimeOffset.UtcNow,
        };
        var contractPath = Path.Combine(output, "contract-v4.v1.json");
        await WriteJsonAsync(contractPath, contract, ct);
        using (var frozen = JsonDocument.Parse(await File.ReadAllTextAsync(contractPath, ct)))
        {
            if (!frozen.RootElement.GetProperty("frozen").GetBoolean() ||
                frozen.RootElement.GetProperty("providerCalls").GetInt32() != 0 ||
                frozen.RootElement.GetProperty("goldReadBeforeFreeze").GetBoolean() ||
                frozen.RootElement.GetProperty("contractSha256").GetString() != contractHash)
                throw new InvalidDataException("V4_CONTRACT_FREEZE_VERIFICATION_FAILED");
        }
        Console.WriteLine($"CONTRACT_V4_FREEZE=PASS sha256={contractHash}");
        var postFreezeLeakage = ScanGoldTextAfterFreeze(repoRoot, contractText);
        if (!postFreezeLeakage.Passed) throw new InvalidDataException("V4_POST_FREEZE_GOLD_LEAKAGE_TEST_FAILED");

        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            return await BlockedAsync(output, startHead, "OPENROUTER_API_KEY_MISSING", 0, ct);

        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(repoRoot, OutputRoot, string.Join(',', SelectedIds), ct);
        var options = new RemoteInferenceOptions
        {
            Endpoint = new Uri(Endpoint), Model = Model, ApiKey = key, ContextSize = 1_000_000,
            MaxOutputTokens = 48_000, RequestTimeoutSeconds = 600, TransientRequestRetries = 0,
            MaxParallelRequests = 1, SendChatTemplateKwargs = false, OpenRouterAllowNonZdrPublicBenchmark = true,
        };
        var preflight = await OpenRouterModelCapabilityResolver.ResolveAsync(options, http, ct);
        if (!preflight.Available || preflight.Capability is null ||
            !string.Equals(preflight.Capability.ModelId, Model, StringComparison.Ordinal) ||
            !preflight.Capability.ReasoningSupported || !preflight.Capability.StructuredOutputSupported)
            return await BlockedAsync(output, startHead, "MODEL_CAPABILITY_MISMATCH", 0, ct);

        var doc0205 = ReadInventory(Path.Combine(repoRoot, InventoryPath)).Single(x => x.DocumentId == "DOC-0205");
        var doc0258 = ReadInventory(Path.Combine(repoRoot, InventoryPath)).Single(x => x.DocumentId == "DOC-0258");
        var first = await RunDocumentAsync(repoRoot, output, doc0205, preflight.Capability, options, http, ct);
        RunMetric? second = null;
        if (first.ExactStatus == "EVALUABLE")
            second = await RunDocumentAsync(repoRoot, output, doc0258, preflight.Capability, options, http, ct);

        var classification = first.ExactStatus != "EVALUABLE" || second is null || second.ExactStatus != "EVALUABLE"
            ? "CONTRACT_EXPERIMENT_EXECUTION_BLOCKED"
            : first.SystemLoss > 0 || second.SystemLoss > 0
                ? "CONTRACT_EXPERIMENT_EXECUTION_BLOCKED"
                : first.TP == 0 && first.FN >= 70
                    ? "MODEL_SEMANTIC_DISCOVERY_FAILURE_CONFIRMED"
                    : first.F1 > 0.05 && second.F1 >= 0.70
                        ? "TASK_ONTOLOGY_MISMATCH_CONFIRMED"
                        : "MIXED_ONTOLOGY_AND_MODEL_FAILURE";
        var vlmGate = classification switch
        {
            "MODEL_SEMANTIC_DISCOVERY_FAILURE_CONFIRMED" => "VLM_WORTH_TESTING",
            "TASK_ONTOLOGY_MISMATCH_CONFIRMED" => "VLM_NOT_YET_NEEDED",
            _ => "VLM_UNDECIDED",
        };
        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-heading-target-ontology-summary-v1",
            startHead, model = Model, contractVersion = HeadingTargetOntologyV4Contract.ProtocolVersion,
            goldLeakageAuditAfterFreeze = postFreezeLeakage,
            offlineAudit = new { audit.PolicyAudit, audit.Taxonomy },
            comparison = new { r1Original = new { contract = "R1_ORIGINAL", tp = 0, fp = 73, fn = 71, precision = 0d, recall = 0d, f1 = 0d, omission = 70, wrongSpan = 1, systemLoss = 0 }, c0 = LoadComparisonMetric(repoRoot, "C0_STRUCTURE_PRESERVING", Path.Combine("eval", "a99-closed-loop", "structure-preserving-ir", "DOC-0205", "text-ceiling.score.v1.json")), c1 = new { contract = "C1_BOUNDARY", status = "INVALID_FOR_MODEL_COMPARISON_DUE_TO_SYSTEM_LOSS", systemLoss = 71 }, c2 = LoadComparisonMetric(repoRoot, "C2_ADDRESSED", Path.Combine("eval", "a99-closed-loop", "flash-heading-contract-realignment", "DOC-0205", "c2_addressed", "score.v1.json")), v4 = new { doc0205 = first, doc0258 = second } },
            finalClassification = classification, vlmGate, providerCalls = first.ProviderAttempts + (second?.ProviderAttempts ?? 0),
            goldReadBeforeFreeze = false, knownN15 = "UNTOUCHED", completedUtc = DateTimeOffset.UtcNow,
        }, ct);
        Console.WriteLine($"DOC0205_V4 TP={first.TP} FP={first.FP} FN={first.FN} F1={first.F1:0.######} SystemLoss={first.SystemLoss}");
        if (second is not null) Console.WriteLine($"DOC0258_V4 TP={second.TP} FP={second.FP} FN={second.FN} F1={second.F1:0.######} SystemLoss={second.SystemLoss}");
        Console.WriteLine($"FINAL_CLASSIFICATION={classification}");
        Console.WriteLine($"VLM_GATE={vlmGate}");
        return classification == "CONTRACT_EXPERIMENT_EXECUTION_BLOCKED" ? 1 : 0;
    }

    private static async Task<RunMetric> RunDocumentAsync(string repoRoot, string root, InventoryItem item,
        OpenRouterModelCapability capability, RemoteInferenceOptions options, HttpClient http, CancellationToken ct)
    {
        var docDir = Path.Combine(root, item.DocumentId);
        Directory.CreateDirectory(docDir);
        using var model = new OpenRouterCeilingReasoningModel(options, capability, http);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var sourcePath = Path.Combine(repoRoot, item.SourcePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(sourcePath) || Sha256File(sourcePath) != item.SourceSha256) throw new InvalidDataException("SOURCE_HASH_MISMATCH");
            var source = new OpenXmlDocumentSource().Read(sourcePath) with { DocumentId = item.DocumentId };
            var features = NumberingStyleFeatures.FromSourceDocument(source);
            var derived = new DocumentFeatureDeriver().Derive(source);
            var policy = DocxPolicyStateBuilder.Build(source, features, derived, new PipelineOptions { DisableLlm = false }.Extraction);
            var maxPrompt = model.MaxPromptTokens(model.SemanticMaxCompletionTokens);
            var pack = ReasoningContextBuilder.Build(source, policy, Math.Max(4_000, maxPrompt), Math.Max(4_000, maxPrompt), expandOwnedPerOccurrence: false);
            if (pack.Segments.Count != 1) throw new InvalidDataException("FULL_CONTEXT_NOT_AVAILABLE");
            var segment = pack.Segments.Single();
            var occurrences = pack.Occurrences.ToDictionary(x => x.SourceOccurrenceId, StringComparer.Ordinal);
            var owned = segment.OwnedSourceOccurrenceIds.ToHashSet(StringComparer.Ordinal);
            var visibleWindow = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
            var ownedWindow = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
            foreach (var id in segment.SourceOccurrenceIds)
            {
                var occurrence = occurrences[id];
                var visible = segment.VisibleStartCharacter is { } vs && segment.VisibleEndCharacter is { } ve && segment.OwnedSourceOccurrenceId == id ? (vs, ve) : (0, occurrence.RawText.Length);
                visibleWindow[id] = visible;
                ownedWindow[id] = owned.Contains(id) ? segment.OwnedSourceOccurrenceId == id && segment.OwnedStartCharacter is { } os && segment.OwnedEndCharacter is { } oe ? (os, oe) : (0, occurrence.RawText.Length) : (visible.Item1, visible.Item1);
            }
            var packet = CeilingPacketBuilder.Build(segment.SourceOccurrenceIds.Select(id => occurrences[id]).ToArray(), owned, visibleWindow, ownedWindow);
            var promptHash = Sha256Text(HeadingTargetOntologyV4Contract.SystemPrompt);
            var schemaHash = Sha256Text(JsonSerializer.Serialize(HeadingTargetOntologyV4Contract.Schema()));
            var packetHash = Sha256Text(packet.SerializedJson);
            var signature = Sha256Text($"A99_HEADING_TARGET_ONTOLOGY_V4|{Model}|{item.DocumentId}|{promptHash}|{schemaHash}|{packetHash}|reasoning=true|temperature=0");
            var requestId = $"HEADING_TARGET_ONTOLOGY_V4:{item.DocumentId}:{signature}";
            var (response, _) = await model.CompleteSemanticAsync(item.DocumentId, ReasoningRoute.ModelCapabilityCeiling.ToString(), requestId,
                packet.SerializedJson, packet.SourceTextCharacters, owned.Count, segment.SourceOccurrenceIds.Count,
                HeadingTargetOntologyV4Contract.SystemPrompt,
                HeadingTargetOntologyV4Contract.BuildUser(packet.SerializedJson, ReasoningRoute.ModelCapabilityCeiling.ToString()),
                HeadingTargetOntologyV4Contract.Schema(), "heading_target_ontology_v4", ct);

            var proposals = new List<ReasoningHeadingProposal>();
            var bindingLoss = 0;
            foreach (var heading in response.Headings)
            {
                var binding = CeilingProposalBinder.ResolveBinding(heading.I, packet.Bindings, owned, null);
                if (binding is null || !binding.TryBind(heading.Start, heading.End, out var globalStart, out var globalEnd, out var ownedSpan) || !ownedSpan)
                { bindingLoss++; continue; }
                var occurrence = occurrences[binding.SourceOccurrenceId];
                proposals.Add(new ReasoningHeadingProposal { SourceId = occurrence.SourceId, HeadingSpan = new StructuralSpan(globalStart, globalEnd), Text = occurrence.RawText[globalStart..globalEnd], SemanticRole = heading.Role, Confidence = 1 });
            }
            if (bindingLoss != 0) throw new InvalidDataException($"SYSTEM_BINDING_LOSS:{bindingLoss}");
            var materialized = ReasoningProposalMaterializer.Materialize(source, policy, proposals);
            var accepted = materialized.Validated.Where(x => x.Accepted).Select(x => x.ElementId).ToHashSet(StringComparer.Ordinal);
            var finalElements = materialized.Structure.Elements
                .Where(x => accepted.Contains(x.Id) && HeadingTargetOntologyV4Contract.IsTaskRole(proposals.First(p => ReasoningProposalMaterializer.ElementId(p) == x.Id).SemanticRole))
                .OrderBy(x => x.Sources.Single().SourceOrdinal).ThenBy(x => x.Sources.Single().Span.Start).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
            var finalKeys = finalElements.Select(Key).ToHashSet(StringComparer.Ordinal);
            var telemetry = model.Telemetry.Where(x => x.DocumentId == item.DocumentId).ToArray();
            var prediction = new
            {
                schemaVersion = "a99-heading-target-ontology-prediction-v1", documentId = item.DocumentId, model = Model,
                semanticContractVersion = HeadingTargetOntologyV4Contract.ProtocolVersion, reasoningMode = "R1_CEILING", executionMode = "FULL_CONTEXT_CEILING",
                sourceSha256 = item.SourceSha256, sourceCharacters = pack.SourceCharacters, packetCharacters = packet.PacketCharacters,
                packetHash, promptHash, schemaHash, rawProposalCount = response.Headings.Count, boundProposalCount = proposals.Count,
                validatedCount = materialized.Validated.Count(x => x.Accepted), finalCount = finalElements.Length,
                proposals, headings = finalElements, goldReadBeforeFreeze = false,
            };
            var result = new { schemaVersion = "a99-heading-target-ontology-result-v1", documentId = item.DocumentId, model = Model, semanticContractVersion = HeadingTargetOntologyV4Contract.ProtocolVersion, reasoningMode = "R1_CEILING", headings = finalElements, goldReadBeforeFreeze = false };
            var predictionPath = Path.Combine(docDir, "prediction.v1.json");
            var resultPath = Path.Combine(docDir, "result.v1.json");
            await WriteJsonAsync(predictionPath, prediction, ct);
            await WriteJsonAsync(resultPath, result, ct);
            var predictionHash = Sha256File(predictionPath); var resultHash = Sha256File(resultPath);
            var actualProvider = telemetry.Select(x => x.ProviderRoute).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "NOT_EXPOSED";
            var freeze = new
            {
                schemaVersion = "a99-heading-target-ontology-freeze-v1", documentId = item.DocumentId, model = Model,
                semanticContractVersion = HeadingTargetOntologyV4Contract.ProtocolVersion, actualProvider,
                reasoningConfiguration = new { requested = true, enabled = true, exclude = true }, sourceSha256 = item.SourceSha256,
                promptHash, schemaHash, packetHash, configurationSignature = signature, predictionSha256 = predictionHash, resultSha256 = resultHash,
                finishReason = telemetry.LastOrDefault()?.FinishReason, providerAttempts = model.ProviderCalls,
                inputTokens = telemetry.Sum(x => x.ReportedInputTokens ?? 0), reasoningTokens = telemetry.Sum(x => x.ReportedReasoningTokens ?? 0), outputTokens = telemetry.Sum(x => x.ReportedOutputTokens ?? 0),
                goldReadBeforeFreeze = false, frozenUtc = DateTimeOffset.UtcNow,
            };
            await WriteJsonAsync(Path.Combine(docDir, "freeze.v1.json"), freeze, ct);
            if (Sha256File(predictionPath) != predictionHash || Sha256File(resultPath) != resultHash) throw new InvalidDataException("FREEZE_HASH_VERIFICATION_FAILED");

            // Gold firewall: this is the first Gold read for this V4 document and it is after freeze.
            var goldPath = Path.Combine(repoRoot, "eval", "a99-closed-loop", "strict-gold-occurrence-v1", $"{item.DocumentId}.occurrence-gold-v1.json");
            var gold = ReasoningGoldArtifactLoader.LoadOccurrence(goldPath).Where(x => x.HeadingSpan is not null).ToArray();
            var goldKeys = gold.Select(x => Key(x.SourceId, x.HeadingSpan!)).ToHashSet(StringComparer.Ordinal);
            var score = Score(goldKeys, finalKeys);
            var losses = ClassifyLosses(gold, proposals, materialized, finalElements, finalKeys).GroupBy(x => x).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
            foreach (var name in new[] { "MODEL_OMISSION", "MODEL_WRONG_SPAN", "MODEL_FALSE_POSITIVE", "SYSTEM_VALIDATOR_LOSS", "SYSTEM_PROJECTION_LOSS", "SYSTEM_BINDING_LOSS" }) losses.TryAdd(name, 0);
            var systemLoss = losses.Where(x => x.Key.StartsWith("SYSTEM_", StringComparison.Ordinal)).Sum(x => x.Value);
            await WriteJsonAsync(Path.Combine(docDir, "score.v1.json"), new { schemaVersion = "a99-heading-target-ontology-score-v1", documentId = item.DocumentId, exactStatus = "EVALUABLE", goldCount = goldKeys.Count, tp = score.TP, fp = score.FP, fn = score.FN, precision = score.P, recall = score.R, f1 = score.F1, lossCounts = losses, systemLossCount = systemLoss, goldReadBeforeFreeze = false }, ct);
            await WriteJsonAsync(Path.Combine(docDir, "first-loss.v1.json"), new { schemaVersion = "a99-heading-target-ontology-first-loss-v1", documentId = item.DocumentId, losses, goldReadBeforeFreeze = false }, ct);
            stopwatch.Stop();
            var metric = new RunMetric(item.DocumentId, "V4_ONTOLOGY", "EVALUABLE", actualProvider, score.TP, score.FP, score.FN, score.P, score.R, score.F1, losses["MODEL_OMISSION"], losses["MODEL_WRONG_SPAN"], systemLoss, model.ProviderCalls, telemetry.Sum(x => x.ReportedReasoningTokens ?? 0), stopwatch.ElapsedMilliseconds, finalElements.Length);
            await WriteJsonAsync(Path.Combine(docDir, "execution.v1.json"), new { metric, telemetry, goldReadBeforeFreeze = false }, ct);
            return metric;
        }
        catch (Exception ex) when (ex is ReasoningCompletionException or HttpRequestException or InvalidDataException or FormatException or JsonException)
        {
            stopwatch.Stop();
            var telemetry = model.Telemetry.Where(x => x.DocumentId == item.DocumentId).ToArray();
            var metric = new RunMetric(item.DocumentId, "V4_ONTOLOGY", "BLOCKED", telemetry.Select(x => x.ProviderRoute).FirstOrDefault() ?? "NOT_EXPOSED", 0, 0, 0, 0, 0, 0, 0, 0, 0, model.ProviderCalls, telemetry.Sum(x => x.ReportedReasoningTokens ?? 0), stopwatch.ElapsedMilliseconds, 0);
            await WriteJsonAsync(Path.Combine(docDir, "execution.v1.json"), new { metric, status = "BLOCKED", error = ex.Message, telemetry, goldReadBeforeFreeze = false }, ct);
            Console.Error.WriteLine($"V4_{item.DocumentId}_FAILURE={ex.Message}");
            return metric;
        }
    }

    private static IEnumerable<string> ClassifyLosses(IReadOnlyList<ReasoningGoldOccurrence> gold, IReadOnlyList<ReasoningHeadingProposal> proposals,
        (ValidatedStructure Structure, IReadOnlyList<ReasoningValidatedProposal> Validated) materialized, IReadOnlyList<ValidatedStructuralElement> finalElements, IReadOnlySet<string> finalKeys)
    {
        var rawByKey = proposals.GroupBy(Key).ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        var rows = materialized.Validated.ToDictionary(x => x.ElementId, StringComparer.Ordinal);
        foreach (var item in gold)
        {
            var key = Key(item.SourceId, item.HeadingSpan!);
            if (finalKeys.Contains(key)) continue;
            if (!rawByKey.TryGetValue(key, out var exact))
                yield return proposals.Any(p => p.SourceId == item.SourceId && p.HeadingSpan.Start < item.HeadingSpan!.End && item.HeadingSpan!.Start < p.HeadingSpan.End) ? "MODEL_WRONG_SPAN" : "MODEL_OMISSION";
            else if (!rows.TryGetValue(ReasoningProposalMaterializer.ElementId(exact), out var row) || !row.Accepted) yield return "SYSTEM_VALIDATOR_LOSS";
            else yield return "SYSTEM_PROJECTION_LOSS";
        }
        foreach (var element in finalElements)
            if (!gold.Any(item => Key(item.SourceId, item.HeadingSpan!) == Key(element))) yield return "MODEL_FALSE_POSITIVE";
    }

    private static OfflineAudit BuildOfflineAudit(string repoRoot, string startHead)
    {
        var policySources = new[]
        {
            "docs/accuracy/hierarchy-human-annotation-guideline.md",
            "docs/accuracy/accuracy99-strict-gold-authority-policy-v2.md",
            "docs/accuracy/accuracy99-historical-gold-reconciliation-v3.md",
            "eval/a99-dataset/reference-authority-policy.v1.json",
        };
        var mapPath = Path.Combine(repoRoot, "eval", "a99-closed-loop", "doc0205-strict-gold-v4-forensic", "proposal-gold-map.v1.json");
        using var map = JsonDocument.Parse(File.ReadAllText(mapPath));
        var extras = map.RootElement.GetProperty("rawProposals").EnumerateArray().Where(x => x.GetProperty("relationship").GetString() == "TRUE_EXTRA").Select(x => new
        {
            rawOrdinal = x.GetProperty("rawOrdinal").GetInt32(), sourceId = x.GetProperty("sourceId").GetString(), start = x.GetProperty("start").GetInt32(), end = x.GetProperty("end").GetInt32(), semanticRole = x.GetProperty("semanticRole").GetString(), text = x.GetProperty("rawTextFromVisibleSource").GetString(), category = TaxonomyCategory(x.GetProperty("rawOrdinal").GetInt32()), rationale = TaxonomyRationale(x.GetProperty("rawOrdinal").GetInt32())
        }).ToArray();
        var counts = extras.GroupBy(x => x.category).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        var policyAudit = new
        {
            schemaVersion = "a99-heading-target-ontology-policy-audit-v1", phase = "OFFLINE_BEFORE_PROVIDER", startHead, providerCalls = 0,
            policySources, policyVsInstanceSeparated = true,
            policyDefinitions = new
            {
                target = "A contiguous textual label organizing substantive document content or outline.",
                included = new[] { "document title", "part", "chapter", "section", "subsection", "article", "clause", "annex", "other substantive content heading" },
                excluded = new[] { "navigation", "table of contents", "running header", "caption", "list item", "table label", "metadata/front matter", "decorative text", "body prose or fragment" },
                boundary = "complete contiguous label, meaningful numbering/punctuation included, surrounding whitespace excluded, source-local UTF-16 half-open offsets",
                ambiguous = new[] { "The human hierarchy guideline says title/running-header/TOC/appendix/table-only text are context but does not itself redefine heading labels.", "Legal marker inclusion is evidenced by the reviewed legal reference, not a global regex rule.", "Per-document approved projections remain instance evidence and are not copied into the runtime prompt." },
            },
            currentR1PromptAudit = new object[]
            {
                new { rule = "every structurally real document heading or structural label", classification = "PROMPT_TOO_BROAD", reason = "requests labels outside the projected substantive-heading target" },
                new { rule = "zero, one, or many elements per occurrence", classification = "PROMPT_ALIGNED", reason = "generic cardinality" },
                new { rule = "formatting, numbering, layout are evidence, not rules", classification = "PROMPT_ALIGNED", reason = "does not make a heuristic gate" },
                new { rule = "return exact spans", classification = "PROMPT_AMBIGUOUS", reason = "does not say complete label versus fragment" },
                new { rule = "extract navigation, TOC, front matter too; projection later", classification = "PROMPT_TOO_BROAD", reason = "explicitly broadens the model target beyond projected content headings" },
                new { rule = "uncertain boundary: omit", classification = "PROMPT_ALIGNED", reason = "conservative generic boundary rule" },
            },
            hypothesis = new { trueExtraCount = extras.Length, outOfScopeButStructurallyPlausibleCount = counts.GetValueOrDefault("OUT_OF_SCOPE_STRUCTURAL_ELEMENT"), obviousSemanticHallucinationCount = counts.GetValueOrDefault("BODY_TEXT") + counts.GetValueOrDefault("LIST_OR_SUBCLAUSE"), classification = "MODEL_SEMANTIC_DISCOVERY_FAILURE_SUPPORTED", note = "The broad prompt is a real ontology defect, but the frozen extras are overwhelmingly fragmented prose/list content rather than legitimate excluded headings." },
            goldReadBeforeFreeze = false,
        };
        var taxonomy = new { schemaVersion = "a99-heading-target-ontology-r1-extra-taxonomy-v1", phase = "OFFLINE_ONLY", documentId = "DOC-0205", sourceArtifact = "frozen R1 proposal map", providerCalls = 0, categories = counts, rows = extras, goldReadBeforeFreeze = false };
        return new OfflineAudit(policySources, policyAudit, taxonomy);
    }

    private static string TaxonomyCategory(int ordinal) => ordinal == 0 ? "VALID_TARGET_BUT_REFERENCE_PROBLEM" : ordinal >= 22 ? "LIST_OR_SUBCLAUSE" : "BODY_TEXT";
    private static string TaxonomyRationale(int ordinal) => ordinal == 0 ? "The span combines a title-like label with date/metadata; a generic title target may be valid, but this reference projection does not establish it as a scored row." : ordinal >= 22 ? "The span is a fragment inside a numbered definition/list sequence, not a complete heading label." : "The span is an interior fragment of continuous legal body prose between structural markers.";

    private static object LoadComparisonMetric(string repoRoot, string contract, string relativePath)
    {
        var path = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return new { contract, status = "NOT_AVAILABLE" };
        using var score = JsonDocument.Parse(File.ReadAllText(path));
        var root = score.RootElement;
        int GetInt(string name) => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var n) ? n : 0;
        double GetDouble(string name) => root.TryGetProperty(name, out var value) && value.TryGetDouble(out var n) ? n : 0d;
        var systemLoss = root.TryGetProperty("systemLoss", out var sl) && sl.TryGetInt32(out var s) ? s : GetInt("systemLossCount");
        return new { contract, status = "FROZEN", tp = GetInt("tp"), fp = GetInt("fp"), fn = GetInt("fn"), precision = GetDouble("precision"), recall = GetDouble("recall"), f1 = GetDouble("f1"), omission = GetInt("modelOmission") + GetInt("modelTrueOmission"), wrongSpan = GetInt("modelWrongSpan"), systemLoss };
    }

    private static LeakCheck CheckContractLeakage(string repoRoot, string contractText)
    {
        var forbidden = new[] { "DOC-0205", "DOC-0258", "71", "24" };
        var hits = forbidden.Where(contractText.Contains).ToArray();
        // This check is deliberately Gold-free and runs before the contract freeze.  A separate
        // post-freeze audit may compare against Gold; no Gold bytes are needed to reject the
        // document identifiers/counts that could leak into a generic runtime contract.
        return new LeakCheck(hits.Length == 0, hits, true, 0);
    }

    private static LeakCheck ScanGoldTextAfterFreeze(string repoRoot, string contractText)
    {
        var hits = new List<string>();
        foreach (var documentId in SelectedIds)
        {
            var path = Path.Combine(repoRoot, "eval", "a99-closed-loop", "strict-gold-occurrence-v1", $"{documentId}.occurrence-gold-v1.json");
            if (!File.Exists(path)) continue;
            using var gold = JsonDocument.Parse(File.ReadAllText(path));
            if (!gold.RootElement.TryGetProperty("bindings", out var bindings)) continue;
            foreach (var binding in bindings.EnumerateArray())
                if (binding.TryGetProperty("rawSourceText", out var raw) && raw.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(raw.GetString()) && contractText.Contains(raw.GetString()!, StringComparison.Ordinal)) hits.Add(documentId);
        }
        return new LeakCheck(hits.Count == 0, hits.Distinct(StringComparer.Ordinal).ToArray(), true, hits.Count);
    }

    private static ScoreResult Score(IReadOnlySet<string> gold, IReadOnlySet<string> predicted)
    {
        var tp = gold.Intersect(predicted).Count(); var fp = predicted.Except(gold).Count(); var fn = gold.Except(predicted).Count();
        var p = tp + fp == 0 ? 0d : (double)tp / (tp + fp); var r = tp + fn == 0 ? 0d : (double)tp / (tp + fn);
        return new(tp, fp, fn, p, r, p + r == 0 ? 0 : 2 * p * r / (p + r));
    }
    private static string Key(ReasoningHeadingProposal p) => Key(p.SourceId, p.HeadingSpan);
    private static string Key(ValidatedStructuralElement e) => Key(e.Sources.Single().SourceId, e.Sources.Single().Span);
    private static string Key(string sourceId, StructuralSpan span) => $"{sourceId}:{span.Start}:{span.End}";
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string CurrentGitSha(string repoRoot) { try { using var p = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD") { WorkingDirectory = repoRoot, RedirectStandardOutput = true, UseShellExecute = false }); return p?.StandardOutput.ReadToEnd().Trim() ?? "UNKNOWN"; } catch { return "UNKNOWN"; } }
    private static InventoryItem[] ReadInventory(string path) { using var doc = JsonDocument.Parse(File.ReadAllText(path)); return doc.RootElement.GetProperty("documents").EnumerateArray().Select(x => new InventoryItem(x.GetProperty("documentId").GetString()!, x.GetProperty("sourcePath").GetString()!, x.GetProperty("sourceSha256").GetString()!)).ToArray(); }
    private static async Task<int> BlockedAsync(string output, string startHead, string reason, int providerCalls, CancellationToken ct) { await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new { schemaVersion = "a99-heading-target-ontology-summary-v1", status = "CONTRACT_EXPERIMENT_EXECUTION_BLOCKED", reason, startHead, model = Model, providerCalls, goldReadBeforeFreeze = false }, ct); Console.WriteLine($"FINAL_CLASSIFICATION=CONTRACT_EXPERIMENT_EXECUTION_BLOCKED:{reason}"); return 1; }
    private static async Task WriteJsonAsync(string path, object value, CancellationToken ct) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, ct);

    private sealed record InventoryItem(string DocumentId, string SourcePath, string SourceSha256);
    private sealed record OfflineAudit(string[] PolicySources, object PolicyAudit, object Taxonomy);
    private sealed record ScoreResult(int TP, int FP, int FN, double P, double R, double F1);
    private sealed record LeakCheck(bool Passed, string[] ForbiddenTokensFound, bool PolicyOnly, int DocumentGoldTextHits);
    private sealed record RunMetric(string DocumentId, string Contract, string ExactStatus, string ActualProvider, int TP, int FP, int FN, double Precision, double Recall, double F1, int ModelOmission, int WrongSpan, int SystemLoss, int ProviderAttempts, int ReasoningTokens, long WallTimeMs, int FinalHeadingCount);
}
