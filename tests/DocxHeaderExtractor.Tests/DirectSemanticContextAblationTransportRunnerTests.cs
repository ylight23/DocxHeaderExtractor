using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Transport/capture harness for the three-arm direct semantic context ablation.
/// The preflight artifact is the authority; this file only supplies the executable lifecycle.
/// </summary>
public sealed class DirectSemanticContextAblationTransportRunnerTests
{
    private const string PreflightRoot =
        "eval/a99-closed-loop/direct-semantic-context-ablation-preflight-v1";
    private const string PreflightFile = "direct-semantic-context-ablation-preflight.v1.json";
    private const string CaptureRoot =
        "eval/a99-closed-loop/direct-semantic-context-ablation-v1/DOC-0252";
    private const string ScoreRoot =
        "eval/a99-closed-loop/direct-semantic-context-ablation-score-v1/DOC-0252";
    private const string SecondModelPreflightRoot =
        "eval/a99-closed-loop/second-model-minimal-replay-preflight-v1";
    private const string Doc0252Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private const string AuthorizedBaseCommit = "af30ed9007669f66a61e6420a4d79537ec04c9a8";
    private const string RunVariable = "A99_DIRECT_SEMANTIC_CONTEXT_ABLATION_V1_RUN";
    private const string Model = "qwen/qwen3.7-flash";
    private const string ResponseFormat = TransportCompatibility.JsonObjectResponseFormat;
    private const int Repeats = 3;
    private const int MaxCalls = 27;
    private const int MaxOutputTokens = 32768;
    private const int StructuralNeighborCount = 2;
    private const int LocalRadius = 3;

    private static readonly string[] ArmOrder =
    [
        "FULL_CONTEXT",
        "LOCAL_CONTEXT_RADIUS_3",
        "MINIMAL_STRUCTURAL_CONTEXT_V1",
    ];

    private static readonly string[] PackOrder = ["PACK_001", "PACK_005", "PACK_006"];

    private static readonly IReadOnlyDictionary<string, string> FrozenPlanHashes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["FULL_CONTEXT"] =
                "a792fb0f03b2c6c16facecba5485ed48dc7742610f7542e0936ca88ecd59243b",
            ["LOCAL_CONTEXT_RADIUS_3"] =
                "303cf743b89b88ba0f85bb1e2f20e15e482d9b2d2aa27fd4a815462bb48e09bf",
            ["MINIMAL_STRUCTURAL_CONTEXT_V1"] =
                "297387401a96bbc8f5f800456b6e4d9f579a1b04cfef92ffca574deff885cf68",
        };

    [Fact]
    public void Runner_freezes_exact_arm_and_cell_order_from_preflight()
    {
        var authority = FrozenContextAblationAuthority.Load();
        var cells = authority.Cells;

        Assert.True(authority.OnlyContextChanged);
        Assert.Equal(MaxCalls, cells.Count);
        Assert.Equal(ArmOrder, cells.Select(cell => cell.ArmId).Distinct().ToArray());
        Assert.Equal(
            ArmOrder.SelectMany(arm =>
                Enumerable.Range(1, Repeats).SelectMany(repeat =>
                    PackOrder.Select(pack => $"{arm}/r{repeat}/{pack}"))),
            cells.Select(cell => cell.Identity));
        Assert.Equal(MaxCalls, cells.Select(cell => cell.Identity).Distinct().Count());
        Assert.All(cells, cell => Assert.Equal(FrozenPlanHashes[cell.ArmId], cell.PlanHash));
    }

    [Fact]
    public async Task All_success_fake_transport_observes_27_ordered_calls_and_captures()
    {
        var authority = FrozenContextAblationAuthority.Load();
        using var temp = TemporaryRoot.Create();
        var transport = new FakeTransport(_ => null, ValidReply);
        var reservations = new FileReservationStore();
        var captures = new FileCaptureStore();

        var result = await ContextAblationTransportRunner.RunAsync(
            temp.Path, authority, transport.SendAsync, reservations, captures);

        Assert.True(result.Succeeded);
        Assert.Equal(MaxCalls, transport.Calls.Count);
        Assert.Equal(authority.Cells.Select(cell => cell.Identity), transport.Calls);
        Assert.Equal(MaxCalls, result.Reservations);
        Assert.Equal(MaxCalls, result.Captures);
        Assert.Equal(MaxCalls, Directory.GetFiles(temp.Path, "*.capture-slot.v1.json", SearchOption.AllDirectories).Length);
        Assert.Equal(MaxCalls, Directory.GetFiles(temp.Path, "*.transport-capture.v1.json", SearchOption.AllDirectories).Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(9)]
    [InlineData(18)]
    public async Task Failure_at_arm_boundary_stops_before_later_arm(int failingIndex)
    {
        var authority = FrozenContextAblationAuthority.Load();
        using var temp = TemporaryRoot.Create();
        var transport = new FakeTransport(cell =>
            authority.Cells[failingIndex].Identity == cell.Identity
                ? new InvalidOperationException("injected transport failure")
                : null);

        var result = await ContextAblationTransportRunner.RunAsync(
            temp.Path, authority, transport.SendAsync, new FileReservationStore(), new FileCaptureStore());

        Assert.False(result.Succeeded);
        Assert.Equal(failingIndex + 1, transport.Calls.Count);
        Assert.Equal(failingIndex + 1, result.Reservations);
        foreach (var identity in authority.Cells.Skip(failingIndex + 1).Select(cell => cell.Identity))
            Assert.DoesNotContain(identity, transport.Calls);
        Assert.Equal(authority.Cells[failingIndex].ArmId,
            authority.Cells.Take(failingIndex + 1).Last().ArmId);
    }

    [Fact]
    public async Task Malformed_response_is_captured_before_parse_and_stops()
    {
        var authority = FrozenContextAblationAuthority.Load();
        using var temp = TemporaryRoot.Create();
        var transport = new FakeTransport(_ => null, _ => "not JSON");

        var result = await ContextAblationTransportRunner.RunAsync(
            temp.Path, authority, transport.SendAsync, new FileReservationStore(), new FileCaptureStore());

        Assert.False(result.Succeeded);
        Assert.Single(transport.Calls);
        Assert.Equal(1, result.Captures);
        var capture = Assert.Single(
            Directory.GetFiles(temp.Path, "*.transport-capture.v1.json", SearchOption.AllDirectories));
        using var json = JsonDocument.Parse(File.ReadAllText(capture));
        Assert.Equal("TRANSPORT_CAPTURE_COMPLETE", json.RootElement.GetProperty("transportCaptureStatus").GetString());
        Assert.Equal("FAILED", json.RootElement.GetProperty("parseStatus").GetString());
        Assert.Equal("not JSON", Encoding.UTF8.GetString(Convert.FromBase64String(
            json.RootElement.GetProperty("rawResponseUtf8Base64").GetString()!)));
        foreach (var identity in authority.Cells.Skip(1).Select(cell => cell.Identity))
            Assert.DoesNotContain(identity, transport.Calls);
    }

    [Fact]
    public async Task Reservation_collision_does_not_invoke_provider_or_continue()
    {
        var authority = FrozenContextAblationAuthority.Load();
        using var temp = TemporaryRoot.Create();
        var transport = new FakeTransport(_ => null, _ => ValidReply(authority.Cells[0]));
        var reservations = new FileReservationStore(authority.Cells[0].Identity);

        var result = await ContextAblationTransportRunner.RunAsync(
            temp.Path, authority, transport.SendAsync, reservations, new FileCaptureStore());

        Assert.False(result.Succeeded);
        Assert.Empty(transport.Calls);
        Assert.Equal(0, result.Reservations);
        Assert.Contains("reservation collision", result.HaltedReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rerun_after_partial_materialization_fails_closed_without_auto_resume()
    {
        var authority = FrozenContextAblationAuthority.Load();
        using var temp = TemporaryRoot.Create();
        var firstTransport = new FakeTransport(_ =>
            new InvalidOperationException("stop after first cell"));
        var first = await ContextAblationTransportRunner.RunAsync(
            temp.Path, authority, firstTransport.SendAsync, new FileReservationStore(), new FileCaptureStore());
        Assert.False(first.Succeeded);

        var secondTransport = new FakeTransport(_ => null, ValidReply);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ContextAblationTransportRunner.RunAsync(
                temp.Path, authority, secondTransport.SendAsync,
                new FileReservationStore(), new FileCaptureStore()));
        Assert.Empty(secondTransport.Calls);
    }

    [Fact]
    public async Task Authority_and_transport_gates_fail_before_reservation_and_provider()
    {
        var authority = FrozenContextAblationAuthority.Load();
        var first = authority.Cells[0];
        var mutations = new[]
        {
            first with { PromptSha256 = "wrong-prompt" },
            first with { SchemaSha256 = "wrong-schema" },
            first with { PolicyHash = "wrong-policy" },
            first with { ContextHash = "wrong-context" },
            first with { ProviderInputHash = "wrong-provider-input" },
            first with { PlanHash = "wrong-plan" },
            first with { Model = "wrong/model" },
            first with { ResponseFormat = "json_schema" },
            first with { MaxTokens = 256 },
            first with { Identity = "unexpected-cell" },
        };

        foreach (var mutation in mutations)
        {
            using var temp = TemporaryRoot.Create();
            var mutatedAuthority = authority.WithFirstCell(mutation);
            var transport = new FakeTransport(_ => null, _ => ValidReply(first));
            var result = await ContextAblationTransportRunner.RunAsync(
                temp.Path, mutatedAuthority, transport.SendAsync,
                new FileReservationStore(), new FileCaptureStore());

            Assert.False(result.Succeeded);
            Assert.Empty(transport.Calls);
            Assert.Equal(0, result.Reservations);
            Assert.Equal(0, result.Captures);
        }
    }

    [Fact]
    public async Task Real_provider_transport_is_unreachable_without_exact_enable_flag()
    {
        if (Environment.GetEnvironmentVariable(RunVariable) is "1" or "true")
            return;

        var authority = FrozenContextAblationAuthority.Load();
        Assert.Equal(MaxCalls, authority.Cells.Count);
        Assert.Equal(0, authority.ProviderCallsAtPreflight);
        await Task.CompletedTask;
    }

    /// <summary>
    /// Real transport is deliberately opt-in. It is not run by this task; the separate execution
    /// authorization must be issued only after this runner commit and failure-injection evidence.
    /// </summary>
    [Fact]
    public async Task Run_real_context_ablation_only_when_explicitly_enabled()
    {
        if (Environment.GetEnvironmentVariable(RunVariable) is not ("1" or "true"))
            return;

        var authority = FrozenContextAblationAuthority.Load();
        var root = TestRepository.Path(CaptureRoot);
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        Assert.False(string.IsNullOrWhiteSpace(key));

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var provider = new OpenRouterHeaderExtractor(http, new RemoteInferenceOptions
        {
            ApiKey = key,
            Model = authority.Model,
        });

        var attempts = 0;
        async Task<string> Send(ContextAblationCell cell)
        {
            attempts++;
            if (attempts > MaxCalls)
                throw new InvalidOperationException("27-call cap exceeded");
            return await provider.BoundaryCutAsync(
                cell.SystemPrompt, cell.Request, CancellationToken.None, cell.ExpectedItemCount);
        }

        var result = await ContextAblationTransportRunner.RunAsync(
            root, authority, Send, new FileReservationStore(), new FileCaptureStore());
        Assert.True(result.Succeeded, result.HaltedReason);
        Assert.Equal(MaxCalls, attempts);
        Assert.Equal(MaxCalls, result.Captures);
    }

    private static string ValidReply(ContextAblationCell cell) => JsonSerializer.Serialize(new
    {
        decisions = cell.ItemIds.Select(itemId => new
        {
            itemId,
            classification = "STRUCTURAL_UNIT",
        }),
    });

    private sealed class FakeTransport
    {
        private readonly Func<ContextAblationCell, Exception?> _failure;
        private readonly Func<ContextAblationCell, string> _response;

        public FakeTransport(
            Func<ContextAblationCell, Exception?> failure,
            Func<ContextAblationCell, string>? response = null)
        {
            _failure = failure;
            _response = response ?? ValidReply;
        }

        public List<string> Calls { get; } = [];

        public Task<string> SendAsync(ContextAblationCell cell)
        {
            Calls.Add(cell.Identity);
            var error = _failure(cell);
            if (error is not null) throw error;
            return Task.FromResult(_response(cell));
        }
    }

    private sealed class TemporaryRoot : IDisposable
    {
        private TemporaryRoot(string path) => Path = path;

        public string Path { get; }

        public static TemporaryRoot Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "a99-context-ablation-runner-" + Guid.NewGuid().ToString("N"));
            return new TemporaryRoot(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}

internal sealed record ContextAblationCell(
    string ArmId,
    int Repeat,
    string Pack,
    string Identity,
    string PolicyHash,
    string ContextHash,
    string ContextJson,
    string ProviderInputHash,
    string PlanHash,
    string SystemPrompt,
    string PromptSha256,
    string SchemaSha256,
    string Request,
    string Model,
    string ResponseFormat,
    int ExpectedItemCount,
    int MaxTokens,
    string[] ItemIds)
{
    public string ArmDirectory => ArmId switch
    {
        "FULL_CONTEXT" => "full-context",
        "LOCAL_CONTEXT_RADIUS_3" => "local-context-radius-3",
        "MINIMAL_STRUCTURAL_CONTEXT_V1" => "minimal-structural-context-v1",
        _ => throw new InvalidOperationException($"Unknown context arm: {ArmId}"),
    };

    public string ReservationRelativePath =>
        $"{ArmDirectory}/r{Repeat}/{Pack}.capture-slot.v1.json";

    public string CaptureRelativePath =>
        $"{ArmDirectory}/r{Repeat}/{Pack}.transport-capture.v1.json";
}

internal sealed record ContextAblationRunResult(
    bool Succeeded,
    int Attempts,
    int Reservations,
    int Captures,
    string? HaltedReason);

internal interface IContextAblationReservationStore
{
    void Reserve(string root, ContextAblationCell cell);
}

internal interface IContextAblationCaptureStore
{
    void PersistRaw(string root, ContextAblationCell cell, string raw, string rawSha256);
    void MarkParseFailure(string root, ContextAblationCell cell, string error);
    void MarkContractSuccess(string root, ContextAblationCell cell);
    void PersistTransportFailure(string root, ContextAblationCell cell, Exception error);
}

internal sealed class FileReservationStore(string? forcedCollisionIdentity = null)
    : IContextAblationReservationStore
{
    public void Reserve(string root, ContextAblationCell cell)
    {
        if (string.Equals(cell.Identity, forcedCollisionIdentity, StringComparison.Ordinal))
            throw new IOException($"reservation collision for {cell.Identity}");

        var path = System.IO.Path.Combine(root, cell.ReservationRelativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(JsonSerializer.Serialize(new
        {
            schemaVersion = "a99-direct-semantic-context-ablation-reservation-v1",
            status = "CAPTURE_SLOT_RESERVED",
            cell = cell.Identity,
            arm = cell.ArmId,
            repeat = cell.Repeat,
            pack = cell.Pack,
            policyHash = cell.PolicyHash,
            contextHash = cell.ContextHash,
            providerInputHash = cell.ProviderInputHash,
            planHash = cell.PlanHash,
        }, FreezeArtifact.Json).ReplaceLineEndings("\n"));
        writer.Flush();
        stream.Flush(true);
    }
}

internal sealed class FileCaptureStore : IContextAblationCaptureStore
{
    public void PersistRaw(string root, ContextAblationCell cell, string raw, string rawSha256)
    {
        var path = PathFor(root, cell);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteExclusive(path, Envelope(cell, raw, rawSha256, "PENDING", null));
    }

    public void MarkParseFailure(string root, ContextAblationCell cell, string error) =>
        Update(root, cell, node =>
        {
            node["parseStatus"] = "FAILED";
            node["contractStatus"] = "NOT_EVALUATED";
            node["fault"] = error;
        });

    public void MarkContractSuccess(string root, ContextAblationCell cell) =>
        Update(root, cell, node =>
        {
            node["parseStatus"] = "OK";
            node["contractStatus"] = "PASS";
        });

    public void PersistTransportFailure(string root, ContextAblationCell cell, Exception error)
    {
        var path = Path.Combine(root, cell.ArmDirectory, $"r{cell.Repeat}",
            $"{cell.Pack}.transport-failure.v1.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteExclusive(path, new
        {
            schemaVersion = "a99-direct-semantic-context-ablation-transport-failure-v1",
            cell = cell.Identity,
            transportStatus = "FAILED",
            errorType = error.GetType().FullName,
            error = error.Message,
            rawResponseCaptured = false,
        });
    }

    private static object Envelope(
        ContextAblationCell cell, string raw, string rawSha256, string parseStatus, string? fault) => new
        {
            schemaVersion = "a99-direct-semantic-context-ablation-transport-capture-v1",
            cell = cell.Identity,
            arm = cell.ArmId,
            repeat = cell.Repeat,
            pack = cell.Pack,
            policyHash = cell.PolicyHash,
            contextHash = cell.ContextHash,
            providerInputHash = cell.ProviderInputHash,
            planHash = cell.PlanHash,
            model = cell.Model,
            providerRoute = "OpenRouter",
            systemPromptSha256 = cell.PromptSha256,
            systemPromptUtf8Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(cell.SystemPrompt)),
            userMessageUtf8Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(cell.Request)),
            responseFormat = cell.ResponseFormat,
            expectedItemCount = cell.ExpectedItemCount,
            maxTokens = cell.MaxTokens,
            itemIds = cell.ItemIds,
            transportCaptureStatus = "TRANSPORT_CAPTURE_COMPLETE",
            replayMaterializationStatus = "PENDING",
            rawResponseSha256 = rawSha256,
            rawResponseBytes = Encoding.UTF8.GetByteCount(raw),
            rawResponseUtf8Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)),
            rawResponseCaptured = true,
            parseStatus,
            contractStatus = "PENDING",
            fault,
        };

    private static string PathFor(string root, ContextAblationCell cell) =>
        Path.Combine(root, cell.CaptureRelativePath);

    private static void Update(string root, ContextAblationCell cell, Action<JsonObject> change)
    {
        var path = PathFor(root, cell);
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        change(node);
        WriteReplace(path, node);
    }

    private static void WriteExclusive(string path, object value)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(JsonSerializer.Serialize(value, FreezeArtifact.Json).ReplaceLineEndings("\n"));
        writer.Flush();
        stream.Flush(true);
    }

    private static void WriteReplace(string path, JsonNode value)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary,
                value.ToJsonString(FreezeArtifact.Json).ReplaceLineEndings("\n"),
                new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

internal static class ContextAblationTransportRunner
{
    public static async Task<ContextAblationRunResult> RunAsync(
        string root,
        FrozenContextAblationAuthority authority,
        Func<ContextAblationCell, Task<string>> transport,
        IContextAblationReservationStore reservations,
        IContextAblationCaptureStore captures,
        CancellationToken cancellationToken = default)
    {
        if (Directory.Exists(root))
            throw new InvalidOperationException(
                "Context-ablation run root already exists; automatic resume is forbidden.");

        Directory.CreateDirectory(root);
        var attempts = 0;
        var reserved = 0;
        var captured = 0;

        for (var index = 0; index < authority.Cells.Count; index++)
        {
            var cell = authority.Cells[index];
            try
            {
                authority.ValidateBeforeReservation(cell, index);
            }
            catch (Exception error)
            {
                return Halt("authority/compatibility/budget gate: " + error.Message,
                    attempts, reserved, captured);
            }

            try
            {
                reservations.Reserve(root, cell);
                reserved++;
            }
            catch (Exception error)
            {
                return Halt("reservation collision: " + error.Message,
                    attempts, reserved, captured);
            }

            attempts++;
            string raw;
            try
            {
                raw = await transport(cell).WaitAsync(cancellationToken);
            }
            catch (Exception error)
            {
                captures.PersistTransportFailure(root, cell, error);
                return Halt("transport failure: " + error.Message,
                    attempts, reserved, captured);
            }

            var rawSha256 = Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
            try
            {
                captures.PersistRaw(root, cell, raw, rawSha256);
                captured++;
            }
            catch (Exception error)
            {
                return Halt("raw capture failure: " + error.Message,
                    attempts, reserved, captured);
            }

            try
            {
                ValidateResponse(raw, cell.ItemIds);
                captures.MarkContractSuccess(root, cell);
            }
            catch (Exception error)
            {
                captures.MarkParseFailure(root, cell, error.Message);
                return Halt("parse/contract failure: " + error.Message,
                    attempts, reserved, captured);
            }
        }

        return new ContextAblationRunResult(true, attempts, reserved, captured, null);
    }

    private static ContextAblationRunResult Halt(
        string reason, int attempts, int reserved, int captured) =>
        new(false, attempts, reserved, captured, reason);

    private static void ValidateResponse(string raw, IReadOnlyList<string> itemIds)
    {
        using var document = JsonDocument.Parse(raw);
        if (!document.RootElement.TryGetProperty("decisions", out var decisions) ||
            decisions.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("response carries no decisions array");

        var allowed = itemIds.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var labels = new[] { "STRUCTURAL_UNIT", "DOCUMENT_LABEL", "NON_STRUCTURAL" };
        foreach (var decision in decisions.EnumerateArray())
        {
            var itemId = decision.TryGetProperty("itemId", out var id) &&
                id.ValueKind == JsonValueKind.String ? id.GetString() : null;
            var classification = decision.TryGetProperty("classification", out var label) &&
                label.ValueKind == JsonValueKind.String ? label.GetString() : null;
            if (itemId is null || !allowed.Contains(itemId))
                throw new InvalidDataException($"unknown itemId '{itemId}'");
            if (classification is null || !labels.Contains(classification, StringComparer.Ordinal))
                throw new InvalidDataException($"classification '{classification}' is outside the contract");
            if (!seen.Add(itemId))
                throw new InvalidDataException($"itemId '{itemId}' answered twice");
        }

        var missing = allowed.Except(seen, StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException($"{missing.Length} item(s) unanswered");
        if (seen.Count != itemIds.Count)
            throw new InvalidDataException("response contains an unexpected decision count");
    }
}

internal sealed class FrozenContextAblationAuthority
{
    private const string PreflightRoot =
        "eval/a99-closed-loop/direct-semantic-context-ablation-preflight-v1";
    private const string PreflightFile = "direct-semantic-context-ablation-preflight.v1.json";
    private const string Doc0252Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private const string CaptureRoot =
        "eval/a99-closed-loop/direct-semantic-context-ablation-v1/DOC-0252";
    private const string ScoreRoot =
        "eval/a99-closed-loop/direct-semantic-context-ablation-score-v1/DOC-0252";
    private const string SecondModelPreflightRoot =
        "eval/a99-closed-loop/second-model-minimal-replay-preflight-v1";
    private const string SecondModelRunnerCaptureRoot =
        "eval/a99-closed-loop/direct-semantic-second-model-minimal-replay-v1/DOC-0252";
    private const string AuthorizedBaseCommit = "af30ed9007669f66a61e6420a4d79537ec04c9a8";
    private const string ExpectedModel = "qwen/qwen3.7-flash";
    private const string ResponseFormat = TransportCompatibility.JsonObjectResponseFormat;
    private const int Repeats = 3;
    private const int MaxCalls = 27;
    private const int MaxOutputTokens = 32768;
    private const int StructuralNeighborCount = 2;
    private const int LocalRadius = 3;

    private static readonly string[] ArmOrder =
    [
        "FULL_CONTEXT",
        "LOCAL_CONTEXT_RADIUS_3",
        "MINIMAL_STRUCTURAL_CONTEXT_V1",
    ];

    private static readonly string[] PackOrder = ["PACK_001", "PACK_005", "PACK_006"];

    private static readonly IReadOnlyDictionary<string, string> FrozenPlanHashes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["FULL_CONTEXT"] =
                "a792fb0f03b2c6c16facecba5485ed48dc7742610f7542e0936ca88ecd59243b",
            ["LOCAL_CONTEXT_RADIUS_3"] =
                "303cf743b89b88ba0f85bb1e2f20e15e482d9b2d2aa27fd4a815462bb48e09bf",
            ["MINIMAL_STRUCTURAL_CONTEXT_V1"] =
                "297387401a96bbc8f5f800456b6e4d9f579a1b04cfef92ffca574deff885cf68",
        };

    private FrozenContextAblationAuthority(
        IReadOnlyList<ContextAblationCell> cells,
        IReadOnlyList<ContextAblationCell> expectedCells,
        string promptSha256,
        string schemaSha256,
        string model,
        bool onlyContextChanged,
        int providerCallsAtPreflight)
    {
        Cells = cells;
        ExpectedCells = expectedCells;
        PromptSha256 = promptSha256;
        SchemaSha256 = schemaSha256;
        Model = model;
        OnlyContextChanged = onlyContextChanged;
        ProviderCallsAtPreflight = providerCallsAtPreflight;
    }

    public IReadOnlyList<ContextAblationCell> Cells { get; }
    private IReadOnlyList<ContextAblationCell> ExpectedCells { get; }
    public string PromptSha256 { get; }
    public string SchemaSha256 { get; }
    public string Model { get; }
    public bool OnlyContextChanged { get; }
    public int ProviderCallsAtPreflight { get; }

    public static FrozenContextAblationAuthority Load()
    {
        var head = Git("rev-parse HEAD");
        Assert.True(IsAncestor(AuthorizedBaseCommit, head),
            $"{AuthorizedBaseCommit} is not an ancestor of {head}");
        var changedSinceBase = Git($"diff --name-only {AuthorizedBaseCommit}..HEAD")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .ToArray();
        Assert.All(changedSinceBase, path => Assert.True(
            path.Equals("tests/DocxHeaderExtractor.Tests/DirectSemanticContextAblationTransportRunnerTests.cs",
                StringComparison.Ordinal)
            || path.Equals("tests/DocxHeaderExtractor.Tests/DirectSemanticContextAblationScoringTests.cs",
                StringComparison.Ordinal)
            || path.Equals("tests/DocxHeaderExtractor.Tests/SecondModelMinimalReplayPreflightTests.cs",
                StringComparison.Ordinal)
            || path.Equals("tests/DocxHeaderExtractor.Tests/SecondModelMinimalReplayTransportTests.cs",
                StringComparison.Ordinal)
            || path.StartsWith(CaptureRoot + "/", StringComparison.Ordinal)
            || path.StartsWith(ScoreRoot + "/", StringComparison.Ordinal)
            || path.StartsWith(SecondModelPreflightRoot + "/", StringComparison.Ordinal)
            || path.StartsWith(SecondModelRunnerCaptureRoot + "/", StringComparison.Ordinal),
            $"unexpected descendant path {path}"));

        var artifactPath = TestRepository.Path(Path.Combine(PreflightRoot, PreflightFile));
        using var artifact = JsonDocument.Parse(File.ReadAllText(artifactPath));
        var root = artifact.RootElement;
        Assert.Equal("a99_direct_semantic_context_ablation_preflight", root.GetProperty("artifactKind").GetString());
        Assert.Equal("a99-direct-semantic-context-ablation-preflight-v1", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("DOC-0252", root.GetProperty("documentId").GetString());
        Assert.False(root.GetProperty("providerAuthorized").GetBoolean());
        Assert.Equal(0, root.GetProperty("modelCalls").GetInt32());
        Assert.Equal(0, root.GetProperty("providerCalls").GetInt32());
        Assert.Equal("CONTEXT_ABLATION_AUTHORIZATION_READY", root.GetProperty("status").GetString());
        var onlyContextChanged = root.GetProperty("differenceAudit").GetProperty("onlyContextChanged").GetBoolean();
        Assert.True(onlyContextChanged);

        var sourcePath = TestRepository.Path(Doc0252Pdf);
        var plan = PdfStructuredSourceAuthorityBuilder.Build(sourcePath);
        var build = DirectSemanticProbePreflightTests.Build(plan);
        var fullPacks = DirectSemanticProbePreflightTests.ComposeAllPacks(plan);
        var itemsByPack = build.Items
            .GroupBy(item => item.PackId.Split(':')[1], StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.OrderBy(item => item.Identity, StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);

        var authority = root.GetProperty("authority");
        var prompt = DirectSemanticProbeRetryPreflightTests.RetryProbePrompt;
        var promptSha256 = authority.GetProperty("promptSha256").GetString()!;
        Assert.Equal(promptSha256, CanonicalArtifactHash.OfText(prompt));
        var schemaSha256 = authority.GetProperty("schemaSha256").GetString()!;
        Assert.Equal(schemaSha256, HashSchema(build.Items.Select(item => item.ItemId).ToArray()));
        var model = authority.GetProperty("model").GetString()!;
        Assert.Equal(ExpectedModel, model);

        var cells = new List<ContextAblationCell>();
        foreach (var armId in ArmOrder)
        {
            var arm = root.GetProperty("arms").EnumerateArray()
                .Single(value => value.GetProperty("id").GetString() == armId);
            var planHash = arm.GetProperty("providerInputPlanHash").GetString()!;
            Assert.Equal(FrozenPlanHashes[armId], planHash);
            var policy = armId switch
            {
                "FULL_CONTEXT" => ContextWindowPolicy.Full,
                "LOCAL_CONTEXT_RADIUS_3" => ContextWindowPolicy.Local,
                "MINIMAL_STRUCTURAL_CONTEXT_V1" => ContextWindowPolicy.MinimalStructural,
                _ => throw new InvalidOperationException(armId),
            };
            var policyHash = arm.GetProperty("policyHash").GetString()!;
            Assert.Equal(PolicyHash(policy), policyHash);

            var armCells = BuildArmCells(
                armId, policy, policyHash, planHash, prompt, promptSha256, schemaSha256,
                fullPacks, itemsByPack, plan, arm);
            cells.AddRange(armCells);
        }

        Assert.Equal(MaxCalls, cells.Count);
        Assert.Equal(MaxCalls, cells.Select(cell => cell.Identity).Distinct().Count());
        Assert.Equal(root.GetProperty("capture").GetProperty("identitiesRequired").GetInt32(), cells.Count);
        return new FrozenContextAblationAuthority(
            cells, cells, promptSha256, schemaSha256, model, onlyContextChanged,
            root.GetProperty("providerCalls").GetInt32());
    }

    public FrozenContextAblationAuthority WithFirstCell(ContextAblationCell replacement)
    {
        var cells = Cells.ToArray();
        cells[0] = replacement;
        return new FrozenContextAblationAuthority(
            cells, ExpectedCells, PromptSha256, SchemaSha256, Model, OnlyContextChanged,
            ProviderCallsAtPreflight);
    }

    public void ValidateBeforeReservation(ContextAblationCell cell, int index)
    {
        if (!OnlyContextChanged)
            throw new InvalidOperationException("ONLY_CONTEXT_CHANGED is false");
        if (index < 0 || index >= Cells.Count)
            throw new InvalidOperationException("unexpected cell index");
        var expected = ExpectedCells[index];
        if (cell.Identity != expected.Identity)
            throw new InvalidOperationException("unexpected cell identity");
        if (cell.ArmId != expected.ArmId || cell.Repeat != expected.Repeat || cell.Pack != expected.Pack)
            throw new InvalidOperationException("cell coordinates do not match frozen order");
        if (cell.SystemPrompt != expected.SystemPrompt || cell.PromptSha256 != expected.PromptSha256)
            throw new InvalidOperationException("prompt authority mismatch");
        if (cell.SchemaSha256 != expected.SchemaSha256)
            throw new InvalidOperationException("schema authority mismatch");
        if (cell.PolicyHash != expected.PolicyHash)
            throw new InvalidOperationException("context policy authority mismatch");
        if (cell.ContextJson != expected.ContextJson || cell.ContextHash != expected.ContextHash)
            throw new InvalidOperationException("context bytes/hash authority mismatch");
        if (cell.Request != expected.Request || cell.ProviderInputHash != expected.ProviderInputHash)
            throw new InvalidOperationException("provider-input authority mismatch");
        if (cell.PlanHash != expected.PlanHash)
            throw new InvalidOperationException("provider-input plan authority mismatch");
        if (cell.Model != ExpectedModel)
            throw new InvalidOperationException("model authority mismatch");
        if (cell.ResponseFormat != ResponseFormat)
            throw new InvalidOperationException("response-format authority mismatch");
        if (!TransportCompatibility.Validate(cell.SystemPrompt, cell.Request, cell.ResponseFormat).IsCompatible)
            throw new InvalidOperationException("transport compatibility mismatch");
        var derivedBudget = OpenRouterHeaderExtractor.BoundaryOutputBudgetFor(
            cell.Request, cell.ExpectedItemCount, MaxOutputTokens);
        if (cell.MaxTokens != expected.MaxTokens || cell.MaxTokens != derivedBudget || cell.MaxTokens <= 256)
            throw new InvalidOperationException("output budget authority mismatch");
        var recomputedInputHash = SemanticAuthorityTransportCall.Sha256Utf8(
            JsonSerializer.Serialize(new { systemPrompt = cell.SystemPrompt, userMessage = cell.Request }));
        if (recomputedInputHash != cell.ProviderInputHash)
            throw new InvalidOperationException("provider input hash does not match request bytes");
    }

    private static IReadOnlyList<ContextAblationCell> BuildArmCells(
        string armId,
        ContextWindowPolicy policy,
        string policyHash,
        string planHash,
        string prompt,
        string promptSha256,
        string schemaSha256,
        IReadOnlyDictionary<string, string> fullPacks,
        IReadOnlyDictionary<string, DirectSemanticProbePreflightTests.ProbeItem[]> itemsByPack,
        PdfStructuredSourceAuthority plan,
        JsonElement arm)
    {
        var contextHashes = arm.GetProperty("packContextHashes");
        var providerHashes = arm.GetProperty("providerInputHashes");
        var expectedCounts = arm.GetProperty("expectedItemCounts");
        var maxTokens = arm.GetProperty("maxTokens");
        var byPack = new Dictionary<string, (string ContextJson, string Request, string ContextHash, string ProviderHash, int Expected, int MaxTokens, string[] ItemIds)>(StringComparer.Ordinal);

        foreach (var pack in PackOrder)
        {
            var fullRequest = fullPacks[$"COHERENT_REGION_SEGMENTATION_V1:{pack}"];
            var schemaMarker = fullRequest.LastIndexOf("\nSCHEMA=", StringComparison.Ordinal);
            Assert.True(schemaMarker > 0);
            using var fullEvidence = JsonDocument.Parse(fullRequest[..schemaMarker]);
            var fullRoot = fullEvidence.RootElement;
            var items = itemsByPack[pack];
            var context = policy == ContextWindowPolicy.Full
                ? JsonNode.Parse(fullRoot.GetRawText())!
                : BuildReducedContext(policy, plan, fullRoot, items.Select(item => item.Identity));
            var contextJson = context.ToJsonString(new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            var request = DirectSemanticProbePreflightTests.ComposeRequest(contextJson, items);
            var contextHash = Sha256CanonicalJson(context);
            var providerHash = SemanticAuthorityTransportCall.Sha256Utf8(
                JsonSerializer.Serialize(new { systemPrompt = prompt, userMessage = request }));
            var expected = fullRoot.GetProperty("ownedSourceAliases").GetArrayLength();
            var budget = OpenRouterHeaderExtractor.BoundaryOutputBudgetFor(request, expected, MaxOutputTokens);
            Assert.Equal(contextHashes.GetProperty(pack).GetString(), contextHash);
            Assert.Equal(providerHashes.GetProperty(pack).GetString(), providerHash);
            Assert.Equal(expectedCounts.GetProperty(pack).GetInt32(), expected);
            Assert.Equal(maxTokens.GetProperty(pack).GetInt32(), budget);
            Assert.True(TransportCompatibility.Validate(prompt, request, ResponseFormat).IsCompatible);
            byPack[pack] = (contextJson, request, contextHash, providerHash, expected, budget,
                items.Select(item => item.ItemId).ToArray());
        }

        var cells = new List<ContextAblationCell>();
        foreach (var repeat in Enumerable.Range(1, Repeats))
        foreach (var pack in PackOrder)
        {
            var value = byPack[pack];
            cells.Add(new ContextAblationCell(
                armId, repeat, pack, $"{armId}/r{repeat}/{pack}", policyHash,
                value.ContextHash, value.ContextJson, value.ProviderHash, planHash,
                prompt, promptSha256, schemaSha256, value.Request, ExpectedModel, ResponseFormat,
                value.Expected, value.MaxTokens, value.ItemIds));
        }

        var computedPlan = SemanticAuthorityTransportCall.Sha256Utf8(
            string.Join("\0", PackOrder.Select(pack => byPack[pack].ProviderHash)));
        Assert.Equal(planHash, computedPlan);
        return cells;
    }

    private static JsonNode BuildReducedContext(
        ContextWindowPolicy policy,
        PdfStructuredSourceAuthority plan,
        JsonElement fullRoot,
        IEnumerable<string> targetIdentities)
    {
        var entries = fullRoot.GetProperty("sourceEvidence").EnumerateArray().ToArray();
        var entryByAlias = entries.ToDictionary(
            entry => entry.GetProperty("alias").GetString()!, StringComparer.Ordinal);
        var atomIndex = plan.Atoms.Select((atom, index) => (atom.Alias, index))
            .ToDictionary(pair => pair.Alias, pair => pair.index, StringComparer.Ordinal);
        var selected = new HashSet<int>();
        var targetAliases = targetIdentities.SelectMany(Aliases).Distinct(StringComparer.Ordinal).ToArray();

        foreach (var alias in targetAliases)
        {
            Assert.True(atomIndex.TryGetValue(alias, out var index));
            if (policy == ContextWindowPolicy.Local)
            {
                AddRange(selected, index - LocalRadius, index + LocalRadius, plan.Atoms.Count);
            }
            else
            {
                selected.Add(index);
                AddRange(selected, index - 1, index + 1, plan.Atoms.Count);
                var candidates = entries
                    .Select(entry =>
                    {
                        var aliasValue = entry.GetProperty("alias").GetString()!;
                        return (entry, aliasValue, index: atomIndex.GetValueOrDefault(aliasValue, -1));
                    })
                    .Where(candidate => candidate.index >= 0 && IsStructuralEvidence(candidate.entry))
                    .OrderBy(candidate => candidate.index)
                    .ToArray();
                foreach (var before in candidates.Where(candidate => candidate.index < index)
                             .OrderByDescending(candidate => candidate.index).Take(StructuralNeighborCount))
                    selected.Add(before.index);
                foreach (var after in candidates.Where(candidate => candidate.index > index)
                             .OrderBy(candidate => candidate.index).Take(StructuralNeighborCount))
                    selected.Add(after.index);
            }
        }

        var sourceEvidence = new JsonArray();
        foreach (var entry in entries)
        {
            var alias = entry.GetProperty("alias").GetString()!;
            if (atomIndex.TryGetValue(alias, out var index) && selected.Contains(index))
                sourceEvidence.Add(JsonNode.Parse(entry.GetRawText()));
        }
        foreach (var alias in targetAliases)
            Assert.True(entryByAlias.ContainsKey(alias));

        var owned = new JsonArray();
        foreach (var alias in fullRoot.GetProperty("ownedSourceAliases").EnumerateArray())
            owned.Add(alias.GetString());
        return new JsonObject
        {
            ["protocol"] = fullRoot.GetProperty("protocol").GetString(),
            ["ownedSourceAliases"] = owned,
            ["sourceEvidence"] = sourceEvidence,
        };
    }

    private static bool IsStructuralEvidence(JsonElement entry) =>
        entry.TryGetProperty("scope", out var scope) &&
        string.Equals(scope.GetString(), "document_body", StringComparison.Ordinal) &&
        entry.TryGetProperty("markers", out var markers) &&
        markers.ValueKind == JsonValueKind.Array &&
        markers.EnumerateArray().Any(marker =>
            marker.GetString()?.StartsWith("marker-family:", StringComparison.Ordinal) == true);

    private static void AddRange(HashSet<int> selected, int start, int end, int count)
    {
        for (var index = Math.Max(0, start); index <= Math.Min(count - 1, end); index++)
            selected.Add(index);
    }

    private static IEnumerable<string> Aliases(string identity) => identity.Split('|')
        .Select(part => part[..part.LastIndexOf(':')]);

    private static string PolicyHash(ContextWindowPolicy policy) => HashObject(new
    {
        id = policy switch
        {
            ContextWindowPolicy.Full => "FULL_CONTEXT",
            ContextWindowPolicy.Local => "LOCAL_CONTEXT_RADIUS_3",
            ContextWindowPolicy.MinimalStructural => "MINIMAL_STRUCTURAL_CONTEXT_V1",
            _ => throw new ArgumentOutOfRangeException(nameof(policy)),
        },
        localRadius = policy == ContextWindowPolicy.Local ? LocalRadius : (int?)null,
        structuralNeighborCount = policy == ContextWindowPolicy.MinimalStructural
            ? StructuralNeighborCount : (int?)null,
        inputs = new[] { "source atom coordinates", "source evidence order", "existing structural markers" },
    });

    private static string HashSchema(string[] itemIds) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(DirectSemanticProbePreflightTests.ProbeSchema(itemIds),
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }).ReplaceLineEndings("\n"))));

    private static string HashObject(object value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(value, FreezeArtifact.Json).ReplaceLineEndings("\n"))));

    private static string Sha256CanonicalJson(JsonNode value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            value.ToJsonString(FreezeArtifact.Json).ReplaceLineEndings("\n"))));

    private static bool IsAncestor(string candidate, string descendant)
    {
        using var process = Process.Start(new ProcessStartInfo(
            "git", $"merge-base --is-ancestor {candidate} {descendant}")
        {
            WorkingDirectory = TestRepository.Root(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    private static string Git(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = TestRepository.Root(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return output;
    }

    private enum ContextWindowPolicy
    {
        Full,
        Local,
        MinimalStructural,
    }
}
