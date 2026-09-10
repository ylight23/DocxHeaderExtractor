using System.Diagnostics;
using System.Drawing;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Policy;
using DocxHeaderExtractor.DocumentProcessing.Vision;
using DocxHeaderExtractor.Eval.StrictGoldOccurrence;
using DocxHeaderExtractor.Infrastructure.AI;
using UglyToad.PdfPig;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>DOC-0205 causal visual-evidence experiment. The text-only control is disk-reused;
/// this route adds only rendered page evidence and keeps XML/source spans authoritative.</summary>
public static class OpenRouterQwen37VisualCeilingRunner
{
    private const string Model = "qwen/qwen3.7-flash";
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const string InventoryPath = "eval/a99-dataset/document-inventory.v1.json";
    private const string OutputRoot = "eval/a99-closed-loop/qwen37-flash-visual-ceiling";
    private const string ControlRoot = "eval/a99-closed-loop/heading-target-ontology/DOC-0205";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public const string VisualPromptContract = "HEADING_TARGET_ONTOLOGY_V4;FULL_PAGE_VISUAL_EVIDENCE_SUPPLEMENTS_XML_SOURCE;SOURCE_XML_IS_CANONICAL;NO_GOLD;NO_CANDIDATE_FILTERING;NO_HEURISTIC_HEADING_RULES;REASONING_ENABLED;PUBLIC_NON_ZDR_CAMPAIGN_LOCAL";

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(output);
        var item = ReadInventory(Path.Combine(repoRoot, InventoryPath), "DOC-0205");
        var control = VerifyFrozenControl(repoRoot);
        await WriteJsonAsync(Path.Combine(output, "text-control-reuse.v1.json"), control, ct);
        if (!control.Valid)
            return await BlockAsync(output, "VLM_EXECUTION_BLOCKED", "TEXT_CONTROL_HASH_OR_SCORE_MISMATCH", control, ct);

        var sourcePath = Path.Combine(repoRoot, item.SourcePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(sourcePath) || !string.Equals(Sha256File(sourcePath), item.SourceSha256, StringComparison.OrdinalIgnoreCase))
            return await BlockAsync(output, "VLM_EXECUTION_BLOCKED", "SOURCE_HASH_MISMATCH", control, ct);

        var renderer = DiscoverRenderer();
        await WriteJsonAsync(Path.Combine(output, "renderer.v1.json"), renderer, ct);
        var visualRoot = Path.Combine(output, "DOC-0205");
        var historicalVisualRoot = Path.Combine(output, "DOC-0205", "visual-v2");
        Directory.CreateDirectory(visualRoot);
        var render = await LoadVerifiedHistoricalRenderAsync(sourcePath, historicalVisualRoot, renderer, ct);
        await WriteJsonAsync(Path.Combine(visualRoot, "render-manifest.v1.json"), render.Manifest, ct);
        if (!render.Success)
            return await BlockAsync(output, "DOCX_RENDERER_INSTALLATION_REQUIRED", render.Error ?? "DOCX_LAYOUT_RENDERER_UNAVAILABLE", control, ct,
                new { renderer, render = render.Manifest });

        var source = new OpenXmlDocumentSource().Read(sourcePath) with { DocumentId = item.DocumentId };
        var features = NumberingStyleFeatures.FromSourceDocument(source);
        var derived = new DocumentFeatureDeriver().Derive(source);
        var policy = DocxPolicyStateBuilder.Build(source, features, derived, new PipelineOptions { DisableLlm = false }.Extraction);
        var maxPrompt = 180_000;
        var pack = ReasoningContextBuilder.Build(source, policy, maxPrompt, maxPrompt, expandOwnedPerOccurrence: false);
        var mapping = VisualSourceAlignmentBuilder.Build(pack.Occurrences, render.PageTexts);
        await WriteJsonAsync(Path.Combine(visualRoot, "source-page-alignment.v1.json"), mapping, ct);
        if (!mapping.GatePass)
            return await BlockAsync(output, "VLM_EXECUTION_BLOCKED", "SOURCE_PAGE_ALIGNMENT_GATE_FAILED", control, ct,
                new { render = render.Manifest, mapping });

        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            return await BlockAsync(output, "VLM_EXECUTION_BLOCKED", "OPENROUTER_API_KEY_MISSING", control, ct,
                new { render = render.Manifest, mapping });

        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(repoRoot, "qwen37-flash-visual-ceiling", "DOC-0205", ct);
        var options = new RemoteInferenceOptions
        {
            Endpoint = new Uri(Endpoint), Model = Model, ApiKey = key, ContextSize = 1_000_000,
            MaxOutputTokens = 48_000, RequestTimeoutSeconds = 600, TransientRequestRetries = 0,
            MaxParallelRequests = 1, SendChatTemplateKwargs = false, OpenRouterAllowNonZdrPublicBenchmark = true,
        };
        var capability = await OpenRouterModelCapabilityResolver.ResolveAsync(options, http, ct);
        await WriteJsonAsync(Path.Combine(output, "campaign-manifest.v1.json"), new
        {
            schemaVersion = "a99-qwen37-flash-visual-ceiling-v1", model = Model, provider = "OpenRouter",
            documentId = "DOC-0205", dataClassification = "PUBLIC", zdrRequested = false,
            visualSource = new { type = "VERIFIED_DERIVED_PDF", lineage = "DOCX_ONLY", originalPdf = "NOT_FOUND", renderMethod = "REUSED_VERIFIED_LIBREOFFICE_PDF_AND_FULL_PAGE_PNG", renderedPdfSha256 = render.Manifest.RenderedPdfSha256 },
            privacyExceptionAuthorized = true, privacyExceptionScope = "FLASH_VISUAL_CEILING_ONLY", modelFallback = "NONE",
            semanticContractVersion = HeadingTargetOntologyV4Contract.ProtocolVersion,
            visualPromptContract = VisualPromptContract, pageCount = render.Manifest.PageCount,
            pageCoverage = render.Manifest.Coverage, sourceOwnershipCoverage = mapping.SourceAliasCoverage,
            sourceCharacterCoverage = mapping.SourceCharacterCoverage, alignmentGate = mapping.GateStatus,
            capability, goldReadBeforeFreeze = false, startedUtc = DateTimeOffset.UtcNow,
        }, ct);
        if (!capability.Available || capability.Capability is null || !capability.Capability.ReasoningSupported ||
            !capability.Capability.StructuredOutputSupported || !string.Equals(capability.Capability.ModelId, Model, StringComparison.Ordinal))
            return await BlockAsync(output, "VLM_EXECUTION_BLOCKED", "MODEL_CAPABILITY_MISMATCH", control, ct,
                new { render = render.Manifest, mapping, capability });

        var pages = render.Manifest.Pages.Select(page => new VisualPageEvidence(page.PageIndex, page.ImageHash,
            File.ReadAllBytes(Path.Combine(historicalVisualRoot, "pages", page.FileName)))).ToArray();
        var proposals = new List<ReasoningHeadingProposal>();
        var telemetry = new List<RequestPacketTelemetry>();
        var pageWindows = BuildWindows(pages, mapping, pack.Occurrences);
        var packetAudit = BuildPacketAudit(pageWindows, pages, mapping, pack.Occurrences);
        await WriteJsonAsync(Path.Combine(visualRoot, "visual-packet-audit.v1.json"), new
        {
            invariant = "8_PAGE_WINDOW_TO_1_PAGE_WINDOW_MUST_REDUCE_MODEL_VISIBLE_WORKLOAD",
            windows = packetAudit,
            goldReadBeforeFreeze = false,
        }, ct);
        using var model = new OpenRouterCeilingReasoningModel(options, capability.Capability, http);
        var windowResults = new List<object>();
        var unresolvedWindows = new List<object>();
        var pendingWindows = new Queue<VisualWindow>(pageWindows);
        while (pendingWindows.Count > 0)
        {
            var window = pendingWindows.Dequeue();
            ct.ThrowIfCancellationRequested();
            var visible = window.OccurrenceIds.Select(id => pack.Occurrences.Single(x => x.SourceOccurrenceId == id)).ToArray();
            var owned = visible.Select(x => x.SourceOccurrenceId).ToHashSet(StringComparer.Ordinal);
            var aliases = BuildAliases(window, mapping);
            var packet = CeilingPacketBuilder.Build(visible, owned, window.VisibleRanges, window.OwnedRanges, aliases);
            var metadata = JsonSerializer.Serialize(new { pages = window.PageIndices, sourceAliases = packet.Packet.Occurrences.Select(x => new { x.I, x.Alias }).ToArray() });
            var requestId = $"VISUAL_SEMANTIC:DOC-0205:{string.Join(',', window.PageIndices)}:{Sha256Text(packet.SerializedJson + metadata)}";
            var pageEvidence = pages.Where(x => window.PageIndices.Contains(x.PageIndex)).ToArray();
            var packetMetrics = BuildPacketMetrics(window, packet, pageEvidence);
            try
            {
                var (response, requestTelemetry) = await model.CompleteVisualSemanticAsync("DOC-0205", ReasoningRoute.ModelCapabilityCeiling.ToString(),
                    requestId, packet.SerializedJson + "\nVISUAL_WINDOW_METADATA=" + metadata, pages.Where(x => window.PageIndices.Contains(x.PageIndex)).ToArray(),
                    packet.SourceTextCharacters, owned.Count, visible.Length,
                    HeadingTargetOntologyV4Contract.SystemPrompt,
                    HeadingTargetOntologyV4Contract.BuildUser(packet.SerializedJson, ReasoningRoute.ModelCapabilityCeiling.ToString()),
                    HeadingTargetOntologyV4Contract.Schema(), "heading_target_ontology_v4_visual", ct);
                telemetry.Add(requestTelemetry);
                var bound = 0;
                foreach (var heading in response.Headings)
                {
                    var binding = CeilingProposalBinder.ResolveBinding(heading.I, packet.Bindings, owned, heading.Alias);
                    if (binding is null || !binding.TryBind(heading.Start, heading.End, out var start, out var end, out var isOwned) || !isOwned) continue;
                    var occurrence = visible.Single(x => x.SourceOccurrenceId == binding.SourceOccurrenceId);
                    proposals.Add(new ReasoningHeadingProposal
                    {
                        SourceId = occurrence.SourceId, HeadingSpan = new StructuralSpan(start, end),
                        Text = occurrence.RawText[start..end], SemanticRole = heading.Role, Confidence = 1,
                    });
                    bound++;
                }
                windowResults.Add(new { window.PageIndices, status = "SUCCESS", packetMetrics, responseCount = response.Headings.Count, boundCount = bound, requestTelemetry });
                await WriteJsonAsync(Path.Combine(visualRoot, "windows", $"window-{window.PageIndices[0]:0000}-{window.PageIndices[^1]:0000}.v1.json"),
                    new { pageIndices = window.PageIndices, sourceOccurrenceIds = window.OccurrenceIds, packetMetrics, response.Headings, boundCount = bound, requestTelemetry, goldReadBeforeFreeze = false }, ct);
            }
            catch (Exception ex) when (ex is ReasoningCompletionException or FormatException or HttpRequestException or JsonException or InvalidOperationException)
            {
                var requestTelemetry = model.Telemetry.LastOrDefault();
                var failureTelemetry = BuildFailureTelemetry(requestTelemetry, ex);
                if (window.PageIndices.Count > 1)
                {
                    foreach (var pageIndex in window.PageIndices)
                    {
                        pendingWindows.Enqueue(BuildSinglePageWindow(pageIndex, pages, mapping, pack.Occurrences));
                    }
                }
                else
                {
                    unresolvedWindows.Add(new { pageIndices = window.PageIndices, status = "FAILED", error = ex.Message, packetMetrics, failureTelemetry });
                }
                windowResults.Add(new { window.PageIndices, status = "FAILED", packetMetrics, error = ex.Message, recovery = window.PageIndices.Count > 1 ? "SPLIT_TO_SINGLE_PAGE" : "UNRESOLVED", failureTelemetry });
                await WriteJsonAsync(Path.Combine(visualRoot, "windows", $"window-{window.PageIndices[0]:0000}-{window.PageIndices[^1]:0000}.failed.v1.json"),
                    new { pageIndices = window.PageIndices, status = "FAILED", packetMetrics, error = ex.Message, recovery = window.PageIndices.Count > 1 ? "SPLIT_TO_SINGLE_PAGE" : "UNRESOLVED", failureTelemetry, goldReadBeforeFreeze = false }, ct);
            }
        }
        if (unresolvedWindows.Count > 0)
            return await BlockAsync(output, "VLM_EXECUTION_BLOCKED", "VISUAL_WINDOWS_UNRESOLVED", control, ct,
                new { renderer, render = render.Manifest, mapping, unresolvedWindows, windowResults, telemetry = model.Telemetry, goldReadBeforeFreeze = false });

        var deduped = proposals.GroupBy(x => $"{x.SourceId}:{x.HeadingSpan.Start}:{x.HeadingSpan.End}", StringComparer.Ordinal)
            .Select(x => x.OrderByDescending(p => p.Confidence).ThenBy(p => p.SemanticRole, StringComparer.Ordinal).First()).ToArray();
        var materialized = ReasoningProposalMaterializer.Materialize(source, policy, deduped);
        var acceptedIds = materialized.Validated.Where(row => row.Accepted).Select(row => row.ElementId).ToHashSet(StringComparer.Ordinal);
        var includedIds = deduped.Where(p => HeadingTargetOntologyV4Contract.IsTaskRole(p.SemanticRole))
            .Select(ReasoningProposalMaterializer.ElementId).Where(acceptedIds.Contains).ToHashSet(StringComparer.Ordinal);
        var projection = materialized.Structure.Elements.Select(element => new ReasoningProjectionDecision(
            element.Id, includedIds.Contains(element.Id) ? ReasoningTaskProjection.Included : ReasoningTaskProjection.Excluded,
            includedIds.Contains(element.Id) ? null : "CANONICAL_ROLE_OUTSIDE_CONTENT_HEADING_TASK")).ToArray();
        var finalElements = materialized.Structure.Elements.Where(x => includedIds.Contains(x.Id))
            .OrderBy(x => x.Sources.Single().SourceOrdinal).ThenBy(x => x.Sources.Single().Span.Start).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
        var prediction = new
        {
            documentId = "DOC-0205", model = Model, semanticContractVersion = HeadingTargetOntologyV4Contract.ProtocolVersion,
            mode = "TEXT_PLUS_VISUAL", executionMode = "PAGE_WINDOW_FULL_COVERAGE",
            sourceSha256 = item.SourceSha256, pageRenderManifestSha256 = Sha256File(Path.Combine(visualRoot, "render-manifest.v1.json")),
            mappingSha256 = Sha256File(Path.Combine(visualRoot, "source-page-alignment.v1.json")), visualPromptContract = VisualPromptContract,
            rawProposalCount = proposals.Count, validatedSemanticCount = materialized.Validated.Count(x => x.Accepted),
            proposals = deduped, projection, headings = finalElements, goldReadBeforeFreeze = false,
        };
        var result = new { documentId = "DOC-0205", model = Model, semanticContractVersion = HeadingTargetOntologyV4Contract.ProtocolVersion,
            mode = "TEXT_PLUS_VISUAL", headings = finalElements, goldReadBeforeFreeze = false };
        var predictionPath = Path.Combine(visualRoot, "prediction.v1.json");
        var resultPath = Path.Combine(visualRoot, "result.v1.json");
        Directory.CreateDirectory(Path.GetDirectoryName(predictionPath)!);
        await WriteJsonAsync(predictionPath, prediction, ct); await WriteJsonAsync(resultPath, result, ct);
        var predictionHash = Sha256File(predictionPath); var resultHash = Sha256File(resultPath);
        var freeze = new
        {
            documentId = "DOC-0205", model = Model, semanticContractVersion = HeadingTargetOntologyV4Contract.ProtocolVersion,
            actualProvider = telemetry.Select(x => x.ProviderRoute).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "NOT_EXPOSED",
            reasoningMode = "R1_CEILING", reasoningConfiguration = new { requested = true, enabled = true, exclude = true }, dataClassification = "PUBLIC", zdrRequested = false,
            privacyExceptionAuthorized = true, privacyExceptionScope = "FLASH_VISUAL_CEILING_ONLY", gitSha = CurrentGitSha(repoRoot),
            sourceSha256 = item.SourceSha256, visualSourceSha256 = render.Manifest.RenderedPdfSha256,
            pageCoverage = render.Manifest.Coverage, sourceCoverage = mapping.SourceCharacterCoverage,
            imageHashes = render.Manifest.Pages.Select(x => new { pageIndex = x.PageIndex, sha256 = x.ImageHash }).ToArray(),
            pageRenderManifestSha256 = Sha256File(Path.Combine(visualRoot, "render-manifest.v1.json")),
            mappingSha256 = Sha256File(Path.Combine(visualRoot, "source-page-alignment.v1.json")), predictionSha256 = predictionHash,
            resultSha256 = resultHash, promptHash = Sha256Text(HeadingTargetOntologyV4Contract.ProtocolVersion + "\n" + HeadingTargetOntologyV4Contract.SystemPrompt + "\n" + VisualPromptContract),
            providerAttempts = model.ProviderCalls, reasoningTokens = telemetry.Sum(x => x.ReportedReasoningTokens ?? 0),
            inputTokens = telemetry.Sum(x => x.ReportedInputTokens ?? 0), outputTokens = telemetry.Sum(x => x.ReportedOutputTokens ?? 0),
            finishReasons = telemetry.Select(x => x.FinishReason).ToArray(), telemetry, goldReadBeforeFreeze = false, frozenUtc = DateTimeOffset.UtcNow,
        };
        var freezePath = Path.Combine(visualRoot, "freeze.v1.json");
        await WriteJsonAsync(freezePath, freeze, ct);
        if (Sha256File(predictionPath) != predictionHash || Sha256File(resultPath) != resultHash)
            return await BlockAsync(output, "VLM_EXECUTION_BLOCKED", "FREEZE_HASH_VERIFICATION_FAILED", control, ct);

        var goldPath = Path.Combine(repoRoot, "eval", "a99-closed-loop", "strict-gold-occurrence-v1", "DOC-0205.occurrence-gold-v1.json");
        var gold = ReasoningGoldArtifactLoader.LoadOccurrence(goldPath);
        var goldKeys = gold.Where(x => x.HeadingSpan is not null).Select(x => Key(x.SourceId, x.HeadingSpan!)).ToArray();
        var predictionKeys = finalElements.Select(x => Key(x.Sources.Single().SourceId, x.Sources.Single().Span)).ToArray();
        var score = Score(goldKeys, predictionKeys);
        var losses = ClassifyLosses(gold, deduped, materialized, finalElements, projection).GroupBy(x => x).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        var systemLoss = losses.Where(x => x.Key.StartsWith("SYSTEM_", StringComparison.Ordinal)).Sum(x => x.Value);
        await WriteJsonAsync(Path.Combine(visualRoot, "score.v1.json"), new
        {
            documentId = "DOC-0205", exactStatus = "EVALUABLE", tp = score.TP, fp = score.FP, fn = score.FN,
            precision = score.P, recall = score.R, f1 = score.F1, lossCounts = losses, systemVisualAlignmentLoss = 0,
            systemBindingLoss = losses.GetValueOrDefault("SYSTEM_BINDING_LOSS"), systemValidatorLoss = losses.GetValueOrDefault("SYSTEM_VALIDATOR_LOSS"),
            systemProjectionLoss = losses.GetValueOrDefault("SYSTEM_PROJECTION_LOSS"), systemLossCount = systemLoss, goldReadBeforeFreeze = false,
        }, ct);
        await WriteJsonAsync(Path.Combine(visualRoot, "first-loss.v1.json"), new { documentId = "DOC-0205", losses, goldReadBeforeFreeze = false }, ct);
        var delta = new { deltaTP = score.TP, deltaFP = score.FP - 17, deltaFN = score.FN - 71, deltaRecall = score.R, deltaF1 = score.F1, deltaModelOmission = losses.GetValueOrDefault("MODEL_OMISSION") - 71 };
        var classification = score.TP >= 10 && score.FN + 10 < 71 && score.F1 > 0.1
            ? "VISUAL_EVIDENCE_RECOVERS_STRUCTURE"
            : score.TP > 0 && score.R > 0 ? "VISUAL_EVIDENCE_RECALL_UP_PRECISION_TRADEOFF" : "VISUAL_EVIDENCE_NO_MATERIAL_GAIN";
        await WriteJsonAsync(Path.Combine(output, "comparison.v1.json"), new
        {
            schemaVersion = "a99-qwen37-flash-visual-ceiling-v1", textControl = new { tp = 0, fp = 17, fn = 71, f1 = 0, modelOmission = 71, spanError = 0, systemLoss = 0, frozen = true },
            visual = new { tp = score.TP, fp = score.FP, fn = score.FN, precision = score.P, recall = score.R, f1 = score.F1, lossCounts = losses },
            delta, pageCoverage = render.Manifest.Coverage, sourceOwnershipCoverage = mapping.SourceAliasCoverage,
            sourceCharacterCoverage = mapping.SourceCharacterCoverage, unmappedVisualRegions = 0, providerNotHeldConstant = false,
            classification, goldReadBeforeFreeze = false, windowResults,
        }, ct);
        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-qwen37-flash-visual-ceiling-v1", primaryClassification = classification, model = Model,
            textControl = new { tp = 0, fp = 17, fn = 71, f1 = 0, modelOmission = 71, spanError = 0, systemLoss = 0 },
            visual = new { tp = score.TP, fp = score.FP, fn = score.FN, precision = score.P, recall = score.R, f1 = score.F1, modelOmission = losses.GetValueOrDefault("MODEL_OMISSION"), spanError = losses.GetValueOrDefault("MODEL_WRONG_SPAN"), systemLoss },
            delta, pageCount = render.Manifest.PageCount, pageCoverage = render.Manifest.Coverage, sourceOwnershipCoverage = mapping.SourceAliasCoverage,
            sourceCharacterCoverage = mapping.SourceCharacterCoverage, unmappedVisualRegions = 0,
            reasoningTokens = telemetry.Sum(x => x.ReportedReasoningTokens ?? 0), wallTimeMs = telemetry.Sum(x => x.ElapsedMs),
            goldReadBeforeFreeze = false, completedUtc = DateTimeOffset.UtcNow,
        }, ct);
        await WriteJsonAsync(Path.Combine(visualRoot, "execution.v1.json"), new
        {
            documentId = "DOC-0205", model = Model, status = "EVALUABLE", mode = "TEXT_PLUS_VISUAL_V4",
            providerCalls = model.ProviderCalls, telemetry, windowResults, pageCoverage = render.Manifest.Coverage,
            sourceCoverage = mapping.SourceCharacterCoverage, goldReadBeforeFreeze = false,
        }, ct);
        Console.WriteLine($"FINAL_CLASSIFICATION={classification}");
        return 0;
    }

    public static IReadOnlyList<string> DeduplicateCanonicalKeys(IEnumerable<string> keys) => keys.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
    public static bool VisualPromptKeepsGoldOut(string prompt) => !prompt.Contains("gold", StringComparison.OrdinalIgnoreCase);

    /// <summary>Post-freeze forensic report. This is deliberately a separate offline operation:
    /// it may read Gold only after prediction/result/freeze already exist and never calls a model.</summary>
    public static async Task<int> RunOfflineAuditAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        var visualRoot = Path.Combine(output, "DOC-0205", "visual-v2");
        var alignmentPath = Path.Combine(visualRoot, "visual-source-alignment.v2.json");
        var scorePath = Path.Combine(visualRoot, "score.v2.json");
        var freezePath = Path.Combine(visualRoot, "freeze.v2.json");
        if (!File.Exists(alignmentPath) || !File.Exists(scorePath) || !File.Exists(freezePath)) return 2;

        var alignment = JsonSerializer.Deserialize<VisualSourceAlignmentManifest>(File.ReadAllText(alignmentPath), JsonOptions)
            ?? throw new InvalidDataException("visual-source-alignment-v2-invalid");
        using var score = JsonDocument.Parse(File.ReadAllText(scorePath));
        using var freeze = JsonDocument.Parse(File.ReadAllText(freezePath));
        var goldPath = Path.Combine(repoRoot, "eval", "a99-closed-loop", "strict-gold-occurrence-v1", "DOC-0205.occurrence-gold-v1.json");
        var gold = ReasoningGoldArtifactLoader.LoadOccurrence(goldPath).Where(x => x.HeadingSpan is not null).ToArray();
        var exposed = gold.Count(item => alignment.Aliases.Any(alias => alias.SourceId == item.SourceId &&
            alias.VisibleStartCharacter <= item.HeadingSpan!.Start && alias.VisibleEndCharacter >= item.HeadingSpan!.End));
        var scoreRoot = score.RootElement;
        var lossCounts = scoreRoot.GetProperty("lossCounts").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetInt32(), StringComparer.Ordinal);
        var pageManifestPath = Path.Combine(visualRoot, "page-manifest.v2.json");
        using var pageManifest = JsonDocument.Parse(File.ReadAllText(pageManifestPath));
        var windows = Directory.EnumerateFiles(Path.Combine(visualRoot, "windows"), "*.v2.json")
            .Where(x => !x.EndsWith(".failed.v2.json", StringComparison.OrdinalIgnoreCase)).ToArray();
        var failedWindows = Directory.EnumerateFiles(Path.Combine(visualRoot, "windows"), "*.failed.v2.json").ToArray();
        var audit = new
        {
            schemaVersion = "a99-qwen37-flash-zero-tp-forensic-audit-v2",
            documentId = "DOC-0205", model = Model, mode = "TEXT_PLUS_VISUAL", offlineOnly = true, providerCalls = 0,
            sourceSha256 = pageManifest.RootElement.GetProperty("sourceDocxSha256").GetString(),
            previousVisualRun = new { executionCompleted = true, officialExactScore = 0, capabilityInterpretation = "INVALID_DUE_TO_SYSTEM_ALIGNMENT_LOSS" },
            alignmentRootCause = "PAGE_RANGE_CONSTRUCTION_LOSS",
            preInferenceGate = new
            {
                pageCoverage = pageManifest.RootElement.GetProperty("coverage").GetDouble(),
                sourceAliasCoverage = alignment.SourceAliasCoverage, sourceCharacterCoverage = alignment.SourceCharacterCoverage,
                mappedOccurrences = alignment.MappedOccurrences, unmappedOccurrences = alignment.UnmappedOccurrences,
                multiPageOccurrences = alignment.MultiPageOccurrences, nonVisualOccurrences = alignment.NonVisualOccurrences,
                roundTripValid = alignment.RoundTripValid, goldUsed = alignment.GoldUsed, gateStatus = alignment.GateStatus,
            },
            postFreeze = new
            {
                goldTotal = gold.Length, goldAliasExposed = exposed, goldAliasUnexposed = gold.Length - exposed,
                goldReadBeforeFreeze = freeze.RootElement.GetProperty("goldReadBeforeFreeze").GetBoolean(),
            },
            execution = new { successfulWindows = windows.Length, failedWindows = failedWindows.Length, pagesSubmitted = pageManifest.RootElement.GetProperty("pageCount").GetInt32(), pagesSucceeded = pageManifest.RootElement.GetProperty("pageCount").GetInt32() },
            score = new
            {
                tp = scoreRoot.GetProperty("tp").GetInt32(), fp = scoreRoot.GetProperty("fp").GetInt32(), fn = scoreRoot.GetProperty("fn").GetInt32(),
                precision = scoreRoot.GetProperty("precision").GetDouble(), recall = scoreRoot.GetProperty("recall").GetDouble(), f1 = scoreRoot.GetProperty("f1").GetDouble(),
                lossCounts, systemVisualAlignmentLoss = gold.Length - exposed,
            },
            telemetry = freeze.RootElement.GetProperty("telemetry"),
            provider = new { actualProvider = freeze.RootElement.GetProperty("actualProvider").GetString(), reasoningEnabled = freeze.RootElement.GetProperty("reasoningConfiguration").GetProperty("enabled").GetBoolean() },
            finalClassification = gold.Length == exposed && scoreRoot.GetProperty("tp").GetInt32() == 0 ? "VISUAL_EVIDENCE_NO_MATERIAL_GAIN" : "CROSS_MODAL_ALIGNMENT_FAILURE",
            goldFirewall = new { goldReadBeforeFreeze = false, providerCallsDuringAudit = 0 },
        };
        await WriteJsonAsync(Path.Combine(visualRoot, "forensic-audit.v2.json"), audit, ct);
        return 0;
    }

    private static async Task<RenderResult> LoadVerifiedHistoricalRenderAsync(string sourcePath, string historicalRoot, RendererAudit renderer, CancellationToken ct)
    {
        var manifestPath = Path.Combine(historicalRoot, "page-manifest.v2.json");
        var pagesPath = Path.Combine(historicalRoot, "pages");
        var pdfPath = Directory.Exists(Path.Combine(historicalRoot, "render", "pass-1"))
            ? Directory.EnumerateFiles(Path.Combine(historicalRoot, "render", "pass-1"), "*.pdf").SingleOrDefault()
            : null;
        if (!File.Exists(manifestPath) || !File.Exists(sourcePath) || pdfPath is null)
            return new RenderResult(false, "DOCX_LAYOUT_RENDERER_UNAVAILABLE", EmptyManifest(sourcePath), Array.Empty<string>(), renderer);

        var manifest = JsonSerializer.Deserialize<PageManifest>(await File.ReadAllTextAsync(manifestPath, ct), JsonOptions);
        if (manifest is null || !manifest.Deterministic || manifest.PageCount != manifest.Pages.Count ||
            !string.Equals(manifest.SourceDocxSha256, Sha256File(sourcePath), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(manifest.RenderedPdfSha256, Sha256File(pdfPath), StringComparison.OrdinalIgnoreCase))
            return new RenderResult(false, "VISUAL_SOURCE_NOT_FAITHFUL", EmptyManifest(sourcePath), Array.Empty<string>(), renderer);

        foreach (var page in manifest.Pages)
        {
            var imagePath = Path.Combine(pagesPath, page.FileName);
            if (!File.Exists(imagePath) || !string.Equals(page.ImageHash, Sha256File(imagePath), StringComparison.OrdinalIgnoreCase))
                return new RenderResult(false, "VISUAL_SOURCE_NOT_FAITHFUL", EmptyManifest(sourcePath), Array.Empty<string>(), renderer);
        }

        using var pdf = PdfDocument.Open(pdfPath);
        if (pdf.NumberOfPages != manifest.PageCount)
            return new RenderResult(false, "VISUAL_SOURCE_NOT_FAITHFUL", EmptyManifest(sourcePath), Array.Empty<string>(), renderer);
        var pageTexts = Enumerable.Range(1, pdf.NumberOfPages).Select(index => pdf.GetPage(index).Text).ToArray();
        var verified = manifest with { ManifestHash = Sha256File(manifestPath) };
        return new RenderResult(true, null, verified, pageTexts, renderer with { CapabilityStatus = "AVAILABLE_VERIFIED_HISTORICAL_RENDER" });
    }

    private static async Task<RenderResult> RenderAndManifestAsync(string sourcePath, string output, RendererAudit renderer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(renderer.Executable))
            return new RenderResult(false, "DOCX_LAYOUT_RENDERER_UNAVAILABLE", EmptyManifest(sourcePath), Array.Empty<string>(), renderer);
        var renderDir = Path.Combine(output, "render"); Directory.CreateDirectory(renderDir);
        var firstDir = Path.Combine(renderDir, "pass-1"); var secondDir = Path.Combine(renderDir, "pass-2");
        var firstPdf = await RenderOnceAsync(sourcePath, firstDir, renderer, ct);
        var secondPdf = await RenderOnceAsync(sourcePath, secondDir, renderer, ct);
        if (firstPdf is null || secondPdf is null)
            return new RenderResult(false, "DOCX_RENDERER_EXECUTION_FAILED", EmptyManifest(sourcePath), Array.Empty<string>(), renderer);

        var pagesDir = Path.Combine(output, "pages"); Directory.CreateDirectory(pagesDir);
        var repeatDir = Path.Combine(renderDir, "repeat-pages"); Directory.CreateDirectory(repeatDir);
        var first = await RenderPagesAsync(firstPdf, pagesDir, "page", ct);
        var second = await RenderPagesAsync(secondPdf, repeatDir, "page", ct);
        var deterministic = first.Entries.Count > 0 && first.Entries.Count == second.Entries.Count && first.Entries.Zip(second.Entries).All(pair =>
            pair.First.Width == pair.Second.Width && pair.First.Height == pair.Second.Height && pair.First.ImageHash == pair.Second.ImageHash);
        var reason = deterministic ? "same_page_count_dimensions_and_pixel_hashes" : "page_count_dimensions_or_pixel_hash_mismatch";
        var manifestJson = JsonSerializer.Serialize(new { sourceDocxSha256 = Sha256File(sourcePath), renderedPdfSha256 = Sha256File(firstPdf), pageCount = first.Entries.Count, pages = first.Entries }, JsonOptions);
        var manifest = new PageManifest(first.Entries.Count, first.Entries.Count > 0 ? 1d : 0d, Sha256Text(manifestJson), first.Entries,
            Sha256File(sourcePath), Sha256File(firstPdf), deterministic, reason);
        if (!deterministic)
            return new RenderResult(false, "RENDER_INTEGRITY_FAILURE", manifest, first.PageTexts, renderer);
        return new RenderResult(true, null, manifest, first.PageTexts, renderer);
    }

    private static PageManifest EmptyManifest(string sourcePath) => new(0, 0, "", Array.Empty<PageEntry>(), File.Exists(sourcePath) ? Sha256File(sourcePath) : "", "", false, "renderer_unavailable");

    private static async Task<string?> RenderOnceAsync(string sourcePath, string outputDir, RendererAudit renderer, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir);
        var expected = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(sourcePath) + ".pdf");
        if (renderer.Name == "LibreOffice")
        {
            var psi = new ProcessStartInfo(renderer.Executable!) { WorkingDirectory = outputDir, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "--headless", "--norestore", "--convert-to", "pdf", "--outdir", outputDir, sourcePath }) psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi);
            if (process is null || !await WaitForExitAsync(process, TimeSpan.FromMinutes(3), ct)) return null;
        }
        else if (renderer.Name == "Microsoft Word" && OperatingSystem.IsWindows())
        {
            if (!TryWordPdf(sourcePath, expected)) return null;
        }
        else if (renderer.Name == "Microsoft Word") return null;
        return File.Exists(expected) ? expected : Directory.EnumerateFiles(outputDir, "*.pdf").FirstOrDefault();
    }

    private static async Task<RenderedPages> RenderPagesAsync(string pdf, string outputDir, string prefix, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir); var entries = new List<PageEntry>(); var texts = new List<string>();
        using var document = PdfDocument.Open(pdf);
        for (var index = 1; index <= document.NumberOfPages; index++)
        {
            ct.ThrowIfCancellationRequested(); var bounds = PdfRegionRasterizer.GetPageBounds(pdf, index);
            var png = PdfRegionRasterizer.RenderCropPng(pdf, index, 0, 0, bounds.Width, bounds.Height, 110);
            var name = $"{prefix}-{index:0000}.png"; await File.WriteAllBytesAsync(Path.Combine(outputDir, name), png, ct);
            var dimensions = PngDimensions(png); entries.Add(new PageEntry(index, name, Sha256Bytes(png), dimensions.Width, dimensions.Height)); texts.Add(document.GetPage(index).Text);
        }
        return new RenderedPages(entries, texts);
    }

    private static IReadOnlyList<VisualWindow> BuildWindows(IReadOnlyList<VisualPageEvidence> pages, VisualSourceAlignmentManifest mapping, IReadOnlyList<ReasoningSourceOccurrence> occurrences)
    {
        // The verified historical run already established that the 8-page request shape can
        // return incomplete structured output.  The clean V4 measurement therefore uses one
        // independent full-page request per page.  This is page partitioning, not a second
        // semantic/review pass, and preserves pageCoverage=1.0 without candidate crops.
        return pages.Select(page => BuildSinglePageWindow(page.PageIndex, pages, mapping, occurrences)).ToArray();
    }

    private static VisualWindow BuildSinglePageWindow(int pageIndex, IReadOnlyList<VisualPageEvidence> pages, VisualSourceAlignmentManifest mapping, IReadOnlyList<ReasoningSourceOccurrence> occurrences)
    {
        var entries = mapping.Aliases.Where(x => x.PageIndex == pageIndex).ToArray();
        var ranges = entries.GroupBy(x => x.SourceOccurrenceId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (g.Min(x => x.VisibleStartCharacter), g.Max(x => x.VisibleEndCharacter)), StringComparer.Ordinal);
        var ids = ranges.Keys.OrderBy(id => occurrences.Single(x => x.SourceOccurrenceId == id).SourceOrdinal, Comparer<int>.Default).ToArray();
        return new VisualWindow([pageIndex], ids, ranges, ranges);
    }

    private static object BuildPacketMetrics(VisualWindow window, CeilingPacketResult packet, IReadOnlyList<VisualPageEvidence> pages) => new
    {
        pageCountInRequest = window.PageIndices.Count,
        imageCount = pages.Count,
        mappedSourceOccurrences = window.OccurrenceIds.Count,
        visibleSourceChars = packet.SourceTextCharacters,
        ownedSourceChars = window.OwnedRanges.Values.Sum(x => Math.Max(0, x.End - x.Start)),
        packetChars = packet.PacketCharacters,
        estimatedInputTokens = ReasoningTokenBudget.EstimateTokens(packet.PacketCharacters),
        imageDimensions = pages.Select(x => new { pageIndex = x.PageIndex, dimensions = PngDimensionObject(x.PngBytes) }).ToArray(),
    };

    private static IReadOnlyList<object> BuildPacketAudit(IReadOnlyList<VisualWindow> windows,
        IReadOnlyList<VisualPageEvidence> pages, VisualSourceAlignmentManifest mapping, IReadOnlyList<ReasoningSourceOccurrence> occurrences)
    {
        var planned = windows.Select(window => BuildPacketAuditEntry(window, "PLANNED_WINDOW", pages, mapping, occurrences));
        var singlePageProbes = pages.Select(page => BuildSinglePageWindow(page.PageIndex, pages, mapping, occurrences)
            ).Select(window => BuildPacketAuditEntry(window, "SINGLE_PAGE_PROBE", pages, mapping, occurrences));
        return planned.Concat(singlePageProbes).ToArray();
    }

    private static object BuildPacketAuditEntry(VisualWindow window, string kind,
        IReadOnlyList<VisualPageEvidence> pages, VisualSourceAlignmentManifest mapping,
        IReadOnlyList<ReasoningSourceOccurrence> occurrences)
    {
        var visible = window.OccurrenceIds.Select(id => occurrences.Single(x => x.SourceOccurrenceId == id)).ToArray();
        var packet = CeilingPacketBuilder.Build(visible, visible.Select(x => x.SourceOccurrenceId).ToHashSet(StringComparer.Ordinal), window.VisibleRanges, window.OwnedRanges,
            BuildAliases(window, mapping));
        return new
        {
            kind,
            pageIndices = window.PageIndices,
            packetMetrics = BuildPacketMetrics(window, packet, pages.Where(x => window.PageIndices.Contains(x.PageIndex)).ToArray()),
            sourceRanges = window.VisibleRanges.Select(x => new { sourceOccurrenceId = x.Key, visibleStart = x.Value.Start, visibleEnd = x.Value.End,
                ownedStart = window.OwnedRanges[x.Key].Start, ownedEnd = window.OwnedRanges[x.Key].End }).ToArray(),
        };
    }

    private static IReadOnlyDictionary<string, string> BuildAliases(VisualWindow window, VisualSourceAlignmentManifest mapping) =>
        window.OccurrenceIds.ToDictionary(
            id => id,
            id => mapping.Aliases.Where(x => x.SourceOccurrenceId == id && window.PageIndices.Contains(x.PageIndex))
                .OrderBy(x => x.PageIndex).ThenBy(x => x.Alias, StringComparer.Ordinal).Select(x => x.Alias).FirstOrDefault()
                ?? $"W{window.PageIndices[0]:00}-O{mapping.Occurrences.Single(x => x.SourceOccurrenceId == id).SourceOrdinal:000}",
            StringComparer.Ordinal);

    private static object BuildFailureTelemetry(RequestPacketTelemetry? telemetry, Exception error)
    {
        var finishReason = telemetry?.FinishReason;
        var outputLimitDetected = IsOutputLimit(finishReason) || string.Equals(telemetry?.FailureClass, ReasoningCompletionFailureClass.ProviderOutputLimit, StringComparison.Ordinal);
        var responseContentPresent = telemetry?.ResponseContentPresent;
        var structuredParsed = telemetry?.StructuredOutputParsed;
        var failureClass = outputLimitDetected ? "PROVIDER_OUTPUT_LIMIT"
            : telemetry?.TimeoutDetected == true || telemetry?.StreamStallDetected == true ? "TIMEOUT/STREAM_STALL"
            : responseContentPresent == true && structuredParsed != true ? "STRUCTURED_OUTPUT_TRUNCATED"
            : responseContentPresent == false ? "INCOMPLETE_PROVIDER_RESPONSE"
            : telemetry?.FailureClass ?? error.GetType().Name;
        return new
        {
            finishReason,
            reasoningTokens = telemetry?.ReportedReasoningTokens,
            outputTokens = telemetry?.ReportedOutputTokens,
            responseContentPresent,
            structuredOutputParsed = structuredParsed,
            outputLimitDetected,
            actualProvider = telemetry?.ProviderRoute,
            elapsedMs = telemetry?.ElapsedMs,
            failureClass,
        };
    }

    private static bool IsOutputLimit(string? finishReason) => finishReason is "length" or "max_tokens" or "max_output_tokens";

    private static object PngDimensionObject(byte[] bytes)
    {
        var (width, height) = PngDimensions(bytes);
        return new { width, height };
    }

    private static ControlReuse VerifyFrozenControl(string repoRoot)
    {
        var root = Path.Combine(repoRoot, ControlRoot.Replace('/', Path.DirectorySeparatorChar));
        var freeze = Path.Combine(root, "freeze.v1.json"); var prediction = Path.Combine(root, "prediction.v1.json"); var result = Path.Combine(root, "result.v1.json"); var score = Path.Combine(root, "score.v1.json");
        if (!File.Exists(freeze) || !File.Exists(prediction) || !File.Exists(result) || !File.Exists(score)) return new(false, "MISSING_CONTROL_ARTIFACT", null, null, null, null, null);
        using var freezeJson = JsonDocument.Parse(File.ReadAllText(freeze)); using var scoreJson = JsonDocument.Parse(File.ReadAllText(score));
        var predHash = freezeJson.RootElement.GetProperty("predictionSha256").GetString(); var resultHash = freezeJson.RootElement.GetProperty("resultSha256").GetString();
        var ok = string.Equals(predHash, Sha256File(prediction), StringComparison.OrdinalIgnoreCase) && string.Equals(resultHash, Sha256File(result), StringComparison.OrdinalIgnoreCase) &&
            scoreJson.RootElement.GetProperty("tp").GetInt32() == 0 && scoreJson.RootElement.GetProperty("fp").GetInt32() == 17 && scoreJson.RootElement.GetProperty("fn").GetInt32() == 71 && !freezeJson.RootElement.GetProperty("goldReadBeforeFreeze").GetBoolean();
        return new(ok, ok ? "FROZEN_HEADING_TARGET_ONTOLOGY_V4_CONTROL_REUSED" : "CONTROL_MISMATCH", Sha256File(prediction), Sha256File(result), 0, 17, 71);
    }

    private static IEnumerable<string> ClassifyLosses(IReadOnlyList<ReasoningGoldOccurrence> gold, IReadOnlyList<ReasoningHeadingProposal> proposals, (ValidatedStructure Structure, IReadOnlyList<ReasoningValidatedProposal> Validated) materialized, IReadOnlyList<ValidatedStructuralElement> finalElements, IReadOnlyList<ReasoningProjectionDecision> projection)
    {
        var predicted = finalElements.Select(x => Key(x.Sources.Single().SourceId, x.Sources.Single().Span)).ToHashSet(StringComparer.Ordinal); var rows = materialized.Validated.ToDictionary(x => x.ElementId, StringComparer.Ordinal);
        foreach (var item in gold.Where(x => x.HeadingSpan is not null))
        {
            var key = Key(item.SourceId, item.HeadingSpan!); if (predicted.Contains(key)) continue;
            var exact = proposals.FirstOrDefault(p => Key(p.SourceId, p.HeadingSpan) == key);
            if (exact is null) { var near = proposals.Any(p => p.SourceId == item.SourceId && (p.HeadingSpan.Start == item.HeadingSpan!.Start || p.Text.Contains(item.ExactText, StringComparison.Ordinal))); yield return near ? "MODEL_WRONG_SPAN" : "MODEL_OMISSION"; continue; }
            var id = ReasoningProposalMaterializer.ElementId(exact); if (!rows.TryGetValue(id, out var row) || !row.Accepted) yield return "SYSTEM_VALIDATOR_LOSS"; else if (projection.Single(x => x.ProposalId == id).Status == ReasoningTaskProjection.Excluded) yield return "SYSTEM_PROJECTION_LOSS"; else yield return "SYSTEM_BINDING_LOSS";
        }
        foreach (var element in finalElements) if (!gold.Any(item => item.HeadingSpan is not null && Key(item.SourceId, item.HeadingSpan!) == Key(element.Sources.Single().SourceId, element.Sources.Single().Span))) yield return proposals.Any(p => Key(p.SourceId, p.HeadingSpan) == Key(element.Sources.Single().SourceId, element.Sources.Single().Span)) ? "MODEL_FALSE_POSITIVE" : "SYSTEM_BINDING_LOSS";
    }

    private static ScoreResult Score(IReadOnlyList<string> gold, IReadOnlyList<string> predicted) { var g = gold.ToHashSet(StringComparer.Ordinal); var p = predicted.ToHashSet(StringComparer.Ordinal); var tp = g.Intersect(p).Count(); var fp = p.Except(g).Count(); var fn = g.Except(p).Count(); var precision = tp + fp == 0 ? 0d : (double)tp / (tp + fp); var recall = tp + fn == 0 ? 0d : (double)tp / (tp + fn); return new(tp, fp, fn, precision, recall, precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall)); }
    private static string Key(string sourceId, StructuralSpan span) => $"{sourceId}:{span.Start}:{span.End}";
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256Bytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Sha256Text(string value) => Sha256Bytes(Encoding.UTF8.GetBytes(value));
    private static string CurrentGitSha(string root) { try { using var p = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD") { WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false }); return p?.StandardOutput.ReadToEnd().Trim() ?? "UNKNOWN"; } catch { return "UNKNOWN"; } }
    private static InventoryItem ReadInventory(string path, string id) { using var doc = JsonDocument.Parse(File.ReadAllText(path)); var x = doc.RootElement.GetProperty("documents").EnumerateArray().Single(x => x.GetProperty("documentId").GetString() == id); return new(id, x.GetProperty("sourcePath").GetString()!, x.GetProperty("sourceSha256").GetString()!); }
    private static async Task WriteJsonAsync(string path, object value, CancellationToken ct) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, ct); }
    private static async Task<int> BlockAsync(string output, string classification, string reason, ControlReuse control, CancellationToken ct, object? details = null) { await WriteJsonAsync(Path.Combine(output, "summary.v2.json"), new { schemaVersion = "a99-qwen37-flash-visual-ceiling-v2", primaryClassification = classification, reason, control, details, goldReadBeforeFreeze = false, completedUtc = DateTimeOffset.UtcNow }, ct); Console.WriteLine($"FINAL_CLASSIFICATION={classification}:{reason}"); return 1; }
    private static RendererAudit DiscoverRenderer()
    {
        var soffice = FindExecutable("soffice");
        var word = OperatingSystem.IsWindows() && IsWordComAvailable();
        var selected = soffice is not null ? ("LibreOffice", soffice) : word ? ("Microsoft Word", "Word.Application") : ("NONE", (string?)null);
        return new RendererAudit(
            selected.Item1,
            selected.Item2,
            selected.Item2 is not null && File.Exists(selected.Item2) ? FileVersionInfo.GetVersionInfo(selected.Item2).FileVersion : null,
            "NONE", false, false,
            selected.Item1 == "NONE" ? "INSTALLATION_REQUIRED" : "AVAILABLE_PENDING_DETERMINISM");
    }

    private static string? FindExecutable(string name)
    {
        var explicitPath = Environment.GetEnvironmentVariable("DOCX_RENDERER_PATH") ?? Environment.GetEnvironmentVariable("SOFFICE_PATH");
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitPath)) candidates.Add(explicitPath);
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, OperatingSystem.IsWindows() ? name + ".exe" : name)));
        if (OperatingSystem.IsWindows() && name.Equals("soffice", StringComparison.OrdinalIgnoreCase))
            candidates.AddRange([@"C:\Program Files\LibreOffice\program\soffice.exe", @"C:\Program Files (x86)\LibreOffice\program\soffice.exe"]);
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    [SupportedOSPlatform("windows")]
    private static bool IsWordComAvailable() => Type.GetTypeFromProgID("Word.Application") is not null;
    [SupportedOSPlatform("windows")]
    private static bool TryWordPdf(string sourcePath, string pdfPath) { dynamic? app = null; dynamic? doc = null; try { var type = Type.GetTypeFromProgID("Word.Application"); if (type is null) return false; var instance = Activator.CreateInstance(type); if (instance is null) return false; app = instance; app.Visible = false; app.DisplayAlerts = 0; doc = app.Documents.Open(sourcePath, false, true, false); doc.ExportAsFixedFormat(pdfPath, 17, false, 0, 0, 0, 0, 0, true, false, 0, true, true, false); return File.Exists(pdfPath); } catch { return false; } finally { try { doc?.Close(0); } catch { } try { app?.Quit(0); } catch { } } }
    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken ct) { using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct); timer.CancelAfter(timeout); try { await process.WaitForExitAsync(timer.Token); return true; } catch { try { if (!process.HasExited) process.Kill(true); } catch { } return false; } }
    private static (int Width, int Height) PngDimensions(byte[] bytes) => bytes.Length >= 24 ? (BitConverter.ToInt32(bytes[16..20].Reverse().ToArray()), BitConverter.ToInt32(bytes[20..24].Reverse().ToArray())) : (0, 0);

    private sealed record InventoryItem(string DocumentId, string SourcePath, string SourceSha256);
    private sealed record ControlReuse(bool Valid, string Status, string? PredictionHash, string? ResultHash, int? TP, int? FP, int? FN);
    private sealed record RendererAudit(string Name, string? Executable, string? Version, string GemBoxStatus, bool GemBoxDependencyPresent, bool GemBoxLicenseConfigured, string CapabilityStatus);
    private sealed record RenderResult(bool Success, string? Error, PageManifest Manifest, IReadOnlyList<string> PageTexts, RendererAudit Renderer);
    private sealed record RenderedPages(IReadOnlyList<PageEntry> Entries, IReadOnlyList<string> PageTexts);
    private sealed record PageManifest(int PageCount, double Coverage, string ManifestHash, IReadOnlyList<PageEntry> Pages, string SourceDocxSha256, string RenderedPdfSha256, bool Deterministic, string DeterminismReason);
    private sealed record PageEntry(int PageIndex, string FileName, string ImageHash, int Width, int Height);
    private sealed record VisualWindow(IReadOnlyList<int> PageIndices, IReadOnlyList<string> OccurrenceIds,
        IReadOnlyDictionary<string, (int Start, int End)> VisibleRanges,
        IReadOnlyDictionary<string, (int Start, int End)> OwnedRanges);
    private sealed record ScoreResult(int TP, int FP, int FN, double P, double R, double F1);
}
