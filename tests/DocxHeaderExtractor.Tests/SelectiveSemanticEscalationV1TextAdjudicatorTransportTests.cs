using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Real text-adjudicator execution for SELECTIVE_SEMANTIC_ESCALATION_V1, on exactly the 2 items
/// CONTEXT_TOPOLOGY_DISAGREEMENT_SIGNAL_V1 flagged (ITEM-505430BB, ITEM-CCE2C592) - one independent
/// adjudication call per item, not 3 repeats. VLM is not called anywhere in this file. Unreachable
/// unless <see cref="RunVariable"/> is set, so this file never spends a real call on an ordinary run.
/// <para>
/// The payload sent is exactly <see cref="SelectiveSemanticEscalationV1PreflightTests.BuildAdjudicationRequest"/>'s
/// already-leakage-checked <c>ModelFacingPayload</c> - not reconstructed here - so the preflight's
/// guarantees (neutral view ids, no Gold/model/arm-name leakage, byte-verified FULL_STRUCTURED_CONTEXT_V2
/// view) carry over unchanged into the real call.
/// </para>
/// </summary>
public sealed class SelectiveSemanticEscalationV1TextAdjudicatorTransportTests
{
    private const string RunVariable = "A99_SELECTIVE_SEMANTIC_ESCALATION_V1_ADJUDICATOR_RUN";
    private const string CaptureRoot = "eval/a99-closed-loop/selective-semantic-escalation-v1/DOC-0252/adjudication";
    private const string Model = "qwen/qwen3.7-flash";
    private const string ProviderBackend = "OpenRouter";
    private const string ResponseFormat = TransportCompatibility.JsonObjectResponseFormat;
    private const int MaxOutputTokens = 2048;
    private const int MaxCalls = 2;

    private const string SystemPrompt =
        "You are an impartial semantic-role adjudicator. You will see, for a single document item, " +
        "three independently rendered views of the same underlying source evidence (VIEW_1, VIEW_2, " +
        "VIEW_3), each already associated with the semantic-role label(s) it produced across one or " +
        "more attempts. The views may disagree with each other, or a single view may itself have been " +
        "unstable across attempts. Using only the evidence shown, choose exactly one final semantic-role " +
        "label for the item from the given allowed labels. Respond with a single JSON object of exactly " +
        "the shape given in outputSchema and nothing else - no explanation, no markdown, no extra fields.";

    private static readonly string[] AllowedLabels = ["STRUCTURAL_UNIT", "DOCUMENT_LABEL", "NON_STRUCTURAL"];

    [Fact]
    public void Cells_are_derived_deterministically_from_the_already_leakage_checked_preflight_payload()
    {
        var first = BuildCells();
        var second = BuildCells();
        Assert.Equal(2, first.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].UserMessage, second[i].UserMessage);
            Assert.Equal(first[i].ProviderInputHash, second[i].ProviderInputHash);
        }
        Assert.Equal(
            SelectiveSemanticEscalationV1PreflightTests.ItemPacks.Select(p => p.ItemId).OrderBy(x => x, StringComparer.Ordinal),
            first.Select(c => c.ItemId).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Fake_transport_reserves_before_send_and_never_resumes_an_existing_root()
    {
        using var temp = new TemporaryRoot();
        var cells = BuildCells();
        var calls = new List<string>();

        Task<string> Fake(Cell cell)
        {
            calls.Add(cell.ItemId);
            return Task.FromResult(JsonSerializer.Serialize(new { finalLabel = "STRUCTURAL_UNIT", resolved = true }));
        }

        var result = await RunAsync(temp.Path, cells, Fake);
        Assert.True(result.Succeeded, result.HaltedReason);
        Assert.Equal(2, result.Captures);
        Assert.Equal(cells.Select(c => c.ItemId), calls);

        foreach (var cell in cells)
        {
            Assert.True(File.Exists(Path.Combine(temp.Path, cell.ReservationRelativePath)));
            Assert.True(File.Exists(Path.Combine(temp.Path, cell.CaptureRelativePath)));
        }

        var rerun = await RunAsync(temp.Path, cells, Fake);
        Assert.False(rerun.Succeeded);
        Assert.Equal(0, rerun.Attempts);
    }

    [Fact]
    public async Task Fake_transport_halts_on_first_failure_without_retry_or_skip()
    {
        using var temp = new TemporaryRoot();
        var cells = BuildCells();
        var attempts = 0;

        Task<string> Failing(Cell cell)
        {
            attempts++;
            if (attempts == 1) throw new HttpRequestException("simulated HTTP 500");
            return Task.FromResult(JsonSerializer.Serialize(new { finalLabel = "STRUCTURAL_UNIT", resolved = true }));
        }

        var result = await RunAsync(temp.Path, cells, Failing);
        Assert.False(result.Succeeded);
        Assert.Equal(1, attempts);
        Assert.Equal(0, result.Captures);
    }

    [Fact]
    public async Task Fake_transport_rejects_an_invalid_final_label_without_marking_success()
    {
        using var temp = new TemporaryRoot();
        var cells = BuildCells();

        Task<string> BadLabel(Cell cell) =>
            Task.FromResult(JsonSerializer.Serialize(new { finalLabel = "NOT_A_REAL_LABEL", resolved = true }));

        var result = await RunAsync(temp.Path, cells, BadLabel);
        Assert.False(result.Succeeded);
        var capturePath = Path.Combine(temp.Path, cells[0].CaptureRelativePath);
        using var doc = JsonDocument.Parse(File.ReadAllText(capturePath));
        Assert.Equal("FAILED", doc.RootElement.GetProperty("parseStatus").GetString());
    }

    [Fact]
    public async Task Real_transport_is_unreachable_without_exact_authorization_flag()
    {
        if (Environment.GetEnvironmentVariable(RunVariable) is "1" or "true") return;

        var cells = BuildCells();
        Assert.Equal(MaxCalls, cells.Count);

        var root = TestRepository.Path(CaptureRoot);
        if (!Directory.Exists(root)) return; // PRE_EXECUTION

        // POST_EXECUTION_FROZEN: real evidence already committed - verify it is exactly the frozen
        // 2-capture set and was never silently extended, not that the root is absent.
        var captures = Directory.GetFiles(root, "*.transport-capture.v1.json", SearchOption.TopDirectoryOnly);
        Assert.Equal(MaxCalls, captures.Length);
        Assert.All(captures, path =>
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("PASS", doc.RootElement.GetProperty("contractStatus").GetString());
        });
    }

    [Fact]
    public async Task Run_real_text_adjudicator_only_when_explicitly_enabled()
    {
        if (Environment.GetEnvironmentVariable(RunVariable) is not ("1" or "true")) return;

        var cells = BuildCells();
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        Assert.False(string.IsNullOrWhiteSpace(key));

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var provider = new OpenRouterHeaderExtractor(http, new RemoteInferenceOptions
        {
            ApiKey = key,
            Model = Model,
        });

        var attempts = 0;
        async Task<string> Send(Cell cell)
        {
            attempts++;
            if (attempts > MaxCalls) throw new InvalidOperationException("2-call cap exceeded");
            return await provider.BoundaryCutAsync(SystemPrompt, cell.UserMessage, CancellationToken.None, 1);
        }

        var root = TestRepository.Path(CaptureRoot);
        var result = await RunAsync(root, cells, Send);
        Assert.True(result.Succeeded, result.HaltedReason);
        Assert.Equal(MaxCalls, attempts);
        Assert.Equal(MaxCalls, result.Captures);
    }

    // ---- cell construction --------------------------------------------------------------------

    private static IReadOnlyList<Cell> BuildCells() =>
        SelectiveSemanticEscalationV1PreflightTests.ItemPacks.Select(pair =>
        {
            var request = SelectiveSemanticEscalationV1PreflightTests.BuildAdjudicationRequest(pair.ItemId);
            var userMessage = JsonSerializer.Serialize(request.ModelFacingPayload);
            var compatibility = TransportCompatibility.Validate(SystemPrompt, userMessage, ResponseFormat);
            if (!compatibility.IsCompatible)
                throw new InvalidOperationException($"{compatibility.Reason}: {compatibility.Detail}");
            var maxTokens = OpenRouterHeaderExtractor.BoundaryOutputBudgetFor(userMessage, 1, MaxOutputTokens);
            var providerInputHash = SemanticAuthorityTransportCall.Sha256Utf8(
                JsonSerializer.Serialize(new { systemPrompt = SystemPrompt, userMessage }));
            return new Cell(pair.ItemId, userMessage, providerInputHash, maxTokens);
        }).OrderBy(c => c.ItemId, StringComparer.Ordinal).ToArray();

    // ---- minimal, self-contained runner (mirrors the established reserve-before-send,
    // persist-raw-before-parse, no-retry, no-resume protocol) -----------------------------------

    private static async Task<RunResult> RunAsync(
        string root, IReadOnlyList<Cell> cells, Func<Cell, Task<string>> transport)
    {
        if (Directory.Exists(root))
            return new RunResult(false, 0, 0, "execution root already exists; automatic resume is forbidden");

        Directory.CreateDirectory(root);
        var attempts = 0;
        var captured = 0;

        foreach (var cell in cells)
        {
            try
            {
                WriteExclusive(Path.Combine(root, cell.ReservationRelativePath), new
                {
                    schemaVersion = "a99-selective-semantic-escalation-adjudication-reservation-v1",
                    status = "CAPTURE_SLOT_RESERVED",
                    itemId = cell.ItemId,
                    providerInputHash = cell.ProviderInputHash,
                });
            }
            catch (Exception error)
            {
                return new RunResult(false, attempts, captured, "reservation collision: " + error.Message);
            }

            attempts++;
            string raw;
            try
            {
                raw = await transport(cell);
            }
            catch (Exception error)
            {
                WriteExclusive(Path.Combine(root, $"{cell.ItemId}.transport-failure.v1.json"), new
                {
                    schemaVersion = "a99-selective-semantic-escalation-adjudication-transport-failure-v1",
                    itemId = cell.ItemId,
                    transportStatus = "FAILED",
                    errorType = error.GetType().FullName,
                    message = error.Message,
                });
                return new RunResult(false, attempts, captured, "transport failure: " + error.Message);
            }

            var rawSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
            var capturePath = Path.Combine(root, cell.CaptureRelativePath);
            WriteExclusive(capturePath, new
            {
                schemaVersion = "a99-selective-semantic-escalation-adjudication-transport-capture-v1",
                itemId = cell.ItemId,
                model = Model,
                providerRoute = ProviderBackend,
                providerInputHash = cell.ProviderInputHash,
                responseFormat = ResponseFormat,
                maxTokens = cell.MaxTokens,
                transportCaptureStatus = "TRANSPORT_CAPTURE_COMPLETE",
                rawResponseCaptured = true,
                rawResponseSha256 = rawSha256,
                rawResponseBytes = Encoding.UTF8.GetByteCount(raw),
                rawResponseUtf8Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)),
                parseStatus = "PENDING",
                contractStatus = "PENDING",
                finalLabel = (string?)null,
                resolved = (bool?)null,
                fault = (string?)null,
            });

            try
            {
                var (finalLabel, resolved) = ValidateResponse(raw);
                UpdateCapture(capturePath, node =>
                {
                    node["parseStatus"] = "OK";
                    node["contractStatus"] = "PASS";
                    node["finalLabel"] = finalLabel;
                    node["resolved"] = resolved;
                });
                captured++;
            }
            catch (Exception error)
            {
                UpdateCapture(capturePath, node =>
                {
                    node["parseStatus"] = "FAILED";
                    node["contractStatus"] = "NOT_EVALUATED";
                    node["fault"] = error.Message;
                });
                return new RunResult(false, attempts, captured, "parse/contract failure: " + error.Message);
            }
        }

        return new RunResult(true, attempts, captured, null);
    }

    private static (string FinalLabel, bool Resolved) ValidateResponse(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        var finalLabel = root.TryGetProperty("finalLabel", out var labelProp) && labelProp.ValueKind == JsonValueKind.String
            ? labelProp.GetString() : null;
        if (finalLabel is null || !AllowedLabels.Contains(finalLabel, StringComparer.Ordinal))
            throw new InvalidDataException($"invalid finalLabel {finalLabel}");
        if (!root.TryGetProperty("resolved", out var resolvedProp) ||
            (resolvedProp.ValueKind != JsonValueKind.True && resolvedProp.ValueKind != JsonValueKind.False))
            throw new InvalidDataException("missing or non-boolean resolved");
        return (finalLabel, resolvedProp.GetBoolean());
    }

    private static void UpdateCapture(string path, Action<System.Text.Json.Nodes.JsonObject> mutate)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        mutate(node);
        File.WriteAllText(path, node.ToJsonString(FreezeArtifact.Json).ReplaceLineEndings("\n"));
    }

    private static void WriteExclusive(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(JsonSerializer.Serialize(value, FreezeArtifact.Json).ReplaceLineEndings("\n"));
        writer.Flush();
        stream.Flush(true);
    }

    private sealed record Cell(string ItemId, string UserMessage, string ProviderInputHash, int MaxTokens)
    {
        public string ReservationRelativePath => $"{ItemId}.capture-slot.v1.json";
        public string CaptureRelativePath => $"{ItemId}.transport-capture.v1.json";
    }

    private sealed record RunResult(bool Succeeded, int Attempts, int Captures, string? HaltedReason);

    private sealed class TemporaryRoot : IDisposable
    {
        public TemporaryRoot() =>
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "a99-selective-escalation-adjudicator-" + Guid.NewGuid().ToString("N"));

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
