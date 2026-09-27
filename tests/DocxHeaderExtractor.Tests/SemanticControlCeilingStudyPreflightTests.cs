using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Tests;

/// <summary>Provider-free preflight for the reasoning-only counterfactual. It never constructs a provider.</summary>
public sealed class SemanticControlCeilingStudyPreflightTests
{
    private const string Root = "eval/a99-closed-loop/semantic-control-ceiling-study";
    private const string Model = "qwen/qwen3.7-flash";
    private const string PromptSha256 = "e996bef4346efff9b0544f34777192d5b59c7e7f75a749dbf91cbe9f0d5df93b";
    private const string SchemaSha256 = "7d8ae805c0373dad3795d819cd5b0229b9421c28600d5d6b001e818f839225db";
    private const int ProductionMaxOutputTokens = 32768;
    private static readonly (string Id, string Pdf)[] Documents =
    [
        ("SRC-089", Src089BlindGeneralizationTests.Pdf),
        ("SRC-095", Src095BlindGeneralizationTests.Pdf),
    ];

    private static readonly CanonicalSemanticExperiment V4 = CanonicalSemanticExperiment.Baseline with
    {
        RequestVersion = SemanticRequestVersion.V4_SEMANTIC_FUNCTION_SINGLE_AUTHORITY,
    };

    [Fact]
    public async Task Freeze_reasoning_only_preflight_and_stop_before_provider()
    {
        if (Environment.GetEnvironmentVariable("A99_LLM_V4R1_RUN") is "1" or "true" or "TRUE") return;
        var documents = new List<object>();
        var allSameSemanticRequestHashes = true;
        var allCalls = 0;
        var maxOutputCeiling = 0;

        foreach (var (id, pdf) in Documents)
        {
            using var capture = new RequestCapturingClassifier();
            using var treatmentCapture = new RequestCapturingClassifier();
            await CanonicalSemanticPdfAuthorityAdapter.RunAsync(TestRepository.Path(pdf), capture, CancellationToken.None,
                experiment: V4, profile: PdfSemanticAuthorityProfile.StructuredSourceParts,
                packingPolicy: SemanticEvidencePackingPolicies.FixedOwnedCount120,
                sourceFacts: PdfSourceFactsVersion.V3_RobustGlyphStatistics, runPlacement: false);
            await CanonicalSemanticPdfAuthorityAdapter.RunAsync(TestRepository.Path(pdf), treatmentCapture, CancellationToken.None,
                experiment: V4, profile: PdfSemanticAuthorityProfile.StructuredSourceParts,
                packingPolicy: SemanticEvidencePackingPolicies.FixedOwnedCount120,
                sourceFacts: PdfSourceFactsVersion.V3_RobustGlyphStatistics, runPlacement: false);
            var baselineSemanticHashes = capture.Requests.Select(request => Sha(request.SystemPrompt + "\n" + request.UserMessage)).ToArray();
            var treatmentSemanticHashes = treatmentCapture.Requests.Select(request => Sha(request.SystemPrompt + "\n" + request.UserMessage)).ToArray();
            Assert.Equal(baselineSemanticHashes, treatmentSemanticHashes);

            var baseline = new RemoteInferenceOptions { Model = Model, OpenRouterReasoningEffort = "none" };
            var r1 = new RemoteInferenceOptions { Model = Model, OpenRouterReasoningEffort = "medium" };
            var rows = capture.Requests.Select(request =>
            {
                var requestHash = Sha(request.UserMessage);
                var maxTokens = OpenRouterHeaderExtractor.BoundaryOutputBudgetFor(
                    request.UserMessage, request.ExpectedItemCount, ProductionMaxOutputTokens);
                var baselineFingerprint = ProviderSemanticExecutionFingerprint.ForOpenRouterBoundary(baseline, maxTokens, SchemaSha256);
                var r1Fingerprint = ProviderSemanticExecutionFingerprint.ForOpenRouterBoundary(r1, maxTokens, SchemaSha256);
                allCalls++;
                return new
                {
                    requestSha256 = requestHash,
                    expectedItemCount = request.ExpectedItemCount,
                    maxTokens,
                    baselineFingerprint = baselineFingerprint.Sha256,
                    r1Fingerprint = r1Fingerprint.Sha256,
                    semanticRequestUnchanged = true,
                    reasoningOnlyDelta = baselineFingerprint.Sha256 != r1Fingerprint.Sha256,
                };
            }).ToArray();
            allSameSemanticRequestHashes &= rows.All(row => row.semanticRequestUnchanged) && baselineSemanticHashes.SequenceEqual(treatmentSemanticHashes);
            maxOutputCeiling += rows.Sum(row => row.maxTokens);
            documents.Add(new { documentId = id, calls = rows.Length, requests = rows });
        }

        Assert.Equal(25, allCalls);
        Assert.True(allSameSemanticRequestHashes);
        FreezeArtifact.AssertJson(Root, "r1-reasoning-preflight.v1.json", new
        {
            artifactKind = "a99_semantic_control_ceiling_reasoning_only_preflight",
            study = "A99_SEMANTIC_CONTROL_CEILING_STUDY",
            requestVersion = "V4R1_SEMANTIC_FUNCTION_EXPLICIT_REASONING",
            treatment = new
            {
                onlyChangedField = "OpenRouter reasoning.effort",
                baselineValue = "none",
                treatmentValue = "medium",
                semanticContract = "V4_SEMANTIC_FUNCTION_SINGLE_AUTHORITY",
            },
            pins = new
            {
                model = Model,
                promptSha256 = PromptSha256,
                schemaSha256 = SchemaSha256,
                factsVersion = "V3_RobustGlyphStatistics",
                packing = "FIXED_OWNED_COUNT_120",
                binder = "SemanticSourcePartCanonicalizer + SemanticSourcePartBinder",
            },
            providerExecution = new
            {
                baselineReasoningFingerprint = "derived per request; effort=none",
                treatmentReasoningFingerprint = "derived per request; effort=medium",
                requestSemanticHashesIdentical = allSameSemanticRequestHashes,
            },
            documents,
            calls = new { total = allCalls, SRC089 = 5, SRC095 = 20 },
            budget = new
            {
                inputTokensReferenceFromFrozenV4 = 712286,
                outputTokensObservedFromFrozenV4 = 46095,
                outputTokensPerRequestCeilingSum = maxOutputCeiling,
                hardCaps = new { calls = 30, input = 2000000, output = 250000 },
                treatmentOutputEstimate = "UNKNOWN_UNTIL_PROVIDER",
            },
            gates = new
            {
                goldOpened = false,
                hierarchyRun = false,
                postFilterApplied = false,
                cohortExpanded = false,
                providerCalls = 0,
                providerAuthorizationRequired = true,
                frozenV4ArtifactsUntouched = true,
            },
            nextGate = "Explicit authorization required before any V4R1 provider transport or raw prediction persistence.",
        });
    }

    [Fact]
    public void R1_requires_explicit_authorization_and_cannot_be_selected_by_normal_host_path()
    {
        using var artifact = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{Root}/r1-reasoning-preflight.v1.json")));
        Assert.Equal("V4R1_SEMANTIC_FUNCTION_EXPLICIT_REASONING", artifact.RootElement.GetProperty("requestVersion").GetString());
        Assert.True(artifact.RootElement.GetProperty("gates").GetProperty("providerAuthorizationRequired").GetBoolean());
        Assert.Equal(SemanticRequestVersion.V2_ATTENTION_FREE, SemanticRequestVersions.ProductionDefault);
    }

    [Fact]
    public async Task Run_the_authorized_r1_arm_and_persist_raw_predictions_before_gold()
    {
        if (Environment.GetEnvironmentVariable("A99_LLM_V4R1_RUN") != "1") return;

        var runPath = TestRepository.Path($"{Root}/r1-run.v1.json");
        Assert.False(File.Exists(runPath), "R1 raw predictions already persist; refusing a second provider run.");
        using var preflight = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{Root}/r1-reasoning-preflight.v1.json")));
        var preflightRoot = preflight.RootElement;
        Assert.Equal("V4R1_SEMANTIC_FUNCTION_EXPLICIT_REASONING", preflightRoot.GetProperty("requestVersion").GetString());
        Assert.Equal("medium", preflightRoot.GetProperty("treatment").GetProperty("treatmentValue").GetString());
        Assert.True(preflightRoot.GetProperty("gates").GetProperty("providerAuthorizationRequired").GetBoolean());

        var planned = new Dictionary<string, CapturedRequest[]>(StringComparer.Ordinal);
        foreach (var (id, pdf) in Documents)
        {
            using var capture = new RequestCapturingClassifier();
            await CanonicalSemanticPdfAuthorityAdapter.RunAsync(TestRepository.Path(pdf), capture, CancellationToken.None,
                experiment: V4, profile: PdfSemanticAuthorityProfile.StructuredSourceParts,
                packingPolicy: SemanticEvidencePackingPolicies.FixedOwnedCount120,
                sourceFacts: PdfSourceFactsVersion.V3_RobustGlyphStatistics, runPlacement: false);
            planned[id] = capture.Requests.ToArray();
        }
        Assert.Equal(25, planned.Values.Sum(requests => requests.Length));

        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        Assert.False(string.IsNullOrWhiteSpace(apiKey), "OPENROUTER_API_KEY is not set.");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var telemetryRoot = TestRepository.Path($"{Root}/r1-telemetry");
        var ledger = new List<R1LedgerEntry>();
        var successful = new HashSet<string>(StringComparer.Ordinal);
        var usedInput = 0L;
        var usedOutput = 0L;
        var recovered = RecoverR1Telemetry(planned, telemetryRoot);
        ledger.AddRange(recovered.Entries);
        foreach (var entry in recovered.Entries.Where(entry => entry.Error is null && entry.Response is not null))
            successful.Add(entry.RequestSha256);
        usedInput = recovered.InputTokens;
        usedOutput = recovered.OutputTokens;
        var attemptOrdinal = ledger.Count;
        string? stopped = null;

        foreach (var (id, requests) in planned)
        {
            var documentTelemetry = Path.Combine(telemetryRoot, id);
            Directory.CreateDirectory(documentTelemetry);
            using var provider = new OpenRouterHeaderExtractor(http, new RemoteInferenceOptions
            {
                ApiKey = apiKey,
                Model = Model,
                OpenRouterReasoningEffort = "medium",
                Observability = new ProviderObservabilityOptions
                {
                    RootDirectory = documentTelemetry,
                    CampaignId = "A99_SEMANTIC_CONTROL_CEILING_R1",
                    DocumentId = id,
                    Provider = "OpenRouter",
                    Model = Model,
                },
            });

            foreach (var request in requests)
            {
                var requestHash = Sha(request.UserMessage);
                var systemHash = Sha(request.SystemPrompt);
                var maxTokens = OpenRouterHeaderExtractor.BoundaryOutputBudgetFor(request.UserMessage, request.ExpectedItemCount, ProductionMaxOutputTokens);
                var inputEstimate = Encoding.UTF8.GetByteCount(request.SystemPrompt) + Encoding.UTF8.GetByteCount(request.UserMessage);
                while (!successful.Contains(requestHash))
                {
                    if (attemptOrdinal >= 30 || usedInput + inputEstimate > 2_000_000 || usedOutput + maxTokens > 250_000)
                    {
                        stopped = $"R1 cap before {id}:{requestHash}";
                        break;
                    }
                    attemptOrdinal++;
                    var started = DateTimeOffset.UtcNow;
                    string? response = null;
                    string? error = null;
                    var usage = (Prompt: (int?)null, Completion: (int?)null);
                    var rawBefore = Directory.Exists(documentTelemetry)
                        ? Directory.GetFiles(documentTelemetry, "response.raw.*.txt", SearchOption.AllDirectories).ToHashSet(StringComparer.OrdinalIgnoreCase)
                        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        response = await provider.BoundaryCutAsync(request.SystemPrompt, request.UserMessage, CancellationToken.None, request.ExpectedItemCount);
                        usage = LatestUsage(documentTelemetry, rawBefore);
                        successful.Add(requestHash);
                    }
                    catch (Exception exception)
                    {
                        error = $"{exception.GetType().Name}: {exception.Message}";
                        usage = LatestUsage(documentTelemetry, rawBefore);
                    }
                    var chargedInput = usage.Prompt ?? inputEstimate;
                    var chargedOutput = usage.Completion ?? maxTokens;
                    usedInput += chargedInput;
                    usedOutput += chargedOutput;
                    ledger.Add(new R1LedgerEntry(
                        attemptOrdinal, id, requestHash, systemHash, request.ExpectedItemCount, maxTokens,
                        usage.Prompt, usage.Completion, chargedInput, chargedOutput,
                        (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds, error, response));
                    if (error is not null && attemptOrdinal >= 30)
                    {
                        stopped = $"R1 provider attempts exhausted after {id}:{requestHash}";
                        break;
                    }
                }
                if (stopped is not null) break;
            }
            if (stopped is not null) break;
        }

        var successfulEntries = ledger.Where(entry => entry.Error is null && entry.Response is not null).ToArray();
        var expectedHashes = planned.SelectMany(pair => pair.Value.Select(request => Sha(request.UserMessage))).ToHashSet(StringComparer.Ordinal);
        var unexpected = successfulEntries.Count(entry => !expectedHashes.Contains(entry.RequestSha256));
        var duplicateSuccessful = successfulEntries.GroupBy(entry => entry.RequestSha256, StringComparer.Ordinal).Count(group => group.Count() > 1);
        var pending = expectedHashes.Count(hash => !successful.Contains(hash));
        var artifact = new
        {
            artifactKind = "a99_semantic_control_ceiling_r1_raw_run",
            study = "A99_SEMANTIC_CONTROL_CEILING_STUDY",
            requestVersion = "V4R1_SEMANTIC_FUNCTION_EXPLICIT_REASONING",
            preflightSha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path($"{Root}/r1-reasoning-preflight.v1.json")),
            pins = new { model = Model, reasoningEffort = "medium", promptSha256 = PromptSha256, schemaSha256 = SchemaSha256, factsVersion = "V3_RobustGlyphStatistics", packing = "FIXED_OWNED_COUNT_120", binder = "existing SemanticSourcePart binder", providerRoute = "OpenRouter automatic route" },
            goldRead = false,
            hierarchyRun = false,
            postFilterApplied = false,
            ontologyChanged = false,
            stopped,
            completion = new
            {
                status = pending == 0 && duplicateSuccessful == 0 && unexpected == 0 ? "COMPLETE_25_OF_25" : "INCOMPLETE",
                planned = expectedHashes.Count,
                successful = successful.Count,
                pending,
                duplicateSuccessful,
                unexpected,
                successfulByDocument = successfulEntries.GroupBy(entry => entry.DocumentId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Select(entry => entry.RequestSha256).Distinct(StringComparer.Ordinal).Count(), StringComparer.Ordinal),
            },
            totals = new { attempts = ledger.Count, inputTokens = usedInput, outputTokens = usedOutput, caps = new { attempts = 30, input = 2_000_000, output = 250_000 } },
            ledger,
        };
        File.WriteAllText(runPath, JsonSerializer.Serialize(artifact, FreezeArtifact.Json).ReplaceLineEndings("\n"));
        Assert.Null(stopped);
        Assert.Equal(25, successful.Count);
        Assert.Equal(0, pending);
        Assert.True(ledger.Count <= 30);
        Assert.True(usedInput <= 2_000_000);
        Assert.True(usedOutput <= 250_000);
    }

    private sealed record R1LedgerEntry(
        int OrdinalOverall,
        string DocumentId,
        string RequestSha256,
        string SystemPromptSha256,
        int ExpectedItemCount,
        int MaxTokens,
        int? PromptTokens,
        int? CompletionTokens,
        long ChargedInput,
        long ChargedOutput,
        long ElapsedMs,
        string? Error,
        string? Response);

    private static (IReadOnlyList<R1LedgerEntry> Entries, long InputTokens, long OutputTokens) RecoverR1Telemetry(
        IReadOnlyDictionary<string, CapturedRequest[]> planned, string telemetryRoot)
    {
        var entries = new List<R1LedgerEntry>();
        var nextRequestByDocument = planned.Keys.ToDictionary(key => key, _ => 0, StringComparer.Ordinal);
        if (!Directory.Exists(telemetryRoot)) return (entries, 0, 0);
        foreach (var file in Directory.GetFiles(telemetryRoot, "attempt.started.*.json", SearchOption.AllDirectories)
                     .OrderBy(path => File.GetLastWriteTimeUtc(path)))
        {
            using var started = JsonDocument.Parse(File.ReadAllText(file));
            var root = started.RootElement;
            var documentId = root.GetProperty("documentId").GetString()!;
            var requestIndex = nextRequestByDocument[documentId];
            Assert.True(requestIndex < planned[documentId].Length, $"Recovered telemetry exceeds planned request count for {documentId}.");
            var request = planned[documentId][requestIndex];
            nextRequestByDocument[documentId] = requestIndex + 1;
            var requestHash = Sha(request.UserMessage);
            var attemptId = root.GetProperty("attemptId").GetString()!;
            var rawPath = Path.Combine(Path.GetDirectoryName(file)!, $"response.raw.{attemptId}.txt");
            string? response = null;
            int? prompt = null;
            int? completion = null;
            string? error = null;
            if (File.Exists(rawPath))
            {
                using var raw = JsonDocument.Parse(File.ReadAllText(rawPath));
                var message = raw.RootElement.GetProperty("choices")[0].GetProperty("message");
                response = message.GetProperty("content").GetString();
                if (raw.RootElement.TryGetProperty("usage", out var usage))
                {
                    prompt = usage.TryGetProperty("prompt_tokens", out var p) ? p.GetInt32() : null;
                    completion = usage.TryGetProperty("completion_tokens", out var c) ? c.GetInt32() : null;
                }
            }
            else error = "INTERRUPTED_BEFORE_RESPONSE";
            var chargedInput = prompt ?? root.GetProperty("estimatedInputTokens").GetInt64();
            var chargedOutput = completion ?? root.GetProperty("maxOutputTokens").GetInt64();
            entries.Add(new R1LedgerEntry(
                root.GetProperty("callOrdinal").GetInt32(), documentId, requestHash, Sha(request.SystemPrompt),
                request.ExpectedItemCount, root.GetProperty("maxOutputTokens").GetInt32(), prompt, completion,
                chargedInput, chargedOutput, 0, error, response));
        }
        return (entries, entries.Sum(entry => entry.ChargedInput), entries.Sum(entry => entry.ChargedOutput));
    }

    private static (int? Prompt, int? Completion) LatestUsage(string telemetryRoot, IReadOnlySet<string> beforeFiles)
    {
        var files = Directory.Exists(telemetryRoot)
            ? Directory.GetFiles(telemetryRoot, "response.raw.*.txt", SearchOption.AllDirectories)
            : [];
        var file = files.Where(path => !beforeFiles.Contains(path)).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (file is null) return (null, null);
        try
        {
            using var raw = JsonDocument.Parse(File.ReadAllText(file));
            if (!raw.RootElement.TryGetProperty("usage", out var usage)) return (null, null);
            return (
                usage.TryGetProperty("prompt_tokens", out var prompt) ? prompt.GetInt32() : null,
                usage.TryGetProperty("completion_tokens", out var completion) ? completion.GetInt32() : null);
        }
        catch (JsonException) { return (null, null); }
    }

    private static string Sha(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
