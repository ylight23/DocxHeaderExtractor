using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Infrastructure.AI;
using DocxHeaderExtractor.V5Qualification;

namespace DocxHeaderExtractor.Tests;

/// <summary>One exact same-body retry for the frozen Arm B DOC-0252/O66 upstream 429.</summary>
public sealed class V5P6TH2CEvidenceRetryTests
{
    private const string Root = "artifacts/v5-p6t-function-membership";
    private const string CaptureRoot = Root + "/p6th2c-evidence-complete-capture-20261005";
    private const string PreflightRoot = Root + "/p6th2c-evidence-complete-preflight";
    private const string RetryRoot = Root + "/p6th2c-evidence-complete-retry-20261005";
    private const string ConfirmationVariable = "P6TH2C_EVIDENCE_SINGLE_RETRY_CONFIRMATION";
    private const string Confirmation = "yes-i-authorize-exact-arm-b-doc0252-o66-retry-once";

    [Fact]
    public async Task Retry_only_frozen_arm_b_doc0252_o66_once_when_explicitly_authorized()
    {
        if (Environment.GetEnvironmentVariable(ConfirmationVariable) != Confirmation) return;
        var repo = TestRepository.Root();
        var capture = TestRepository.Path(CaptureRoot);
        var manifestPath = TestRepository.Path(PreflightRoot + "/execution-manifest.v1.json");
        var preflightPath = TestRepository.Path(PreflightRoot + "/h2c-evidence-complete-preflight.v1.json");
        var resultPath = Path.Combine(capture, "result.v1.json");
        var freezePath = Path.Combine(capture, "capture-freeze.v1.json");
        var primaryRawPath = Path.Combine(capture, "arm-b", "DOC-0252_O66.raw-capture.v1.json");
        Assert.True(File.Exists(resultPath) && File.Exists(freezePath) && File.Exists(primaryRawPath));
        Assert.False(Directory.Exists(TestRepository.Path(RetryRoot)), "h2c-evidence-retry-already-reserved-stop");

        using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        using var preflight = JsonDocument.Parse(File.ReadAllBytes(preflightPath));
        using var execution = JsonDocument.Parse(File.ReadAllBytes(resultPath));
        using var primaryRaw = JsonDocument.Parse(File.ReadAllBytes(primaryRawPath));
        using var freeze = JsonDocument.Parse(File.ReadAllBytes(freezePath));
        Assert.Equal("ALL_PRIMARY_ATTEMPTS_RAW_FROZEN_GOLD_NOT_READ", execution.RootElement.GetProperty("status").GetString());
        Assert.Equal(62, execution.RootElement.GetProperty("providerCallsAttempted").GetInt32());
        Assert.False(execution.RootElement.GetProperty("goldRead").GetBoolean());
        Assert.Equal("RAW_HASH_VERIFIED_61_RESPONSES_1_UPSTREAM_429_GOLD_STILL_CLOSED", freeze.RootElement.GetProperty("status").GetString());
        Assert.Equal(Hash(File.ReadAllBytes(manifestPath)), freeze.RootElement.GetProperty("executionManifestSha256").GetString());
        var primary = primaryRaw.RootElement;
        Assert.Equal("B", primary.GetProperty("arm").GetString());
        Assert.Equal("DOC-0252", primary.GetProperty("DocumentId").GetString());
        Assert.Equal("O66", primary.GetProperty("Anchor").GetString());
        Assert.Equal("NO_RESPONSE", primary.GetProperty("contractStatus").GetString());
        Assert.Contains("429", primary.GetProperty("transportError").GetString(), StringComparison.Ordinal);
        Assert.Null(primary.GetProperty("rawResponse").GetString());

        var plan = manifest.RootElement.GetProperty("requests").EnumerateArray().Single(row =>
            row.GetProperty("Arm").GetString() == "B" && row.GetProperty("DocumentId").GetString() == "DOC-0252" &&
            row.GetProperty("Anchor").GetString() == "O66");
        var request = P6TH2CEndPointerCanary.BuildAllForTreatment(repo, "V2").Single(value =>
            value.Source.DocumentId == "DOC-0252" && value.Anchor == "O66");
        var evidenceRequest = V5P6TH2CEvidenceCompletePreflightTests.BuildArmBRequest(repo, request);
        Assert.Equal(plan.GetProperty("providerBodySha256").GetString(), evidenceRequest.ProviderBodySha256);
        Assert.Equal(plan.GetProperty("userMessageSha256").GetString(), evidenceRequest.UserMessageSha256);
        Assert.Equal(plan.GetProperty("systemPromptSha256").GetString(), Hash(evidenceRequest.SystemPrompt));
        Assert.Equal(plan.GetProperty("SourceSha256").GetString(), evidenceRequest.SourceSha256);
        Assert.Equal(plan.GetProperty("SourceUniverseSha256").GetString(), evidenceRequest.SourceUniverseSha256);
        Assert.Equal(primary.GetProperty("providerBodySha256").GetString(), evidenceRequest.ProviderBodySha256);
        Assert.Equal(primary.GetProperty("userMessageSha256").GetString(), evidenceRequest.UserMessageSha256);

        var options = RemoteInferenceOptions.FromEnvironment();
        options.Model = "qwen/qwen3.7-flash";
        options.OpenRouterProviderRoute = "alibaba";
        options.OpenRouterReasoningEffort = "none";
        options.RequireJsonObjectResponse = true;
        options.TransientRequestRetries = 0;
        options.ProviderTransportTimeoutSeconds = 300;
        options.Validate();

        var retryDirectory = TestRepository.Path(RetryRoot);
        Directory.CreateDirectory(retryDirectory);
        WriteNew(Path.Combine(retryDirectory, "execution-reservation.v1.json"), new
        {
            schemaVersion = "v5-p6th2c-evidence-single-retry-reservation-v1",
            status = "ONE_EXACT_RETRY_RESERVED_GOLD_CLOSED",
            originalCaptureFreezeSha256 = Hash(File.ReadAllBytes(freezePath)),
            originalRawFileSha256 = Hash(File.ReadAllBytes(primaryRawPath)),
            executionManifestSha256 = Hash(File.ReadAllBytes(manifestPath)),
            preflightSha256 = Hash(File.ReadAllBytes(preflightPath)),
            providerCallsAlreadySent = 0,
            maximumAuthorizedRetryCalls = 1,
            retry = new { arm = "B", documentId = "DOC-0252", anchor = "O66", providerBodySha256 = evidenceRequest.ProviderBodySha256,
                userMessageSha256 = evidenceRequest.UserMessageSha256, systemPromptSha256 = Hash(evidenceRequest.SystemPrompt) },
            transportRetries = 0,
            repair = false,
            fallback = false,
            goldRead = false,
        });

        OpenRouterExecutionObservation? observation = null;
        string? transportError = null;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var client = OpenRouterQualificationTransport.CreateOwned(options);
            observation = await client.ExecuteObservedAsync(evidenceRequest.ProviderBody,
                evidenceRequest.MaxCompletionTokens, evidenceRequest.SystemPrompt, evidenceRequest.UserMessage);
        }
        catch (Exception exception) { transportError = exception.Message; }
        stopwatch.Stop();

        var contractValid = observation is not null && transportError is null && observation.FinishReason == "stop" &&
            V5P6TH2CEvidenceCompletePreflightTests.TryParseEndPointer(observation.Content,
                evidenceRequest.OccurrenceHandles, evidenceRequest.Anchor);
        var rawPath = Path.Combine(retryDirectory, "ARM_B_DOC-0252_O66.retry1.raw-capture.v1.json");
        WriteNew(rawPath, new
        {
            schemaVersion = "v5-p6th2c-evidence-single-retry-raw-capture-v1",
            arm = "B",
            documentId = evidenceRequest.DocumentId,
            packId = evidenceRequest.PackId,
            anchor = evidenceRequest.Anchor,
            providerCallOrdinal = 63,
            attempt = 1,
            originalPrimaryRawFileSha256 = Hash(File.ReadAllBytes(primaryRawPath)),
            originalPrimaryTransportError = primary.GetProperty("transportError").GetString(),
            providerBodySha256 = evidenceRequest.ProviderBodySha256,
            userMessageSha256 = evidenceRequest.UserMessageSha256,
            systemPromptSha256 = Hash(evidenceRequest.SystemPrompt),
            sourceSha256 = evidenceRequest.SourceSha256,
            sourceUniverseSha256 = evidenceRequest.SourceUniverseSha256,
            reasoningRequested = true,
            reasoningTokens = Usage(observation?.Usage, "completion_tokens_details", "reasoning_tokens"),
            promptTokens = Usage(observation?.Usage, "prompt_tokens"),
            completionTokens = Usage(observation?.Usage, "completion_tokens"),
            finishReason = observation?.FinishReason,
            transportRetryCount = observation?.RetryCount ?? 0,
            latencyMs = stopwatch.Elapsed.TotalMilliseconds,
            contractStatus = contractValid ? "VALID" : observation is null ? "NO_RESPONSE" : "INVALID_OR_INCOMPLETE_RESPONSE",
            rawSseSha256 = observation is null ? null : Hash(observation.RawSse),
            rawResponseSha256 = observation is null ? null : Hash(observation.Content),
            rawSse = observation?.RawSse,
            rawResponse = observation?.Content,
            transportError,
            goldReadDuringCapture = false,
        });
        var rawBytes = File.ReadAllBytes(rawPath);
        WriteNew(Path.Combine(retryDirectory, "result.v1.json"), new
        {
            schemaVersion = "v5-p6th2c-evidence-single-retry-result-v1",
            status = contractValid ? "RETRY_RESPONSE_CONTRACT_VALID" : observation is null ? "RETRY_NO_RESPONSE" : "RETRY_RESPONSE_NOT_CONTRACT_VALID",
            primaryCalls = 62,
            retryCalls = 1,
            totalCalls = 63,
            rawCaptureSha256 = Hash(rawBytes),
            retryResponseSha256 = observation is null ? null : Hash(observation.Content),
            retrySseSha256 = observation is null ? null : Hash(observation.RawSse),
            finishReason = observation?.FinishReason,
            contractValid,
            transportRetryCount = observation?.RetryCount ?? 0,
            repair = false,
            fallback = false,
            goldRead = false,
            runtimeChanged = false,
        });
        Console.WriteLine($"P6T-H2C exact Arm B DOC-0252/O66 retry captured once: {observation?.FinishReason ?? "NO_RESPONSE"}; contract={(contractValid ? "VALID" : "NOT_VALID")}; hash={Hash(rawBytes)}");
    }

    private static int? Usage(JsonElement? usage, params string[] path)
    {
        if (usage is not { ValueKind: JsonValueKind.Object } current) return null;
        foreach (var key in path)
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(key, out current)) return null;
        return current.ValueKind == JsonValueKind.Number && current.TryGetInt32(out var value) ? value : null;
    }

    private static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));
    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));

    private static void WriteNew(string path, object value)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(JsonSerializer.Serialize(value, FreezeArtifact.Json));
        writer.WriteLine();
    }
}
