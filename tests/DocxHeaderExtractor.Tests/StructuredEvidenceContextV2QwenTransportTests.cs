using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Execution transport for FULL_STRUCTURED_CONTEXT_V2 (STRUCTURED_EVIDENCE_CONTEXT_V2_EXECUTION),
/// the arm frozen as the sole execution candidate in commit 5186081
/// (structured-evidence-context-v2/DOC-0252/execution-candidate.v1.json). FULL_STRUCTURED_CONTEXT_V1
/// is historical preflight evidence only and is never executed from here.
/// <para>
/// Cells are derived deterministically from the same immutable FULL_CONTEXT captures every earlier
/// stage read (repeat 1; request bytes are identical across repeats), re-grouped into the frozen
/// page-then-block hierarchy. Same model, temperature, reasoning effort, response format and
/// provider routing as the original ablation cohort (via the same production
/// <see cref="OpenRouterHeaderExtractor.BoundaryCutAsync"/> call site
/// <c>DirectSemanticContextAblationTransportRunnerTests.Run_real_context_ablation_only_when_explicitly_enabled</c>
/// uses) - not a new transport policy. Unreachable unless <see cref="RunVariable"/> is set; disabled
/// by default so this file never spends a real call on an ordinary test run.
/// </para>
/// </summary>
public sealed class StructuredEvidenceContextV2QwenTransportTests
{
    private const string RunVariable = "A99_STRUCTURED_EVIDENCE_CONTEXT_V2_QWEN_RUN";
    private const string CaptureRoot =
        "eval/a99-closed-loop/structured-evidence-context-v1/DOC-0252/execution/qwen";
    private const string FullContextCaptureRoot =
        "eval/a99-closed-loop/direct-semantic-context-ablation-v1/DOC-0252/full-context/r1";
    private const string SourcePdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private const string Model = "qwen/qwen3.7-flash";
    private const string ProviderBackend = "OpenRouter";
    private const string ResponseFormat = TransportCompatibility.JsonObjectResponseFormat;
    private const int Repeats = 3;
    private const int MaxOutputTokens = 32768;
    private const int MaxCalls = 9;

    private static readonly string[] Packs = ["PACK_001", "PACK_005", "PACK_006"];

    [Fact]
    public void Cells_are_derived_deterministically_from_frozen_evidence()
    {
        var first = BuildCells();
        var second = BuildCells();
        Assert.Equal(9, first.Count);
        Assert.Equal(MaxCalls, first.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].SystemPrompt, second[i].SystemPrompt);
            Assert.Equal(first[i].UserMessage, second[i].UserMessage);
            Assert.Equal(first[i].ProviderInputHash, second[i].ProviderInputHash);
        }

        // Same 18-item cohort, same schema/prompt authority as every earlier arm.
        Assert.Equal(18, first.Where(c => c.Repeat == 1).Sum(c => c.ItemIds.Length));
        Assert.All(first, cell => Assert.Equal(
            "5e8d393c7e78af28a9695011e63cb4bad00505467532bdcf14582e551aa2a387", cell.PromptSha256));
    }

    [Fact]
    public async Task Fake_transport_reserves_before_send_and_never_resumes_an_existing_root()
    {
        using var temp = new TemporaryRoot();
        var cells = BuildCells();
        var calls = new List<string>();

        Task<string> Fake(Cell cell)
        {
            calls.Add(cell.Identity);
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                decisions = cell.ItemIds.Select(id => new { itemId = id, classification = "STRUCTURAL_UNIT" }),
            }));
        }

        var result = await RunAsync(temp.Path, cells, Fake);
        Assert.True(result.Succeeded, result.HaltedReason);
        Assert.Equal(9, result.Captures);
        Assert.Equal(cells.Select(c => c.Identity), calls);

        foreach (var cell in cells)
        {
            Assert.True(File.Exists(Path.Combine(temp.Path, cell.ReservationRelativePath)));
            Assert.True(File.Exists(Path.Combine(temp.Path, cell.CaptureRelativePath)));
        }

        // No automatic resume: running again against the same, already-populated root is refused.
        var rerun = await RunAsync(temp.Path, cells, Fake);
        Assert.False(rerun.Succeeded);
        Assert.Equal(0, rerun.Attempts);
    }

    [Fact]
    public async Task Fake_transport_stops_before_reservation_reuse_and_does_not_overwrite_a_capture()
    {
        using var temp = new TemporaryRoot();
        var cells = BuildCells();
        Directory.CreateDirectory(temp.Path);
        var firstCell = cells[0];
        Directory.CreateDirectory(Path.Combine(temp.Path,
            Path.GetDirectoryName(firstCell.ReservationRelativePath)!));
        // Simulate a reservation that already exists (e.g. a prior partial attempt).
        using (var stream = new FileStream(
            Path.Combine(temp.Path, firstCell.ReservationRelativePath),
            FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
        }

        var attempts = 0;
        Task<string> Fake(Cell cell)
        {
            attempts++;
            return Task.FromResult("{}");
        }

        Directory.Delete(temp.Path, recursive: true);
        Directory.CreateDirectory(temp.Path);
        Directory.CreateDirectory(Path.Combine(temp.Path,
            Path.GetDirectoryName(firstCell.ReservationRelativePath)!));
        using (var stream = new FileStream(
            Path.Combine(temp.Path, firstCell.ReservationRelativePath),
            FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
        }

        var result = await RunAsyncWithoutRootCheck(temp.Path, cells, Fake);
        Assert.False(result.Succeeded);
        Assert.Equal(0, attempts);
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
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                decisions = cell.ItemIds.Select(id => new { itemId = id, classification = "STRUCTURAL_UNIT" }),
            }));
        }

        var result = await RunAsync(temp.Path, cells, Failing);
        Assert.False(result.Succeeded);
        Assert.Equal(1, attempts);
        Assert.Equal(0, result.Captures);
        var failurePath = Path.Combine(temp.Path, cells[0].ArmDirectory,
            $"r{cells[0].Repeat}", $"{cells[0].Pack}.transport-failure.v1.json");
        Assert.True(File.Exists(failurePath));
    }

    [Fact]
    public async Task Real_transport_is_unreachable_without_exact_authorization_flag()
    {
        if (Environment.GetEnvironmentVariable(RunVariable) is "1" or "true") return;

        var cells = BuildCells();
        Assert.Equal(MaxCalls, cells.Count);

        var root = TestRepository.Path(CaptureRoot);
        if (!Directory.Exists(root))
        {
            // PRE_EXECUTION: no authorized run has ever produced evidence here yet.
            return;
        }

        // POST_EXECUTION_FROZEN: the one authorized real run (STRUCTURED_EVIDENCE_CONTEXT_V2_EXECUTION)
        // already completed and its evidence is committed, immutable history - this branch does not,
        // and must not, assert the root's absence. Instead it verifies that history is exactly the
        // frozen 9-capture set and was never silently extended or reused by an unauthorized run.
        var armRoot = Path.Combine(root, "full-structured-context-v2");
        Assert.True(Directory.Exists(armRoot));
        var captures = Directory.GetFiles(armRoot, "*.transport-capture.v1.json", SearchOption.AllDirectories);
        Assert.Equal(MaxCalls, captures.Length);
        Assert.All(captures, path =>
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("PASS", doc.RootElement.GetProperty("contractStatus").GetString());
        });
    }

    [Fact]
    public async Task Run_real_structured_v2_qwen_execution_only_when_explicitly_enabled()
    {
        if (Environment.GetEnvironmentVariable(RunVariable) is not ("1" or "true"))
            return;

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
            if (attempts > MaxCalls)
                throw new InvalidOperationException("9-call cap exceeded");
            return await provider.BoundaryCutAsync(
                cell.SystemPrompt, cell.UserMessage, CancellationToken.None, cell.ExpectedItemCount);
        }

        var root = TestRepository.Path(CaptureRoot);
        var result = await RunAsync(root, cells, Send);
        Assert.True(result.Succeeded, result.HaltedReason);
        Assert.Equal(MaxCalls, attempts);
        Assert.Equal(MaxCalls, result.Captures);
    }

    // ---- cell construction --------------------------------------------------------------------
    // BuildCells/BuildPackRequest/Cell/PackRequest are internal (not private) so
    // SelectiveSemanticEscalationV1V2ViewMaterializationTests can derive the FULL_STRUCTURED_CONTEXT_V2
    // adjudicator view from this exact same deterministic builder, rather than reimplementing it.

    internal static IReadOnlyList<Cell> BuildCells()
    {
        var plan = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(SourcePdf));
        var pageByAlias = plan.Atoms.ToDictionary(a => a.Alias, a => a.Page, StringComparer.Ordinal);

        var perPack = Packs.Select(pack => BuildPackRequest(pack, pageByAlias)).ToArray();

        var cells = new List<Cell>();
        foreach (var repeat in Enumerable.Range(1, Repeats))
            foreach (var request in perPack)
                cells.Add(new Cell(
                    "FULL_STRUCTURED_CONTEXT_V2", repeat, request.Pack,
                    $"FULL_STRUCTURED_CONTEXT_V2/r{repeat}/{request.Pack}",
                    request.SystemPrompt, request.PromptSha256, request.UserMessage,
                    request.ProviderInputHash, request.ExpectedItemCount, request.MaxTokens,
                    request.ItemIds));
        return cells;
    }

    internal static PackRequest BuildPackRequest(string pack, IReadOnlyDictionary<string, int> pageByAlias)
    {
        var path = TestRepository.Path(Path.Combine(FullContextCaptureRoot, $"{pack}.transport-capture.v1.json"));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var systemPrompt = Encoding.UTF8.GetString(Convert.FromBase64String(
            document.RootElement.GetProperty("systemPromptUtf8Base64").GetString()!));
        var promptSha256 = document.RootElement.GetProperty("systemPromptSha256").GetString()!;
        Assert.Equal(promptSha256, CanonicalArtifactHash.OfText(systemPrompt));

        var userMessage = Encoding.UTF8.GetString(Convert.FromBase64String(
            document.RootElement.GetProperty("userMessageUtf8Base64").GetString()!));
        var marker = userMessage.LastIndexOf("\nSCHEMA=", StringComparison.Ordinal);
        Assert.True(marker > 0);
        var schemaJson = userMessage[(marker + "\nSCHEMA=".Length)..];
        using var body = JsonDocument.Parse(userMessage[..marker]);
        var root = body.RootElement;

        var atoms = root.GetProperty("documentEvidence").GetProperty("sourceEvidence")
            .EnumerateArray()
            .Select(e =>
            {
                var alias = e.GetProperty("alias").GetString()!;
                return (Alias: alias, Block: e.GetProperty("block").GetString()!,
                    Text: e.GetProperty("text").GetString()!, Page: pageByAlias.GetValueOrDefault(alias, -1));
            })
            .ToArray();
        Assert.All(atoms, a => Assert.True(a.Page > 0));

        var items = root.GetProperty("itemsToClassify").EnumerateArray()
            .Select(e => (ItemId: e.GetProperty("ItemId").GetString()!, Text: e.GetProperty("sourceText").GetString()!))
            .ToArray();

        var text = new StringBuilder();
        text.Append("{\"probe\":\"direct-semantic-classification-structured-v2\",");
        text.Append("\"itemsToClassify\":[");
        text.Append(string.Join(",", items.Select(item =>
            $"{{\"ItemId\":{JsonSerializer.Serialize(item.ItemId)},\"sourceText\":{JsonSerializer.Serialize(item.Text)}}}")));
        text.Append("]}\n\n");

        text.Append("SOURCE REGIONS (physical page, then existing layout block; document order; context only)\n\n");
        var regionIdx = 0;
        var i = 0;
        while (i < atoms.Length)
        {
            var page = atoms[i].Page;
            regionIdx++;
            text.Append('[').Append($"R{regionIdx:000}").Append("]\n");
            var blockIdx = 0;
            string? curBlock = null;
            while (i < atoms.Length && atoms[i].Page == page)
            {
                if (curBlock is null || atoms[i].Block != curBlock)
                {
                    blockIdx++;
                    text.Append('[').Append($"B{blockIdx:00}").Append("]\n");
                    curBlock = atoms[i].Block;
                }
                text.Append('[').Append(i + 1).Append("] ").Append(atoms[i].Alias).Append(": ").Append(atoms[i].Text).Append('\n');
                i++;
            }
            text.Append('\n');
        }
        text.Append("REGION ORDER\n");
        text.Append(string.Join(" -> ", Enumerable.Range(1, regionIdx).Select(n => $"R{n:000}")));
        text.Append('\n');

        var packet = text.ToString().ReplaceLineEndings("\n");
        var request = packet + "\nSCHEMA=" + schemaJson;
        var compatibility = TransportCompatibility.Validate(systemPrompt, request, ResponseFormat);
        if (!compatibility.IsCompatible)
            throw new InvalidOperationException($"{compatibility.Reason}: {compatibility.Detail}");

        var itemIds = items.Select(it => it.ItemId).ToArray();
        var maxTokens = OpenRouterHeaderExtractor.BoundaryOutputBudgetFor(request, itemIds.Length, MaxOutputTokens);
        Assert.True(maxTokens >= 256);

        var providerInputHash = SemanticAuthorityTransportCall.Sha256Utf8(
            JsonSerializer.Serialize(new { systemPrompt, userMessage = request }));

        return new PackRequest(pack, systemPrompt, promptSha256, request, providerInputHash, itemIds.Length, maxTokens, itemIds);
    }

    // ---- minimal, self-contained runner (mirrors ContextAblationTransportRunner's protocol:
    // reserve before send, persist raw before parse, no retry, no resume) ----------------------

    private static async Task<RunResult> RunAsync(
        string root, IReadOnlyList<Cell> cells, Func<Cell, Task<string>> transport)
    {
        if (Directory.Exists(root))
            return new RunResult(false, 0, 0, "execution root already exists; automatic resume is forbidden");
        return await RunAsyncWithoutRootCheck(root, cells, transport);
    }

    private static async Task<RunResult> RunAsyncWithoutRootCheck(
        string root, IReadOnlyList<Cell> cells, Func<Cell, Task<string>> transport)
    {
        Directory.CreateDirectory(root);
        var attempts = 0;
        var captured = 0;

        foreach (var cell in cells)
        {
            try
            {
                Reserve(root, cell);
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
                PersistTransportFailure(root, cell, error);
                return new RunResult(false, attempts, captured, "transport failure: " + error.Message);
            }

            var rawSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
            try
            {
                PersistCapture(root, cell, raw, rawSha256, validated: false, fault: null);
            }
            catch (Exception error)
            {
                return new RunResult(false, attempts, captured, "raw capture failure: " + error.Message);
            }

            try
            {
                ValidateResponse(raw, cell.ItemIds);
                MarkContractSuccess(root, cell);
                captured++;
            }
            catch (Exception error)
            {
                MarkParseFailure(root, cell, error.Message);
                return new RunResult(false, attempts, captured, "parse/contract failure: " + error.Message);
            }
        }

        return new RunResult(true, attempts, captured, null);
    }

    private static void Reserve(string root, Cell cell)
    {
        var path = Path.Combine(root, cell.ReservationRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteExclusive(path, new
        {
            schemaVersion = "a99-structured-evidence-context-v2-reservation-v1",
            status = "CAPTURE_SLOT_RESERVED",
            cell = cell.Identity,
            arm = cell.ArmId,
            repeat = cell.Repeat,
            pack = cell.Pack,
            providerInputHash = cell.ProviderInputHash,
        });
    }

    private static void PersistCapture(
        string root, Cell cell, string raw, string rawSha256, bool validated, string? fault)
    {
        var path = Path.Combine(root, cell.CaptureRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteExclusive(path, new
        {
            schemaVersion = "a99-structured-evidence-context-v2-transport-capture-v1",
            cell = cell.Identity,
            arm = cell.ArmId,
            repeat = cell.Repeat,
            pack = cell.Pack,
            model = Model,
            providerRoute = ProviderBackend,
            systemPromptSha256 = cell.PromptSha256,
            providerInputHash = cell.ProviderInputHash,
            responseFormat = ResponseFormat,
            expectedItemCount = cell.ExpectedItemCount,
            maxTokens = cell.MaxTokens,
            itemIds = cell.ItemIds,
            transportCaptureStatus = "TRANSPORT_CAPTURE_COMPLETE",
            rawResponseCaptured = true,
            rawResponseSha256 = rawSha256,
            rawResponseBytes = Encoding.UTF8.GetByteCount(raw),
            rawResponseUtf8Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)),
            parseStatus = "PENDING",
            contractStatus = "PENDING",
            fault = (string?)null,
        });
    }

    private static void MarkContractSuccess(string root, Cell cell) =>
        UpdateCapture(root, cell, node =>
        {
            node["parseStatus"] = "OK";
            node["contractStatus"] = "PASS";
        });

    private static void MarkParseFailure(string root, Cell cell, string error) =>
        UpdateCapture(root, cell, node =>
        {
            node["parseStatus"] = "FAILED";
            node["contractStatus"] = "NOT_EVALUATED";
            node["fault"] = error;
        });

    private static void UpdateCapture(string root, Cell cell, Action<System.Text.Json.Nodes.JsonObject> mutate)
    {
        var path = Path.Combine(root, cell.CaptureRelativePath);
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        mutate(node);
        File.WriteAllText(path, node.ToJsonString(FreezeArtifact.Json).ReplaceLineEndings("\n"));
    }

    private static void PersistTransportFailure(string root, Cell cell, Exception error)
    {
        var path = Path.Combine(root, cell.ArmDirectory, $"r{cell.Repeat}", $"{cell.Pack}.transport-failure.v1.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteExclusive(path, new
        {
            schemaVersion = "a99-structured-evidence-context-v2-transport-failure-v1",
            cell = cell.Identity,
            transportStatus = "FAILED",
            errorType = error.GetType().FullName,
            message = error.Message,
        });
    }

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

    private static void WriteExclusive(string path, object value)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(JsonSerializer.Serialize(value, FreezeArtifact.Json).ReplaceLineEndings("\n"));
        writer.Flush();
        stream.Flush(true);
    }

    internal sealed record PackRequest(
        string Pack, string SystemPrompt, string PromptSha256, string UserMessage,
        string ProviderInputHash, int ExpectedItemCount, int MaxTokens, string[] ItemIds);

    internal sealed record Cell(
        string ArmId, int Repeat, string Pack, string Identity,
        string SystemPrompt, string PromptSha256, string UserMessage,
        string ProviderInputHash, int ExpectedItemCount, int MaxTokens, string[] ItemIds)
    {
        public string ArmDirectory => "full-structured-context-v2";
        public string ReservationRelativePath => $"{ArmDirectory}/r{Repeat}/{Pack}.capture-slot.v1.json";
        public string CaptureRelativePath => $"{ArmDirectory}/r{Repeat}/{Pack}.transport-capture.v1.json";
    }

    private sealed record RunResult(bool Succeeded, int Attempts, int Captures, string? HaltedReason);

    private sealed class TemporaryRoot : IDisposable
    {
        public TemporaryRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "a99-structured-v2-qwen-" + Guid.NewGuid().ToString("N"));
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
