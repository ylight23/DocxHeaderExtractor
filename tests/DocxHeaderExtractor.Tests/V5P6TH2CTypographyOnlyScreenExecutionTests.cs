using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Infrastructure.AI;
using DocxHeaderExtractor.V5Qualification;

namespace DocxHeaderExtractor.Tests;

/// <summary>Executes exactly the four frozen typography-only diagnostic requests when explicitly authorized.</summary>
public sealed class V5P6TH2CTypographyOnlyScreenExecutionTests
{
    private const string Root = "artifacts/v5-p6t-function-membership";
    private const string PreflightRoot = Root + "/p6th2c-typography-only-screen-preflight";
    private const string CaptureRoot = Root + "/p6th2c-typography-only-screen-capture-20261005";
    private const string ConfirmationVariable = "P6TH2C_TYPOGRAPHY_ONLY_SCREEN_EXECUTION_CONFIRMATION";
    private const string Confirmation = "yes-i-authorize-four-typography-only-primary-calls-no-retry";

    private static readonly (string DocumentId, string Anchor)[] Targets =
    [
        ("SRC-089", "O17"),
        ("SRC-089", "O19"),
        ("SRC-041", "O4"),
        ("DOC-0256", "O1"),
    ];

    [Fact]
    public async Task Execute_exactly_four_frozen_typography_only_primary_calls_when_explicitly_authorized()
    {
        if (Environment.GetEnvironmentVariable(ConfirmationVariable) != Confirmation) return;

        var repo = TestRepository.Root();
        var preflightPath = TestRepository.Path(PreflightRoot + "/typography-only-screen-preflight.v1.json");
        var manifestPath = TestRepository.Path(PreflightRoot + "/execution-manifest.v1.json");
        var capturePath = TestRepository.Path(CaptureRoot);
        Assert.False(Directory.Exists(capturePath), "h2c-typography-only-capture-directory-exists-stop-before-network");
        var preflightBytes = File.ReadAllBytes(preflightPath);
        var manifestBytes = File.ReadAllBytes(manifestPath);
        using var preflight = JsonDocument.Parse(preflightBytes);
        using var manifest = JsonDocument.Parse(manifestBytes);
        Assert.Equal("POST_HOC_DIAGNOSTIC_PREFLIGHT_FROZEN_PROVIDER_NOT_AUTHORIZED_GOLD_CLOSED",
            preflight.RootElement.GetProperty("status").GetString());
        Assert.Equal("PREPARED_NOT_AUTHORIZED_PROVIDER_CALLS_ZERO_GOLD_CLOSED", manifest.RootElement.GetProperty("status").GetString());
        Assert.Equal(Hash(preflightBytes), manifest.RootElement.GetProperty("preflightSha256").GetString());
        Assert.Equal(4, manifest.RootElement.GetProperty("primaryCalls").GetInt32());
        Assert.Equal(0, manifest.RootElement.GetProperty("retry").GetInt32());
        Assert.False(manifest.RootElement.GetProperty("repair").GetBoolean());
        Assert.False(manifest.RootElement.GetProperty("fallback").GetBoolean());
        Assert.False(manifest.RootElement.GetProperty("goldRead").GetBoolean());
        Assert.False(manifest.RootElement.GetProperty("runtimeChanged").GetBoolean());

        var planRows = manifest.RootElement.GetProperty("requests").EnumerateArray().ToArray();
        Assert.Equal(4, planRows.Length);
        Assert.Equal(Targets, planRows.Select(row =>
        {
            var item = row.GetProperty("row");
            return (item.GetProperty("documentId").GetString()!, item.GetProperty("anchor").GetString()!);
        }));
        Assert.True(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("A99_FREEZE_UPDATE")),
            "h2c-typography-only-execution-refuses-freeze-update-mode");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")))
            throw new InvalidOperationException("h2c-typography-only-openrouter-api-key-missing-provider-calls-zero");

        var all = P6TH2CEndPointerCanary.BuildAllForTreatment(repo, "V2");
        var calls = new List<V5P6TH2CEvidenceCompletePreflightTests.EvidenceArmRequest>(4);
        for (var index = 0; index < Targets.Length; index++)
        {
            var (documentId, anchor) = Targets[index];
            var source = all.Single(value => value.Source.DocumentId == documentId && value.Anchor == anchor);
            var request = V5P6TH2CEvidenceCompletePreflightTests.BuildTypographyOnlyRequest(repo, source);
            var row = planRows[index].GetProperty("row");
            var treatment = row.GetProperty("typographyOnlyTreatment");
            Assert.Equal(index + 1, planRows[index].GetProperty("callOrdinal").GetInt32());
            Assert.Equal("TYPOGRAPHY_ONLY", planRows[index].GetProperty("arm").GetString());
            Assert.Equal(request.DocumentId, row.GetProperty("documentId").GetString());
            Assert.Equal(request.PackId, row.GetProperty("packId").GetString());
            Assert.Equal(request.Anchor, row.GetProperty("anchor").GetString());
            Assert.Equal(request.UserMessageSha256, treatment.GetProperty("userMessageSha256").GetString());
            Assert.Equal(Hash(request.SystemPrompt), treatment.GetProperty("systemPromptSha256").GetString());
            Assert.Equal(request.ProviderBodySha256, treatment.GetProperty("providerBodySha256").GetString());
            Assert.Equal(request.ProviderBodyBytes, treatment.GetProperty("providerBodyBytes").GetInt32());
            Assert.Equal(request.SourceSha256, row.GetProperty("sourceSha256").GetString());
            Assert.Equal(request.SourceUniverseSha256, row.GetProperty("sourceUniverseSha256").GetString());
            calls.Add(request);
        }

        var options = RemoteInferenceOptions.FromEnvironment();
        options.Model = "qwen/qwen3.7-flash";
        options.OpenRouterProviderRoute = "alibaba";
        options.OpenRouterReasoningEffort = "none";
        options.RequireJsonObjectResponse = true;
        options.TransientRequestRetries = 0;
        options.MaxParallelRequests = 1;
        options.ProviderTransportTimeoutSeconds = 300;
        options.Validate();

        Directory.CreateDirectory(capturePath);
        WriteNew(Path.Combine(capturePath, "execution-reservation.v1.json"), new
        {
            schemaVersion = "v5-p6th2c-typography-only-screen-execution-reservation-v1",
            status = "AUTHORIZED_FOUR_PRIMARY_CALLS_RESERVED_RAW_CAPTURE_IN_PROGRESS_GOLD_CLOSED",
            preflightCommit = "148a3de",
            preflightSha256 = Hash(preflightBytes),
            executionManifestSha256 = Hash(manifestBytes),
            authorizedPrimaryCalls = 4,
            providerCallsAlreadySent = 0,
            retry = 0,
            repair = false,
            fallback = false,
            goldRead = false,
            runtimeChanged = false,
            reentryPolicy = "ANY_EXISTING_CAPTURE_DIRECTORY_STOPS_BEFORE_NETWORK",
        });

        var rows = new List<object>(4);
        for (var index = 0; index < calls.Count; index++)
        {
            var request = calls[index];
            OpenRouterExecutionObservation? observation = null;
            string? transportError = null;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var client = OpenRouterHeaderExtractor.CreateOwned(options);
                observation = await client.ExecuteObservedAsync(request.ProviderBody, request.MaxCompletionTokens,
                    request.SystemPrompt, request.UserMessage);
            }
            catch (Exception exception) { transportError = exception.Message; }
            stopwatch.Stop();

            var contractError = observation is null ? "NO_RESPONSE" :
                observation.RetryCount != 0 ? "UNEXPECTED_INTERNAL_RETRY" :
                observation.FinishReason != "stop" ? "FINISH_REASON_NOT_STOP" :
                !V5P6TH2CEvidenceCompletePreflightTests.TryParseEndPointer(observation.Content, request.OccurrenceHandles, request.Anchor)
                    ? "INVALID_END_POINTER_LEDGER" : null;
            var rawPath = Path.Combine(capturePath, $"{request.DocumentId}_{request.Anchor}.raw-capture.v1.json");
            WriteNew(rawPath, new
            {
                schemaVersion = "v5-p6th2c-typography-only-screen-raw-capture-v1",
                providerCallOrdinal = index + 1,
                request.DocumentId,
                request.PackId,
                request.Anchor,
                request.SourceSha256,
                request.SourceUniverseSha256,
                systemPromptSha256 = Hash(request.SystemPrompt),
                userMessageSha256 = request.UserMessageSha256,
                providerBodySha256 = request.ProviderBodySha256,
                providerBodyBytes = request.ProviderBodyBytes,
                occurrenceHandlesSha256 = Hash(string.Join("\n", request.OccurrenceHandles) + "\n"),
                occurrenceCount = request.OccurrenceHandles.Count,
                reasoningRequested = true,
                promptTokens = Usage(observation?.Usage, "prompt_tokens"),
                completionTokens = Usage(observation?.Usage, "completion_tokens"),
                reasoningTokens = Usage(observation?.Usage, "completion_tokens_details", "reasoning_tokens"),
                finishReason = observation?.FinishReason,
                retryCount = observation?.RetryCount ?? 0,
                latencyMs = stopwatch.Elapsed.TotalMilliseconds,
                rawSseSha256 = observation is null ? null : Hash(observation.RawSse),
                rawResponseSha256 = observation is null ? null : Hash(observation.Content),
                contractStatus = contractError is null ? "VALID" : contractError,
                contractError,
                transportError,
                rawSse = observation?.RawSse,
                rawResponse = observation?.Content,
                goldReadDuringCapture = false,
            });
            rows.Add(new
            {
                callOrdinal = index + 1,
                request.DocumentId,
                request.PackId,
                request.Anchor,
                finishReason = observation?.FinishReason,
                retryCount = observation?.RetryCount ?? 0,
                contractStatus = contractError is null ? "VALID" : contractError,
                rawSseSha256 = observation is null ? null : Hash(observation.RawSse),
                rawResponseSha256 = observation is null ? null : Hash(observation.Content),
                transportError,
            });
            File.WriteAllText(Path.Combine(capturePath, "progress.v1.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = "v5-p6th2c-typography-only-screen-progress-v1",
                callsAttempted = index + 1,
                maximumCalls = 4,
                goldRead = false,
                rows,
            }, FreezeArtifact.Json), new UTF8Encoding(false));
            Console.WriteLine($"[{index + 1}/4] {request.DocumentId}/{request.Anchor}: {observation?.FinishReason ?? "NO_RESPONSE"}; {contractError ?? "contract-valid"}");
        }

        var complete = rows.Count == 4 && rows.All(row => JsonSerializer.SerializeToElement(row).GetProperty("contractStatus").GetString() == "VALID");
        WriteNew(Path.Combine(capturePath, "result.v1.json"), new
        {
            schemaVersion = "v5-p6th2c-typography-only-screen-result-v1",
            status = complete ? "COMPLETE_RAW_CAPTURE_ALL_FOUR_CONTRACT_VALID_GOLD_CLOSED" : "INCOMPLETE_RAW_CAPTURE",
            providerCallsAttempted = rows.Count,
            authorizedPrimaryCalls = 4,
            retry = 0,
            repair = false,
            fallback = false,
            goldRead = false,
            runtimeChanged = false,
            rows,
        });
    }

    private static int? Usage(JsonElement? usage, params string[] path)
    {
        if (usage is not { ValueKind: JsonValueKind.Object } current) return null;
        foreach (var key in path)
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(key, out current)) return null;
        return current.ValueKind == JsonValueKind.Number && current.TryGetInt32(out var value) ? value : null;
    }

    private static void WriteNew(string path, object value)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(JsonSerializer.Serialize(value, FreezeArtifact.Json));
        writer.WriteLine();
    }

    private static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));
    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
}
