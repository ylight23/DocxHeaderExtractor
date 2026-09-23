using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Dedicated, fail-closed transport for the approved nine-cell second-model replay.
/// It is unreachable unless the exact experiment flag is enabled. It pins OpenRouter's upstream
/// order to OpenAI and disables fallbacks; it never retries, replaces, or resumes a cell.
/// </summary>
public sealed class SecondModelMinimalReplayTransportTests
{
    private const string RunVariable = "A99_SECOND_MODEL_MINIMAL_REPLAY_RUN";
    private const string PreflightRoot =
        "eval/a99-closed-loop/second-model-minimal-replay-preflight-v1";
    private const string PreflightFile = "second-model-minimal-replay-preflight.v1.json";
    private const string QwenCaptureRoot =
        "eval/a99-closed-loop/direct-semantic-context-ablation-v1/DOC-0252";
    private const string CaptureRoot =
        "eval/a99-closed-loop/direct-semantic-second-model-minimal-replay-v1/DOC-0252";
    private const string RequiredBaseCommit =
        "43132059ad84a9cf914ae3c83e48ec0cab631465";
    private const string Model = "openai/gpt-4.1";
    private const string ProviderBackend = "OpenRouter";
    private const string UpstreamProvider = "OpenAI";
    private const string TransportProtocol = "openai-chat-completions-v1";
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const string ResponseFormat = TransportCompatibility.JsonObjectResponseFormat;
    private const int MaxOutputTokens = 32768;
    private const int MaxCalls = 9;

    private static readonly string[] Packs = ["PACK_001", "PACK_005", "PACK_006"];

    [Fact]
    public void Runner_is_disabled_without_exact_authorization_flag()
    {
        if (Environment.GetEnvironmentVariable(RunVariable) is "1" or "true") return;

        var authority = LoadAuthority();
        Assert.Equal(MaxCalls, authority.Count);
        Assert.All(authority, cell =>
            Assert.False(File.Exists(TestRepository.Path(cell.ReservationPath))));
    }

    [Fact]
    public async Task Run_real_second_model_minimal_replay_only_when_explicitly_enabled()
    {
        if (Environment.GetEnvironmentVariable(RunVariable) is not ("1" or "true"))
            return;

        var authority = LoadAuthority();
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        Assert.False(string.IsNullOrWhiteSpace(key));

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        var attempts = 0;
        var completed = 0;
        var started = DateTimeOffset.UtcNow;

        foreach (var cell in authority)
        {
            attempts++;
            if (attempts > MaxCalls)
                throw new InvalidOperationException("9-call cap exceeded");

            Reserve(cell);
            string? rawResponse = null;
            try
            {
                var payload = BuildPayload(cell);
                using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                request.Headers.TryAddWithoutValidation("X-Title", "DocxHeaderExtractor");

                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                rawResponse = await response.Content.ReadAsStringAsync();
                var headers = ResponseHeaders(response);

                if (!response.IsSuccessStatusCode)
                {
                    WriteCapture(cell, payload, rawResponse, null, headers,
                        "HTTP_ERROR", $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                    throw new InvalidOperationException(
                        $"second-model replay stopped at {cell.Identity}: HTTP {(int)response.StatusCode}");
                }

                var content = ExtractContent(rawResponse);
                ValidateResponse(content, cell.ItemIds);
                WriteCapture(cell, payload, rawResponse, content, headers, "PASS", null);
                completed++;
            }
            catch (Exception error) when (error is not InvalidOperationException ||
                                          !error.Message.StartsWith("second-model replay stopped", StringComparison.Ordinal))
            {
                WriteCapture(cell, BuildPayload(cell), rawResponse, null,
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    rawResponse is null ? "TRANSPORT_ERROR" : "CONTRACT_ERROR", error.Message);
                throw new InvalidOperationException(
                    $"second-model replay stopped at {cell.Identity}: {error.Message}", error);
            }
        }

        Assert.Equal(MaxCalls, attempts);
        Assert.Equal(MaxCalls, completed);
        WriteRunManifest(started, attempts, completed, null);
    }

    private static IReadOnlyList<CellAuthority> LoadAuthority()
    {
        var head = Git("rev-parse HEAD");
        Assert.True(IsAncestor(RequiredBaseCommit, head),
            $"{RequiredBaseCommit} is not an ancestor of {head}");

        using var preflight = JsonDocument.Parse(File.ReadAllText(
            TestRepository.Path(Path.Combine(PreflightRoot, PreflightFile))));
        var root = preflight.RootElement;
        Assert.Equal("SECOND_MODEL_MINIMAL_REPLAY_AUTHORIZATION_READY",
            root.GetProperty("status").GetString());
        Assert.Equal(Model, root.GetProperty("secondModelId").GetString());
        Assert.Equal(ProviderBackend, root.GetProperty("transport")
            .GetProperty("providerBackend").GetString());
        Assert.Equal(TransportProtocol, root.GetProperty("transport")
            .GetProperty("transportProtocol").GetString());
        Assert.False(root.GetProperty("transport")
            .GetProperty("providerFallbacksAllowed").GetBoolean());
        var providerOptions = root.GetProperty("transport").GetProperty("providerOptions");
        Assert.False(providerOptions.GetProperty("allow_fallbacks").GetBoolean());
        Assert.Equal(UpstreamProvider, providerOptions.GetProperty("order")[0].GetString());
        Assert.False(root.GetProperty("providerAuthorized").GetBoolean());
        Assert.Equal(0, root.GetProperty("modelCalls").GetInt32());
        Assert.Equal(0, root.GetProperty("providerCalls").GetInt32());

        var contextHashes = root.GetProperty("contextHashes");
        var qwenHashes = root.GetProperty("qwenProviderInputHashes");
        var secondHashes = root.GetProperty("secondModelProviderInputHashes");
        var expectedCounts = root.GetProperty("expectedItemCounts");
        var maxTokens = root.GetProperty("maxTokens");
        var cells = new List<CellAuthority>();

        foreach (var repeat in Enumerable.Range(1, 3))
        foreach (var pack in Packs)
        {
            var qwenCapturePath = TestRepository.Path(Path.Combine(
                QwenCaptureRoot, "minimal-structural-context-v1", $"r{repeat}",
                $"{pack}.transport-capture.v1.json"));
            using var capture = JsonDocument.Parse(File.ReadAllText(qwenCapturePath));
            var qwen = capture.RootElement;
            var systemPrompt = Decode(qwen, "systemPromptUtf8Base64");
            var userMessage = Decode(qwen, "userMessageUtf8Base64");
            var expectedCount = expectedCounts.GetProperty(pack).GetInt32();
            var budget = maxTokens.GetProperty(pack).GetInt32();
            Assert.Equal(budget, OpenRouterHeaderExtractor.BoundaryOutputBudgetFor(
                userMessage, expectedCount, MaxOutputTokens));
            Assert.True(TransportCompatibility.Validate(systemPrompt, userMessage, ResponseFormat)
                .IsCompatible);

            var secondHash = TransportIdentityHash(systemPrompt, userMessage, budget);
            Assert.Equal(secondHashes.GetProperty(pack).GetString(), secondHash);
            Assert.Equal(qwen.GetProperty("contextHash").GetString(),
                contextHashes.GetProperty(pack).GetString());
            Assert.Equal(qwen.GetProperty("providerInputHash").GetString(),
                qwenHashes.GetProperty(pack).GetString());
            Assert.Equal("qwen/qwen3.7-flash", qwen.GetProperty("model").GetString());

            var identity = $"r{repeat}/{pack}";
            var reservationPath = $"{CaptureRoot}/r{repeat}/{pack}.capture-slot.v1.json";
            var capturePath = $"{CaptureRoot}/r{repeat}/{pack}.transport-capture.v1.json";
            Assert.False(File.Exists(TestRepository.Path(reservationPath)));
            Assert.False(File.Exists(TestRepository.Path(capturePath)));
            cells.Add(new CellAuthority(
                identity, repeat, pack, systemPrompt, userMessage, expectedCount, budget,
                qwen.GetProperty("itemIds").EnumerateArray().Select(item => item.GetString()!).ToArray(),
                secondHash, reservationPath, capturePath));
        }

        Assert.Equal(MaxCalls, cells.Count);
        Assert.Equal(MaxCalls, cells.Select(cell => cell.Identity).Distinct().Count());
        Assert.True(ProbeAtomicReservation(MaxCalls));
        return cells;
    }

    private static string BuildPayload(CellAuthority cell) =>
        JsonSerializer.Serialize(new
        {
            model = Model,
            temperature = 0,
            max_tokens = cell.MaxTokens,
            reasoning = new { effort = "none" },
            messages = new[]
            {
                new { role = "system", content = cell.SystemPrompt },
                new { role = "user", content = cell.UserMessage },
            },
            response_format = new { type = ResponseFormat },
            provider = new
            {
                order = new[] { UpstreamProvider },
                zdr = false,
                data_collection = "deny",
                require_parameters = true,
                allow_fallbacks = false,
            },
        });

    private static string TransportIdentityHash(
        string systemPrompt, string userMessage, int maxTokens) =>
        SemanticAuthorityTransportCall.Sha256Utf8(JsonSerializer.Serialize(new
        {
            model = Model,
            providerBackend = ProviderBackend,
            transportProtocol = TransportProtocol,
            endpoint = Endpoint,
            temperature = 0,
            max_tokens = maxTokens,
            reasoning = new { effort = "none" },
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage },
            },
            response_format = new { type = ResponseFormat },
            provider = new
            {
                order = new[] { UpstreamProvider },
                zdr = false,
                data_collection = "deny",
                require_parameters = true,
                allow_fallbacks = false,
            },
        }));

    private static void Reserve(CellAuthority cell)
    {
        var path = TestRepository.Path(cell.ReservationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteExclusive(path, new
        {
            schemaVersion = "a99-second-model-minimal-replay-reservation-v1",
            status = "CAPTURE_SLOT_RESERVED",
            cell = cell.Identity,
            model = Model,
            provider = ProviderBackend,
            upstream = UpstreamProvider,
            providerFallbacksAllowed = false,
            providerInputHash = cell.ProviderInputHash,
        });
    }

    private static void WriteCapture(
        CellAuthority cell,
        string payload,
        string? rawResponse,
        string? modelContent,
        IReadOnlyDictionary<string, string> headers,
        string status,
        string? fault)
    {
        var path = TestRepository.Path(cell.CapturePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteExclusive(path, new
        {
            schemaVersion = "a99-second-model-minimal-replay-transport-capture-v1",
            cell = cell.Identity,
            repeat = cell.Repeat,
            pack = cell.Pack,
            model = Model,
            provider = ProviderBackend,
            upstream = UpstreamProvider,
            transportProtocol = TransportProtocol,
            endpoint = Endpoint,
            providerFallbacksAllowed = false,
            systemPromptSha256 = SemanticAuthorityTransportCall.Sha256Utf8(cell.SystemPrompt),
            userMessageSha256 = SemanticAuthorityTransportCall.Sha256Utf8(cell.UserMessage),
            providerInputSha256 = cell.ProviderInputHash,
            requestPayloadSha256 = SemanticAuthorityTransportCall.Sha256Utf8(payload),
            requestPayloadUtf8Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)),
            responseFormat = ResponseFormat,
            expectedItemCount = cell.ExpectedItemCount,
            maxTokens = cell.MaxTokens,
            itemIds = cell.ItemIds,
            transportCaptureStatus = "TRANSPORT_CAPTURE_COMPLETE",
            rawResponseCaptured = rawResponse is not null,
            rawResponseSha256 = rawResponse is null ? null : SemanticAuthorityTransportCall.Sha256Utf8(rawResponse),
            rawResponseBytes = rawResponse is null ? 0 : Encoding.UTF8.GetByteCount(rawResponse),
            rawResponseUtf8Base64 = rawResponse is null ? null : Convert.ToBase64String(Encoding.UTF8.GetBytes(rawResponse)),
            modelContentSha256 = modelContent is null ? null : SemanticAuthorityTransportCall.Sha256Utf8(modelContent),
            modelContentUtf8Base64 = modelContent is null ? null : Convert.ToBase64String(Encoding.UTF8.GetBytes(modelContent)),
            responseHeaders = headers,
            parseStatus = modelContent is null ? "NOT_EVALUATED" : "OK",
            contractStatus = status == "PASS" ? "PASS" : "NOT_EVALUATED",
            status,
            fault,
        });
    }

    private static string ExtractContent(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        if (document.RootElement.TryGetProperty("model", out var model) &&
            model.ValueKind == JsonValueKind.String && model.GetString() != Model)
            throw new InvalidDataException($"unexpected response model {model.GetString()}");
        var choices = document.RootElement.GetProperty("choices");
        var content = choices[0].GetProperty("message").GetProperty("content");
        if (content.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("provider content is not a JSON string");
        return content.GetString()!;
    }

    private static void ValidateResponse(string raw, IReadOnlyList<string> itemIds)
    {
        using var document = JsonDocument.Parse(raw);
        var decisions = document.RootElement.GetProperty("decisions");
        var allowed = itemIds.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var labels = new[] { "STRUCTURAL_UNIT", "DOCUMENT_LABEL", "NON_STRUCTURAL" };
        foreach (var decision in decisions.EnumerateArray())
        {
            var itemId = decision.GetProperty("itemId").GetString();
            var classification = decision.GetProperty("classification").GetString();
            if (itemId is null || !allowed.Contains(itemId))
                throw new InvalidDataException($"unknown itemId {itemId}");
            if (classification is null || !labels.Contains(classification, StringComparer.Ordinal))
                throw new InvalidDataException($"invalid classification {classification}");
            if (!seen.Add(itemId)) throw new InvalidDataException($"duplicate itemId {itemId}");
        }

        var missing = allowed.Except(seen, StringComparer.Ordinal).ToArray();
        if (missing.Length > 0) throw new InvalidDataException($"missing {missing.Length} item(s)");
    }

    private static IReadOnlyDictionary<string, string> ResponseHeaders(HttpResponseMessage response)
    {
        var headers = response.Headers.Concat(response.Content.Headers)
            .ToDictionary(pair => pair.Key,
                pair => string.Join(",", pair.Value), StringComparer.OrdinalIgnoreCase);
        return headers;
    }

    private static void WriteRunManifest(
        DateTimeOffset started, int attempts, int completed, string? halted)
    {
        var path = TestRepository.Path(Path.Combine(CaptureRoot, "second-model-minimal-replay-run.v1.json"));
        WriteExclusive(path, new
        {
            schemaVersion = "a99-second-model-minimal-replay-run-v1",
            model = Model,
            provider = ProviderBackend,
            upstream = UpstreamProvider,
            providerFallbacksAllowed = false,
            arm = "MINIMAL_STRUCTURAL_CONTEXT_V1",
            proposedCalls = MaxCalls,
            attempts,
            completed,
            halted,
            modelCalls = completed,
            providerCalls = completed,
            startedUtc = started,
            completedUtc = DateTimeOffset.UtcNow,
        });
    }

    private static void WriteExclusive(string path, object value)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(JsonSerializer.Serialize(value, FreezeArtifact.Json).ReplaceLineEndings("\n"));
        writer.Flush();
        stream.Flush(true);
    }

    private static string Decode(JsonElement root, string property) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(root.GetProperty(property).GetString()!));

    private static bool ProbeAtomicReservation(int count)
    {
        var root = Path.Combine(Path.GetTempPath(),
            "a99-second-model-minimal-transport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            for (var index = 0; index < count; index++)
            {
                using var stream = new FileStream(
                    Path.Combine(root, $"slot-{index:000}.reserve"),
                    FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Flush(true);
            }

            return Directory.GetFiles(root).Length == count;
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static bool IsAncestor(string candidate, string descendant) =>
        Git($"merge-base --is-ancestor {candidate} {descendant}", allowFailure: true).Length == 0;

    private static string Git(string arguments, bool allowFailure = false)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = TestRepository.Root(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        var error = process.StandardError.ReadToEnd().Trim();
        process.WaitForExit();
        if (!allowFailure && process.ExitCode != 0)
            throw new InvalidOperationException($"git {arguments} failed: {error}");
        return process.ExitCode == 0 ? output : "not-ancestor";
    }

    private sealed record CellAuthority(
        string Identity,
        int Repeat,
        string Pack,
        string SystemPrompt,
        string UserMessage,
        int ExpectedItemCount,
        int MaxTokens,
        string[] ItemIds,
        string ProviderInputHash,
        string ReservationPath,
        string CapturePath);
}
