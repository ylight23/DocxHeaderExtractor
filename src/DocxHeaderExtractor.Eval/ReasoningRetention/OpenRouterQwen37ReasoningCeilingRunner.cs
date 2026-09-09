using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Policy;
using DocxHeaderExtractor.Eval.StrictGoldOccurrence;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>Public-document A/B ceiling campaign for qwen/qwen3.7-flash. R0 and R1 share the
/// exact source packet and semantic contract; only the reasoning transport field differs. The
/// non-ZDR setting is explicit and campaign-scoped, never a default privacy change.</summary>
public static class OpenRouterQwen37ReasoningCeilingRunner
{
    private const string Model = "qwen/qwen3.7-flash";
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const string InventoryPath = "eval/a99-dataset/document-inventory.v1.json";
    private const string OutputRoot = "eval/a99-closed-loop/qwen37-flash-reasoning-ceiling";
    private static readonly string[] SelectedIds = ["DOC-0205", "DOC-0258"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(output);
        var selected = ReadInventory(Path.Combine(repoRoot, InventoryPath))
            .Where(x => SelectedIds.Contains(x.DocumentId, StringComparer.Ordinal)).ToArray();
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            return await WriteBlockedAsync(output, "FLASH_PROVIDER_EXECUTION_BLOCKED", "OPENROUTER_API_KEY_MISSING", ct);

        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(repoRoot, "qwen37-flash-reasoning-ceiling", string.Join(',', SelectedIds), ct);
        Console.WriteLine($"LIVE_PROVIDER_LOCK=acquired concurrentCampaignsDetected={lease.ConcurrentCampaignsDetected} providerConcurrency={lease.ProviderConcurrency}");

        var baseOptions = new RemoteInferenceOptions
        {
            Endpoint = new Uri(Endpoint), Model = Model, ApiKey = key,
            ContextSize = 1_000_000, MaxOutputTokens = 48_000, RequestTimeoutSeconds = 600,
            TransientRequestRetries = 0, MaxParallelRequests = 1, SendChatTemplateKwargs = false,
            OpenRouterAllowNonZdrPublicBenchmark = true,
        };
        var preflight = await OpenRouterModelCapabilityResolver.ResolveAsync(baseOptions, http, ct);
        var manifest = new
        {
            schemaVersion = "a99-qwen37-flash-reasoning-ceiling-v1", model = Model, provider = "OpenRouter",
            dataClassification = "PUBLIC", zdrRequested = false, privacyExceptionAuthorized = true,
            privacyExceptionScope = "THIS_CAMPAIGN_ONLY", modelFallback = "NONE", selectedDocuments = SelectedIds,
            preflightAvailable = preflight.Available, preflightClassification = preflight.Classification,
            preflightReason = preflight.Reason, capability = preflight.Capability,
            goldReadBeforeFreeze = false, startedUtc = DateTimeOffset.UtcNow,
        };
        await WriteJsonAsync(Path.Combine(output, "campaign-manifest.v1.json"), manifest, ct);
        if (!preflight.Available || preflight.Capability is null)
            return await WriteBlockedAsync(output, "FLASH_PROVIDER_EXECUTION_BLOCKED", preflight.Reason, ct);
        var capability = preflight.Capability;
        Console.WriteLine($"MODEL={Model}");
        Console.WriteLine($"CONTEXT_LENGTH={capability.ContextLength}");
        Console.WriteLine($"REASONING_SUPPORTED={capability.ReasoningSupported}");
        Console.WriteLine($"SUPPORTED_EFFORTS={string.Join(',', capability.SupportedReasoningEfforts)}");
        Console.WriteLine($"SELECTED_REASONING_EFFORT={capability.SelectedReasoningEffort}");
        Console.WriteLine($"STRUCTURED_OUTPUT_SUPPORTED={capability.StructuredOutputSupported}");
        Console.WriteLine($"MAX_COMPLETION_IF_KNOWN={capability.MaxCompletionTokens?.ToString() ?? "unknown"}");

        if (!string.Equals(capability.ModelId, Model, StringComparison.Ordinal) || !capability.ReasoningSupported || !capability.StructuredOutputSupported)
            return await WriteBlockedAsync(output, "FLASH_PROVIDER_EXECUTION_BLOCKED", "MODEL_CAPABILITY_MISMATCH", ct);

        var sanity = await RunSanityAsync(http, key, ct);
        await WriteJsonAsync(Path.Combine(output, "sanity-request.v1.json"), sanity, ct);
        if (!sanity.Success)
            return await WriteBlockedAsync(output, "FLASH_PROVIDER_EXECUTION_BLOCKED", "SANITY_REQUEST_FAILED", ct);

        var r0 = await RunModeAsync(repoRoot, output, selected.Single(x => x.DocumentId == "DOC-0205"), capability, baseOptions, false, http, ct);
        var r1 = await RunModeAsync(repoRoot, output, selected.Single(x => x.DocumentId == "DOC-0205"), capability, baseOptions, true, http, ct);
        ModeMetric? doc0258 = null;
        if (r1.ExactStatus == "EVALUABLE")
            doc0258 = await RunModeAsync(repoRoot, output, selected.Single(x => x.DocumentId == "DOC-0258"), capability, baseOptions, true, http, ct);

        var primary = r1.ExactStatus == "EVALUABLE"
            ? r1.SystemLossCount > 0 ? "FLASH_SYSTEM_LOSS_INVALIDATES_CEILING" : "FLASH_REASONING_CEILING_MEASURED"
            : "FLASH_REASONING_CEILING_EXECUTION_BLOCKED";
        var reasoningEffect = r0.ExactStatus != "EVALUABLE" || r1.ExactStatus != "EVALUABLE"
            ? "REASONING_CONTROL_NOT_SUPPORTED"
            : r1.F1 - r0.F1 > 0.05 || r1.FN + 3 < r0.FN ? "REASONING_MATERIALLY_IMPROVES_CAPABILITY"
            : Math.Abs(r1.F1 - r0.F1) > 0.02 ? "REASONING_PRECISION_RECALL_TRADEOFF" : "REASONING_NO_MATERIAL_GAIN";
        var providerNotHeldConstant = r0.ActualProvider != r1.ActualProvider && r0.ActualProvider != "NOT_EXPOSED" && r1.ActualProvider != "NOT_EXPOSED";
        var modelComparison = r1.ExactStatus != "EVALUABLE" ? "MODEL_CAPABILITY_NOT_MEASURED"
            : r1.FN + 10 < 71 ? "MODEL_CAPABILITY_GAP_SUPPORTED" : "TEXT_CONTRACT_OR_TASK_DIFFICULTY_SUSPECTED";
        await WriteJsonAsync(Path.Combine(output, "comparison.v1.json"), new
        {
            schemaVersion = "a99-qwen37-flash-reasoning-comparison-v1", modelControl = new
            {
                model = "qwen/qwen3.5-9b", reasoning = "CEILING", tp = 0, fp = 15, fn = 71, precision = 0, recall = 0,
                f1 = 0, modelOmission = 69, spanError = 2, systemLoss = 0, frozen = true,
            },
            doc0205 = new { r0, r1 }, doc0258 = doc0258 is null ? null : new { r1 = doc0258 },
            providerNotHeldConstant, reasoningEffect, modelComparison, primary, goldReadBeforeFreeze = false,
        }, ct);
        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-qwen37-flash-reasoning-ceiling-v1", primaryClassification = primary,
            reasoningEffect, modelComparison, dataClassification = "PUBLIC", zdrRequested = false,
            privacyExceptionAuthorized = true, privacyExceptionScope = "THIS_CAMPAIGN_ONLY",
            sanity, capability, doc0205 = new { r0, r1 }, doc0258 = doc0258 is null ? null : new { r1 = doc0258 },
            providerNotHeldConstant, goldReadBeforeFreeze = false,
            vlmNextStep = r1.ExactStatus != "EVALUABLE" ? "VLM_UNDECIDED" : "VLM_WORTH_TESTING",
            completedUtc = DateTimeOffset.UtcNow,
        }, ct);
        Console.WriteLine($"PRIMARY_CLASSIFICATION={primary}");
        Console.WriteLine($"REASONING_EFFECT={reasoningEffect}");
        Console.WriteLine($"MODEL_COMPARISON={modelComparison}");
        Console.WriteLine($"VLM_NEXT_STEP={(r1.ExactStatus == "EVALUABLE" ? "VLM_WORTH_TESTING" : "VLM_UNDECIDED")}");
        return primary == "FLASH_REASONING_CEILING_MEASURED" ? 0 : 1;
    }

    private static async Task<ModeMetric> RunModeAsync(string repoRoot, string output, InventoryItem item,
        OpenRouterModelCapability capability, RemoteInferenceOptions baseOptions, bool reasoningEnabled,
        HttpClient http, CancellationToken ct)
    {
        var mode = reasoningEnabled ? "r1-ceiling" : "r0-control";
        var modeLabel = reasoningEnabled ? "R1_CEILING" : "R0_NO_REASONING";
        var docDir = Path.Combine(output, item.DocumentId, mode);
        Directory.CreateDirectory(docDir);
        var options = Clone(baseOptions, reasoningEnabled ? null : false);
        using var model = new OpenRouterCeilingReasoningModel(options, capability, http);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var sourcePath = Path.Combine(repoRoot, item.SourcePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(sourcePath) || !string.Equals(Sha256File(sourcePath), item.SourceSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SOURCE_HASH_MISMATCH");
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
                var visible = segment.VisibleStartCharacter is { } vs && segment.VisibleEndCharacter is { } ve && segment.OwnedSourceOccurrenceId == id
                    ? (vs, ve) : (0, occurrence.RawText.Length);
                visibleWindow[id] = visible;
                ownedWindow[id] = owned.Contains(id)
                    ? segment.OwnedSourceOccurrenceId == id && segment.OwnedStartCharacter is { } os && segment.OwnedEndCharacter is { } oe
                        ? (os, oe) : (0, occurrence.RawText.Length)
                    : (visible.Item1, visible.Item1);
            }
            var packet = CeilingPacketBuilder.Build(segment.SourceOccurrenceIds.Select(id => occurrences[id]).ToArray(), owned, visibleWindow, ownedWindow);
            var promptHash = Sha256Text(CeilingSemanticPrompt.ProtocolVersion + "\n" + CeilingSemanticPrompt.System);
            var schemaHash = Sha256Text(JsonSerializer.Serialize(CeilingSemanticPrompt.Schema()));
            var packetHash = Sha256Text(packet.SerializedJson);
            var configurationSignature = Sha256Text($"A99_QWEN37_FLASH_REASONING_CEILING|{Model}|{modeLabel}|{promptHash}|{schemaHash}|{packetHash}|temperature=0|max_completion={model.SemanticMaxCompletionTokens}|zdr=false");
            var requestId = $"{modeLabel}:{item.DocumentId}:{configurationSignature}";
            var (response, _) = await model.CompleteSemanticAsync(item.DocumentId, ReasoningRoute.ModelCapabilityCeiling.ToString(), requestId,
                packet.SerializedJson, packet.SourceTextCharacters, owned.Count, segment.SourceOccurrenceIds.Count, ct);
            var proposals = new List<ReasoningHeadingProposal>();
            var spanErrors = 0;
            foreach (var heading in response.Headings)
            {
                if (heading.I < 0 || heading.I >= packet.Bindings.Count) { spanErrors++; continue; }
                var binding = packet.Bindings[heading.I];
                if (!owned.Contains(binding.SourceOccurrenceId) || !binding.TryBind(heading.Start, heading.End, out var globalStart, out var globalEnd, out var ownedSpan) || !ownedSpan)
                { spanErrors++; continue; }
                var occurrence = occurrences[binding.SourceOccurrenceId];
                proposals.Add(new ReasoningHeadingProposal
                {
                    SourceId = occurrence.SourceId, HeadingSpan = new StructuralSpan(globalStart, globalEnd),
                    Text = occurrence.RawText[globalStart..globalEnd], SemanticRole = heading.Role, Confidence = 1,
                });
            }
            var materialized = ReasoningProposalMaterializer.Materialize(source, policy, proposals);
            var structureProjection = ReasoningTaskProjection.Project(materialized.Structure).ToDictionary(x => x.ProposalId, StringComparer.Ordinal);
            var projection = materialized.Validated.Select(row => structureProjection.GetValueOrDefault(row.ElementId) ??
                new ReasoningProjectionDecision(row.ElementId, ReasoningTaskProjection.Excluded, "VALIDATION_REJECTED")).ToArray();
            var finalElements = ReasoningTaskProjection.ProjectContentHeadings(materialized.Structure)
                .OrderBy(x => x.Sources.Single().SourceOrdinal).ThenBy(x => x.Sources.Single().Span.Start).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
            var predictionKeys = finalElements.Select(Key).ToArray();
            var telemetry = model.Telemetry.Where(x => x.DocumentId == item.DocumentId).ToArray();
            var prediction = new
            {
                documentId = item.DocumentId, model = Model, reasoningMode = modeLabel, executionMode = "FULL_CONTEXT_CEILING",
                sourceSha256 = item.SourceSha256, sourceCharacters = pack.SourceCharacters, packetCharacters = packet.PacketCharacters,
                packetHash, promptHash, schemaHash, semanticProtocolVersion = CeilingSemanticPrompt.ProtocolVersion,
                rawProposalCount = response.Headings.Count, boundProposalCount = proposals.Count, spanErrorCount = spanErrors,
                validatedSemanticCount = materialized.Validated.Count(x => x.Accepted), proposals, projection, headings = finalElements,
                goldReadBeforeFreeze = false,
            };
            var result = new { documentId = item.DocumentId, model = Model, reasoningMode = modeLabel, headings = finalElements, goldReadBeforeFreeze = false };
            var predictionPath = Path.Combine(docDir, "prediction.v1.json");
            var resultPath = Path.Combine(docDir, "result.v1.json");
            await WriteJsonAsync(predictionPath, prediction, ct);
            await WriteJsonAsync(resultPath, result, ct);
            var predictionHash = Sha256File(predictionPath); var resultHash = Sha256File(resultPath);
            var actualProvider = telemetry.Select(x => x.ProviderRoute).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "NOT_EXPOSED";
            var freeze = new
            {
                documentId = item.DocumentId, model = Model, actualProvider, reasoningMode = modeLabel,
                reasoningConfiguration = new { requested = reasoningEnabled, enabled = reasoningEnabled, exclude = reasoningEnabled },
                dataClassification = "PUBLIC", zdrRequested = false, privacyExceptionAuthorized = true,
                privacyExceptionScope = "THIS_CAMPAIGN_ONLY", gitSha = CurrentGitSha(repoRoot), sourceSha256 = item.SourceSha256,
                promptHash, packetHash, schemaHash, configurationSignature, predictionSha256 = predictionHash, resultSha256 = resultHash,
                finishReason = telemetry.LastOrDefault()?.FinishReason, providerAttempts = model.ProviderCalls,
                inputTokens = telemetry.Sum(x => x.ReportedInputTokens ?? 0), reasoningTokens = telemetry.Sum(x => x.ReportedReasoningTokens ?? 0),
                outputTokens = telemetry.Sum(x => x.ReportedOutputTokens ?? 0), executionMode = "FULL_CONTEXT_CEILING",
                goldReadBeforeFreeze = false, frozenUtc = DateTimeOffset.UtcNow,
            };
            var freezePath = Path.Combine(docDir, "freeze.v1.json");
            await WriteJsonAsync(freezePath, freeze, ct);
            var freezeCheck = JsonDocument.Parse(await File.ReadAllTextAsync(freezePath, ct));
            if (!string.Equals(Sha256File(predictionPath), predictionHash, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Sha256File(resultPath), resultHash, StringComparison.OrdinalIgnoreCase) ||
                freezeCheck.RootElement.GetProperty("goldReadBeforeFreeze").GetBoolean())
                throw new InvalidDataException("FREEZE_HASH_VERIFICATION_FAILED");

            var goldPath = Path.Combine(repoRoot, "eval", "a99-closed-loop", "strict-gold-occurrence-v1", $"{item.DocumentId}.occurrence-gold-v1.json");
            var gold = ReasoningGoldArtifactLoader.LoadOccurrence(goldPath);
            var goldKeys = gold.Where(x => x.HeadingSpan is not null).Select(x => Key(x.SourceId, x.HeadingSpan!)).ToArray();
            var score = Score(goldKeys, predictionKeys);
            var losses = ClassifyLosses(gold, proposals, materialized, finalElements, projection).GroupBy(x => x).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
            var systemLoss = losses.Where(x => x.Key.StartsWith("SYSTEM_", StringComparison.Ordinal)).Sum(x => x.Value);
            await WriteJsonAsync(Path.Combine(docDir, "score.v1.json"), new
            {
                documentId = item.DocumentId, exactStatus = "EVALUABLE", goldCount = goldKeys.Length, tp = score.TP, fp = score.FP, fn = score.FN,
                precision = score.P, recall = score.R, f1 = score.F1, lossCounts = losses, systemLossCount = systemLoss, goldReadBeforeFreeze = false,
            }, ct);
            await WriteJsonAsync(Path.Combine(docDir, "first-loss.v1.json"), new { documentId = item.DocumentId, losses, goldReadBeforeFreeze = false }, ct);
            stopwatch.Stop();
            var metric = new ModeMetric(item.DocumentId, modeLabel, "FULL_CONTEXT_CEILING", "EVALUABLE", actualProvider,
                score.TP, score.FP, score.FN, score.P, score.R, score.F1, losses.GetValueOrDefault("MODEL_OMISSION"),
                losses.GetValueOrDefault("MODEL_SPAN_ERROR"), systemLoss, model.ProviderCalls,
                telemetry.Sum(x => x.ReportedReasoningTokens ?? 0), stopwatch.ElapsedMilliseconds, predictionKeys.Length);
            await WriteJsonAsync(Path.Combine(docDir, "execution.v1.json"), new { metric, telemetry, goldReadBeforeFreeze = false }, ct);
            return metric;
        }
        catch (Exception ex) when (ex is ReasoningCompletionException or HttpRequestException or InvalidDataException or FormatException or JsonException)
        {
            stopwatch.Stop();
            var telemetry = model.Telemetry.Where(x => x.DocumentId == item.DocumentId).ToArray();
            var metric = new ModeMetric(item.DocumentId, modeLabel, "FULL_CONTEXT_CEILING", "BLOCKED", telemetry.Select(x => x.ProviderRoute).FirstOrDefault() ?? "NOT_EXPOSED",
                0, 0, 0, 0, 0, 0, 0, 0, 0, model.ProviderCalls, telemetry.Sum(x => x.ReportedReasoningTokens ?? 0), stopwatch.ElapsedMilliseconds, 0);
            await WriteJsonAsync(Path.Combine(docDir, "execution.v1.json"), new
            {
                documentId = item.DocumentId, mode = modeLabel, status = "BLOCKED", error = ex.Message, metric, telemetry,
                goldReadBeforeFreeze = false,
            }, ct);
            Console.Error.WriteLine($"FLASH_{modeLabel}_FAILURE={item.DocumentId}:{ex.Message}");
            return metric;
        }
    }

    private static async Task<SanityResult> RunSanityAsync(HttpClient http, string key, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = Model,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Reply with the word OK." }),
            ["reasoning"] = new JsonObject { ["enabled"] = true },
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = "a99_flash_sanity", ["strict"] = true,
                    ["schema"] = new JsonObject
                    {
                        ["type"] = "object", ["properties"] = new JsonObject { ["ok"] = new JsonObject { ["type"] = "boolean" } },
                        ["required"] = new JsonArray("ok"), ["additionalProperties"] = false,
                    },
                },
            },
        };
        var requestJson = body.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(45));
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new StringContent(requestJson, Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var raw = await response.Content.ReadAsStringAsync(timeout.Token);
            string? reportedModel = null, provider = null, errorBody = null;
            try
            {
                using var json = JsonDocument.Parse(raw);
                reportedModel = ReadString(json.RootElement, "model"); provider = ReadString(json.RootElement, "provider") ?? ReadString(json.RootElement, "provider_name");
                errorBody = response.IsSuccessStatusCode ? null : raw;
            }
            catch (JsonException) { errorBody = raw; }
            return new SanityResult(response.IsSuccessStatusCode, (int)response.StatusCode, errorBody, reportedModel, provider,
                true, true, false, Sha256Text(requestJson));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return new SanityResult(false, 0, ex.Message, null, null, true, true, false, Sha256Text(requestJson));
        }
    }

    private static RemoteInferenceOptions Clone(RemoteInferenceOptions source, bool? reasoningOverride) => new()
    {
        Endpoint = source.Endpoint, ApiKey = source.ApiKey, Model = source.Model, ContextSize = source.ContextSize,
        MaxOutputTokens = source.MaxOutputTokens, RequestTimeoutSeconds = source.RequestTimeoutSeconds,
        TransientRequestRetries = source.TransientRequestRetries, MaxParallelRequests = 1, SendChatTemplateKwargs = false,
        OpenRouterAllowNonZdrPublicBenchmark = true, OpenRouterReasoningEnabledOverride = reasoningOverride,
    };

    private static IEnumerable<string> ClassifyLosses(IReadOnlyList<ReasoningGoldOccurrence> gold, IReadOnlyList<ReasoningHeadingProposal> proposals,
        (ValidatedStructure Structure, IReadOnlyList<ReasoningValidatedProposal> Validated) materialized,
        IReadOnlyList<ValidatedStructuralElement> finalElements, IReadOnlyList<ReasoningProjectionDecision> projection)
    {
        var predicted = finalElements.Select(Key).ToHashSet(StringComparer.Ordinal);
        var raw = proposals.ToArray(); var rows = materialized.Validated.ToDictionary(x => x.ElementId, StringComparer.Ordinal);
        foreach (var item in gold.Where(x => x.HeadingSpan is not null))
        {
            var key = Key(item.SourceId, item.HeadingSpan!); if (predicted.Contains(key)) continue;
            var exact = raw.FirstOrDefault(p => Key(p) == key);
            if (exact is null)
            {
                var near = raw.Any(p => p.SourceId == item.SourceId && (p.HeadingSpan.Start == item.HeadingSpan!.Start || p.Text.Contains(item.ExactText, StringComparison.Ordinal)));
                yield return near ? "MODEL_SPAN_ERROR" : "MODEL_OMISSION"; continue;
            }
            var id = ReasoningProposalMaterializer.ElementId(exact);
            if (!rows.TryGetValue(id, out var row) || !row.Accepted) yield return "SYSTEM_VALIDATOR_LOSS";
            else if (projection.Single(x => x.ProposalId == id).Status == ReasoningTaskProjection.Excluded) yield return "SYSTEM_PROJECTION_LOSS";
            else yield return "SYSTEM_BINDING_LOSS";
        }
        foreach (var element in finalElements)
        {
            if (gold.Any(item => item.HeadingSpan is not null && Key(item.SourceId, item.HeadingSpan!) == Key(element))) continue;
            yield return raw.Any(p => Key(p) == Key(element)) ? "MODEL_FALSE_POSITIVE" : "SYSTEM_BINDING_LOSS";
        }
    }

    private static string Key(ReasoningHeadingProposal p) => Key(p.SourceId, p.HeadingSpan);
    private static string Key(ValidatedStructuralElement e) => Key(e.Sources.Single().SourceId, e.Sources.Single().Span);
    private static string Key(string sourceId, StructuralSpan span) => $"{sourceId}:{span.Start}:{span.End}";
    private static string? ReadString(JsonElement parent, string name) => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string CurrentGitSha(string repoRoot)
    {
        try { using var p = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD") { WorkingDirectory = repoRoot, RedirectStandardOutput = true, UseShellExecute = false }); return p?.StandardOutput.ReadToEnd().Trim() ?? "UNKNOWN"; }
        catch { return "UNKNOWN"; }
    }
    private static ScoreResult Score(IReadOnlyList<string> gold, IReadOnlyList<string> predicted)
    {
        var g = gold.ToHashSet(StringComparer.Ordinal); var p = predicted.ToHashSet(StringComparer.Ordinal);
        var tp = g.Intersect(p).Count(); var fp = p.Except(g).Count(); var fn = g.Except(p).Count();
        var precision = tp + fp == 0 ? 0d : (double)tp / (tp + fp); var recall = tp + fn == 0 ? 0d : (double)tp / (tp + fn);
        return new(tp, fp, fn, precision, recall, precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall));
    }
    private static InventoryItem[] ReadInventory(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("documents").EnumerateArray().Select(x => new InventoryItem(
            x.GetProperty("documentId").GetString()!, x.GetProperty("sourcePath").GetString()!, x.GetProperty("sourceSha256").GetString()!)).ToArray();
    }
    private static async Task<int> WriteBlockedAsync(string output, string classification, string reason, CancellationToken ct)
    {
        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new { status = classification, reason, model = Model, goldReadBeforeFreeze = false }, ct);
        Console.WriteLine($"PRIMARY_CLASSIFICATION={classification}"); return 1;
    }
    private static async Task WriteJsonAsync(string path, object value, CancellationToken ct) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, ct);

    private sealed record InventoryItem(string DocumentId, string SourcePath, string SourceSha256);
    private sealed record SanityResult(bool Success, int Status, string? ErrorBody, string? ReportedModel, string? Provider,
        bool ReasoningRequested, bool StructuredOutputRequested, bool ZdrRequested, string RequestBodySha256);
    private sealed record ScoreResult(int TP, int FP, int FN, double P, double R, double F1);
    private sealed record ModeMetric(string DocumentId, string ReasoningMode, string ExecutionMode, string ExactStatus, string ActualProvider,
        int TP, int FP, int FN, double P, double R, double F1, int ModelOmission, int SpanError, int SystemLossCount,
        int ProviderAttempts, int ReasoningTokens, long WallTimeMs, int FinalHeadingCount);
}
