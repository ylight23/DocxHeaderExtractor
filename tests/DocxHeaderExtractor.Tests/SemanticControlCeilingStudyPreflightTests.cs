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

    [Fact]
    public async Task Continue_the_authorized_r1_arm_only_for_pending_src095_hashes()
    {
        if (Environment.GetEnvironmentVariable("A99_LLM_V4R1_CONTINUATION_RUN") != "1") return;

        var priorPath = TestRepository.Path($"{Root}/r1-run.v1.json");
        var continuationPath = TestRepository.Path($"{Root}/r1-continuation.v1.json");
        var combinedPath = TestRepository.Path($"{Root}/r1-combined-manifest.v1.json");
        Assert.True(File.Exists(priorPath), "The immutable partial R1 run must exist before continuation.");
        Assert.False(File.Exists(continuationPath), "R1 continuation already exists; refusing a second continuation provider run.");
        Assert.False(File.Exists(combinedPath), "R1 combined manifest already exists; refusing a second continuation provider run.");

        using var prior = JsonDocument.Parse(File.ReadAllText(priorPath));
        var priorRoot = prior.RootElement;
        Assert.Equal("V4R1_SEMANTIC_FUNCTION_EXPLICIT_REASONING", priorRoot.GetProperty("requestVersion").GetString());
        Assert.Equal(Model, priorRoot.GetProperty("pins").GetProperty("model").GetString());
        Assert.Equal("medium", priorRoot.GetProperty("pins").GetProperty("reasoningEffort").GetString());
        Assert.Equal(PromptSha256, priorRoot.GetProperty("pins").GetProperty("promptSha256").GetString());
        Assert.Equal(SchemaSha256, priorRoot.GetProperty("pins").GetProperty("schemaSha256").GetString());
        Assert.Equal("V3_RobustGlyphStatistics", priorRoot.GetProperty("pins").GetProperty("factsVersion").GetString());
        Assert.Equal("FIXED_OWNED_COUNT_120", priorRoot.GetProperty("pins").GetProperty("packing").GetString());
        Assert.False(priorRoot.GetProperty("goldRead").GetBoolean());
        Assert.False(priorRoot.GetProperty("hierarchyRun").GetBoolean());
        Assert.False(priorRoot.GetProperty("postFilterApplied").GetBoolean());

        var priorSuccessful = priorRoot.GetProperty("ledger").EnumerateArray()
            .Where(entry => entry.GetProperty("Error").ValueKind == JsonValueKind.Null && entry.GetProperty("Response").ValueKind != JsonValueKind.Null)
            .Select(entry => new
            {
                DocumentId = entry.GetProperty("DocumentId").GetString()!,
                RequestSha256 = entry.GetProperty("RequestSha256").GetString()!,
            })
            .ToArray();
        var priorSuccessfulHashes = priorSuccessful.Select(entry => entry.RequestSha256).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(7, priorSuccessfulHashes.Count);
        Assert.Equal(5, priorSuccessful.Count(entry => entry.DocumentId == "SRC-089"));
        Assert.Equal(2, priorSuccessful.Count(entry => entry.DocumentId == "SRC-095"));

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
        var plannedByHash = planned.SelectMany(pair => pair.Value.Select(request => (DocumentId: pair.Key, Request: request, Hash: Sha(request.UserMessage))))
            .ToDictionary(item => item.Hash, item => item, StringComparer.Ordinal);
        Assert.Equal(25, plannedByHash.Count);
        Assert.All(priorSuccessfulHashes, hash => Assert.True(plannedByHash.ContainsKey(hash), $"Successful prior hash is not in the frozen plan: {hash}"));
        var pending = planned["SRC-095"].Where(request => !priorSuccessfulHashes.Contains(Sha(request.UserMessage))).ToArray();
        Assert.Equal(18, pending.Length);
        Assert.All(pending, request => Assert.DoesNotContain(Sha(request.UserMessage), priorSuccessfulHashes));
        Assert.Equal(0, planned["SRC-089"].Count(request => !priorSuccessfulHashes.Contains(Sha(request.UserMessage))));

        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        Assert.False(string.IsNullOrWhiteSpace(apiKey), "OPENROUTER_API_KEY is not set.");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var telemetryRoot = TestRepository.Path($"{Root}/r1-continuation-telemetry");
        var documentTelemetry = Path.Combine(telemetryRoot, "SRC-095");
        Directory.CreateDirectory(documentTelemetry);
        using var provider = new OpenRouterHeaderExtractor(http, new RemoteInferenceOptions
        {
            ApiKey = apiKey,
            Model = Model,
            RequestTimeoutSeconds = 300,
            OpenRouterReasoningEffort = "medium",
            Observability = new ProviderObservabilityOptions
            {
                RootDirectory = documentTelemetry,
                CampaignId = "A99_SEMANTIC_CONTROL_CEILING_R1_CONTINUATION",
                DocumentId = "SRC-095",
                Provider = "OpenRouter",
                Model = Model,
            },
        });

        var ledger = new List<R1LedgerEntry>();
        var successful = new HashSet<string>(priorSuccessfulHashes, StringComparer.Ordinal);
        var usedInput = 0L;
        var usedOutput = 0L;
        var attemptOrdinal = 0;
        string? stopped = null;
        foreach (var request in pending)
        {
            var requestHash = Sha(request.UserMessage);
            var systemHash = Sha(request.SystemPrompt);
            var maxTokens = OpenRouterHeaderExtractor.BoundaryOutputBudgetFor(request.UserMessage, request.ExpectedItemCount, ProductionMaxOutputTokens);
            var inputEstimate = Encoding.UTF8.GetByteCount(request.SystemPrompt) + Encoding.UTF8.GetByteCount(request.UserMessage);
            while (!successful.Contains(requestHash))
            {
                if (attemptOrdinal >= 30 || usedInput + inputEstimate > 2_000_000 || usedOutput + maxTokens > 500_000)
                {
                    stopped = $"R1 continuation cap before SRC-095:{requestHash}";
                    break;
                }

                attemptOrdinal++;
                var started = DateTimeOffset.UtcNow;
                string? response = null;
                string? error = null;
                var usage = (Prompt: (int?)null, Completion: (int?)null);
                var rawBefore = Directory.GetFiles(documentTelemetry, "response.raw.*.txt", SearchOption.AllDirectories)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
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
                    attemptOrdinal, "SRC-095", requestHash, systemHash, request.ExpectedItemCount, maxTokens,
                    usage.Prompt, usage.Completion, chargedInput, chargedOutput,
                    (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds, error, response));
            }
            if (stopped is not null) break;
        }

        var successfulContinuation = ledger.Where(entry => entry.Error is null && entry.Response is not null).ToArray();
        var continuationSuccessfulHashes = successfulContinuation.Select(entry => entry.RequestSha256).ToHashSet(StringComparer.Ordinal);
        var allSuccessful = new HashSet<string>(priorSuccessfulHashes, StringComparer.Ordinal);
        allSuccessful.UnionWith(continuationSuccessfulHashes);
        var expectedHashes = plannedByHash.Keys.ToHashSet(StringComparer.Ordinal);
        var unexpected = continuationSuccessfulHashes.Count(hash => !expectedHashes.Contains(hash));
        var duplicateSuccessful = successfulContinuation.GroupBy(entry => entry.RequestSha256, StringComparer.Ordinal).Count(group => group.Count() > 1);
        var pendingCount = expectedHashes.Count(hash => !allSuccessful.Contains(hash));
        var complete = pendingCount == 0 && duplicateSuccessful == 0 && unexpected == 0;
        var priorAttempts = priorRoot.GetProperty("totals").GetProperty("attempts").GetInt32();
        var priorInput = priorRoot.GetProperty("totals").GetProperty("inputTokens").GetInt64();
        var priorOutput = priorRoot.GetProperty("totals").GetProperty("outputTokens").GetInt64();
        var continuationArtifact = new
        {
            artifactKind = "a99_semantic_control_ceiling_r1_continuation",
            study = "A99_SEMANTIC_CONTROL_CEILING_STUDY",
            requestVersion = "V4R1_SEMANTIC_FUNCTION_EXPLICIT_REASONING",
            priorRunSha256 = CanonicalArtifactHash.OfTextFile(priorPath),
            pins = new { model = Model, reasoningEffort = "medium", promptSha256 = PromptSha256, schemaSha256 = SchemaSha256, factsVersion = "V3_RobustGlyphStatistics", packing = "FIXED_OWNED_COUNT_120", binder = "existing SemanticSourcePart binder", providerRoute = "OpenRouter automatic route" },
            executionChange = new { kind = "execution-reliability-only", priorClientTimeoutSeconds = 90, clientTimeoutSeconds = 300, semanticTreatmentUnchanged = true, requestBytesUnchanged = true },
            carriedForward = new { successfulExisting = priorSuccessfulHashes.Count, pendingInitial = pending.Length, attempts = priorAttempts, inputTokens = priorInput, outputTokens = priorOutput },
            goldRead = false,
            hierarchyRun = false,
            postFilterApplied = false,
            ontologyChanged = false,
            stopped,
            completion = new
            {
                status = complete ? "COMPLETE_25_OF_25" : "INCOMPLETE",
                planned = expectedHashes.Count,
                successful = allSuccessful.Count,
                successfulExisting = priorSuccessfulHashes.Count,
                successfulContinuation = continuationSuccessfulHashes.Count,
                pending = pendingCount,
                duplicateSuccessful,
                unexpected,
                successfulByDocument = plannedByHash.Values.GroupBy(item => item.DocumentId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(item => allSuccessful.Contains(item.Hash)), StringComparer.Ordinal),
            },
            incrementalTotals = new { attempts = ledger.Count, inputTokens = usedInput, outputTokens = usedOutput, caps = new { attempts = 30, input = 2_000_000, output = 500_000 } },
            cumulativeTotals = new { attempts = priorAttempts + ledger.Count, inputTokens = priorInput + usedInput, outputTokens = priorOutput + usedOutput },
            ledger,
        };
        File.WriteAllText(continuationPath, JsonSerializer.Serialize(continuationArtifact, FreezeArtifact.Json).ReplaceLineEndings("\n"));

        if (complete)
        {
            var combinedArtifact = new
            {
                artifactKind = "a99_semantic_control_ceiling_r1_combined_manifest",
                study = "A99_SEMANTIC_CONTROL_CEILING_STUDY",
                requestVersion = "V4R1_SEMANTIC_FUNCTION_EXPLICIT_REASONING",
                lineage = new
                {
                    preflightSha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path($"{Root}/r1-reasoning-preflight.v1.json")),
                    attempt1Sha256 = CanonicalArtifactHash.OfTextFile(priorPath),
                    continuationSha256 = CanonicalArtifactHash.OfTextFile(continuationPath),
                },
                pins = new { model = Model, reasoningEffort = "medium", promptSha256 = PromptSha256, schemaSha256 = SchemaSha256, factsVersion = "V3_RobustGlyphStatistics", packing = "FIXED_OWNED_COUNT_120", binder = "existing SemanticSourcePart binder" },
                completion = new { status = "COMPLETE_25_OF_25", successfulPlannedHashes = allSuccessful.Count, pending = 0, duplicateSuccessful = 0, unexpected = 0, SRC089 = 5, SRC095 = 20 },
                totals = new { attempts = priorAttempts + ledger.Count, inputTokens = priorInput + usedInput, outputTokens = priorOutput + usedOutput, cumulativeCaps = new { attempts = 49, input = 3_187_381, output = 739_017 } },
                goldRead = false,
                hierarchyRun = false,
                postFilterApplied = false,
            };
            File.WriteAllText(combinedPath, JsonSerializer.Serialize(combinedArtifact, FreezeArtifact.Json).ReplaceLineEndings("\n"));
        }

        Assert.Null(stopped);
        Assert.True(complete);
        Assert.Equal(25, allSuccessful.Count);
        Assert.Equal(0, pendingCount);
        Assert.True(ledger.Count <= 30);
        Assert.True(usedInput <= 2_000_000);
        Assert.True(usedOutput <= 500_000);
    }

    [Fact]
    public async Task Finish_the_authorized_r1_arm_for_the_last_pending_hash()
    {
        if (Environment.GetEnvironmentVariable("A99_LLM_V4R1_FINAL_RUN") != "1") return;

        var priorPath = TestRepository.Path($"{Root}/r1-continuation.v1.json");
        var attempt1Path = TestRepository.Path($"{Root}/r1-run.v1.json");
        var finalPath = TestRepository.Path($"{Root}/r1-final-continuation.v1.json");
        var combinedPath = TestRepository.Path($"{Root}/r1-combined-manifest.v1.json");
        Assert.True(File.Exists(attempt1Path));
        Assert.True(File.Exists(priorPath));
        Assert.False(File.Exists(finalPath), "R1 final continuation already exists; refusing a second final provider run.");
        Assert.False(File.Exists(combinedPath), "R1 combined manifest already exists; refusing a second final provider run.");

        using var attempt1 = JsonDocument.Parse(File.ReadAllText(attempt1Path));
        using var prior = JsonDocument.Parse(File.ReadAllText(priorPath));
        Assert.Equal("V4R1_SEMANTIC_FUNCTION_EXPLICIT_REASONING", prior.RootElement.GetProperty("requestVersion").GetString());
        Assert.Equal("medium", prior.RootElement.GetProperty("pins").GetProperty("reasoningEffort").GetString());
        Assert.Equal(PromptSha256, prior.RootElement.GetProperty("pins").GetProperty("promptSha256").GetString());
        Assert.Equal(SchemaSha256, prior.RootElement.GetProperty("pins").GetProperty("schemaSha256").GetString());
        Assert.False(prior.RootElement.GetProperty("goldRead").GetBoolean());

        var successful = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in new[] { attempt1Path, priorPath })
        {
            using var artifact = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var entry in artifact.RootElement.GetProperty("ledger").EnumerateArray())
            {
                if (entry.GetProperty("Error").ValueKind == JsonValueKind.Null && entry.GetProperty("Response").ValueKind != JsonValueKind.Null)
                    successful.Add(entry.GetProperty("RequestSha256").GetString()!);
            }
        }
        Assert.Equal(24, successful.Count);

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
        var plannedByHash = planned.SelectMany(pair => pair.Value.Select(request => (DocumentId: pair.Key, Request: request, Hash: Sha(request.UserMessage))))
            .ToDictionary(item => item.Hash, item => item, StringComparer.Ordinal);
        var pending = plannedByHash.Values.Where(item => !successful.Contains(item.Hash)).ToArray();
        Assert.Single(pending);
        Assert.Equal("SRC-095", pending[0].DocumentId);

        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        Assert.False(string.IsNullOrWhiteSpace(apiKey), "OPENROUTER_API_KEY is not set.");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var telemetryRoot = TestRepository.Path($"{Root}/r1-final-telemetry");
        var documentTelemetry = Path.Combine(telemetryRoot, "SRC-095");
        Directory.CreateDirectory(documentTelemetry);
        using var provider = new OpenRouterHeaderExtractor(http, new RemoteInferenceOptions
        {
            ApiKey = apiKey,
            Model = Model,
            RequestTimeoutSeconds = 300,
            OpenRouterReasoningEffort = "medium",
            Observability = new ProviderObservabilityOptions
            {
                RootDirectory = documentTelemetry,
                CampaignId = "A99_SEMANTIC_CONTROL_CEILING_R1_FINAL",
                DocumentId = "SRC-095",
                Provider = "OpenRouter",
                Model = Model,
            },
        });

        var request = pending[0].Request;
        var requestHash = pending[0].Hash;
        var systemHash = Sha(request.SystemPrompt);
        var maxTokens = OpenRouterHeaderExtractor.BoundaryOutputBudgetFor(request.UserMessage, request.ExpectedItemCount, ProductionMaxOutputTokens);
        var inputEstimate = Encoding.UTF8.GetByteCount(request.SystemPrompt) + Encoding.UTF8.GetByteCount(request.UserMessage);
        var ledger = new List<R1LedgerEntry>();
        var usedInput = 0L;
        var usedOutput = 0L;
        string? stopped = null;
        for (var attemptOrdinal = 1; attemptOrdinal <= 10 && !successful.Contains(requestHash); attemptOrdinal++)
        {
            if (usedInput + inputEstimate > 2_000_000 || usedOutput + maxTokens > 500_000)
            {
                stopped = $"R1 final continuation cap before SRC-095:{requestHash}";
                break;
            }
            var started = DateTimeOffset.UtcNow;
            string? response = null;
            string? error = null;
            var usage = (Prompt: (int?)null, Completion: (int?)null);
            var rawBefore = Directory.GetFiles(documentTelemetry, "response.raw.*.txt", SearchOption.AllDirectories).ToHashSet(StringComparer.OrdinalIgnoreCase);
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
                attemptOrdinal, "SRC-095", requestHash, systemHash, request.ExpectedItemCount, maxTokens,
                usage.Prompt, usage.Completion, chargedInput, chargedOutput,
                (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds, error, response));
        }

        var complete = successful.Count == 25;
        var priorAttempts = prior.RootElement.GetProperty("cumulativeTotals").GetProperty("attempts").GetInt32();
        var priorInput = prior.RootElement.GetProperty("cumulativeTotals").GetProperty("inputTokens").GetInt64();
        var priorOutput = prior.RootElement.GetProperty("cumulativeTotals").GetProperty("outputTokens").GetInt64();
        var finalArtifact = new
        {
            artifactKind = "a99_semantic_control_ceiling_r1_final_continuation",
            study = "A99_SEMANTIC_CONTROL_CEILING_STUDY",
            requestVersion = "V4R1_SEMANTIC_FUNCTION_EXPLICIT_REASONING",
            priorContinuationSha256 = CanonicalArtifactHash.OfTextFile(priorPath),
            pins = new { model = Model, reasoningEffort = "medium", promptSha256 = PromptSha256, schemaSha256 = SchemaSha256, factsVersion = "V3_RobustGlyphStatistics", packing = "FIXED_OWNED_COUNT_120", binder = "existing SemanticSourcePart binder", providerRoute = "OpenRouter automatic route" },
            executionChange = new { kind = "execution-reliability-only", clientTimeoutSeconds = 300, semanticTreatmentUnchanged = true, requestBytesUnchanged = true },
            carriedForward = new { successfulExisting = 24, pendingInitial = 1, attempts = priorAttempts, inputTokens = priorInput, outputTokens = priorOutput },
            goldRead = false,
            hierarchyRun = false,
            postFilterApplied = false,
            ontologyChanged = false,
            stopped,
            completion = new { status = complete ? "COMPLETE_25_OF_25" : "INCOMPLETE", planned = 25, successful = successful.Count, pending = complete ? 0 : 1, duplicateSuccessful = 0, unexpected = 0, SRC089 = 5, SRC095 = successful.Count - 5 },
            incrementalTotals = new { attempts = ledger.Count, inputTokens = usedInput, outputTokens = usedOutput, caps = new { attempts = 10, input = 2_000_000, output = 500_000 } },
            cumulativeTotals = new { attempts = priorAttempts + ledger.Count, inputTokens = priorInput + usedInput, outputTokens = priorOutput + usedOutput },
            ledger,
        };
        File.WriteAllText(finalPath, JsonSerializer.Serialize(finalArtifact, FreezeArtifact.Json).ReplaceLineEndings("\n"));

        if (complete)
        {
            var combinedArtifact = new
            {
                artifactKind = "a99_semantic_control_ceiling_r1_combined_manifest",
                study = "A99_SEMANTIC_CONTROL_CEILING_STUDY",
                requestVersion = "V4R1_SEMANTIC_FUNCTION_EXPLICIT_REASONING",
                lineage = new
                {
                    preflightSha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path($"{Root}/r1-reasoning-preflight.v1.json")),
                    attempt1Sha256 = CanonicalArtifactHash.OfTextFile(attempt1Path),
                    continuationSha256 = CanonicalArtifactHash.OfTextFile(priorPath),
                    finalContinuationSha256 = CanonicalArtifactHash.OfTextFile(finalPath),
                },
                pins = new { model = Model, reasoningEffort = "medium", promptSha256 = PromptSha256, schemaSha256 = SchemaSha256, factsVersion = "V3_RobustGlyphStatistics", packing = "FIXED_OWNED_COUNT_120", binder = "existing SemanticSourcePart binder" },
                completion = new { status = "COMPLETE_25_OF_25", successfulPlannedHashes = 25, pending = 0, duplicateSuccessful = 0, unexpected = 0, SRC089 = 5, SRC095 = 20 },
                totals = new { attempts = priorAttempts + ledger.Count, inputTokens = priorInput + usedInput, outputTokens = priorOutput + usedOutput },
                goldRead = false,
                hierarchyRun = false,
                postFilterApplied = false,
            };
            File.WriteAllText(combinedPath, JsonSerializer.Serialize(combinedArtifact, FreezeArtifact.Json).ReplaceLineEndings("\n"));
        }

        Assert.Null(stopped);
        Assert.True(complete);
        Assert.Equal(25, successful.Count);
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
