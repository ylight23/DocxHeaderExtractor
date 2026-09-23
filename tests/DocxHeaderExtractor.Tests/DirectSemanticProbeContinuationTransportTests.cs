using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The two-cell continuation of the partially completed direct semantic probe.
/// The required cells are derived from the prior ledger; no successful cell is eligible again.
/// </summary>
public sealed class DirectSemanticProbeContinuationTransportTests
{
    private const string RunVariable = "A99_DIRECT_SEMANTIC_PROBE_CONTINUATION_V1_RUN";
    private const string OutputRoot =
        "eval/a99-closed-loop/direct-semantic-discrimination-probe-continuation-v1/DOC-0252";
    private const string PriorRoot =
        "eval/a99-closed-loop/direct-semantic-discrimination-probe-retry-v1/DOC-0252";
    private const string RejectedRoot = "eval/a99-closed-loop/direct-semantic-probe-v1/DOC-0252";
    private const string Doc0252Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private const string AuthorizedCommit = "4f961101e488b82b7a9d984f70a778f087108de1";
    private const string SchemaSha256 =
        "20f937f19ef560380f9ec7f76ad43fa8dec4442dafdd9c404eb6c258e14295b9";
    private const string PromptSha256 =
        "5e8d393c7e78af28a9695011e63cb4bad00505467532bdcf14582e551aa2a387";
    private const string Pack001ProviderInputSha256 =
        "22d3bbe551900fd0e47f0144054e66c16ef12b03f030540c34b9856886450d37";
    private const string Pack005ProviderInputSha256 =
        "8ce58a9c478f481f6d021f83b2e849953f1d5b1e378186405d7dd4d5fb555866";
    private const string Pack006ProviderInputSha256 =
        "127573c38acfbcaca27dd713a9054cd627a02ac8e3469c0c538f9e1f6b3e86fd";
    private const string ProviderInputPlanSha256 =
        "a792fb0f03b2c6c16facecba5485ed48dc7742610f7542e0936ca88ecd59243b";
    private const string SourceSha256 =
        "a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61";
    private const string SourceUniverseSha256 =
        "2a953bf785ed1af00bc908ff9e5d6a1d988b04c0d980ecd95336bc5a9702f46f";
    private const string GoldSha256 =
        "e0001e940bc71c78d0dc2c8df44434f49421ff97679f1f968b192e98a05dd66e";
    private const string Model = "qwen/qwen3.7-flash";
    private const int MaxOutputTokens = 32768;
    private const int RequiredContinuationCalls = 2;

    private static readonly IReadOnlyDictionary<string, (int Expected, int MaxTokens, string Input)> Authority =
        new Dictionary<string, (int, int, string)>(StringComparer.Ordinal)
        {
            ["PACK_001"] = (38, 4960, Pack001ProviderInputSha256),
            ["PACK_005"] = (119, 15328, Pack005ProviderInputSha256),
            ["PACK_006"] = (57, 7392, Pack006ProviderInputSha256),
        };

    [Fact]
    public void Continuation_gates_derive_exactly_two_missing_cells_without_contacting_provider()
    {
        var gates = VerifyGates(out _, out _, out var required);
        Assert.Equal(
            ["r3:PACK_005", "r3:PACK_006"],
            required.Select(cell => cell.Identity).ToArray());
        Assert.All(gates, line => Assert.DoesNotContain("MISMATCH", line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_the_authorized_two_cell_continuation()
    {
        if (Environment.GetEnvironmentVariable(RunVariable) is not ("1" or "true"))
            return;

        var gates = VerifyGates(out var contexts, out _, out var required);
        Assert.All(gates, line => Assert.DoesNotContain("MISMATCH", line, StringComparison.Ordinal));
        Assert.Equal(AuthorizedCommit, Head());
        Assert.False(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")));

        var priorFiles = PriorFiles();
        Assert.Equal(9, priorFiles.Count(path => path.Contains("capture-slot", StringComparison.Ordinal)));
        var priorContents = priorFiles.ToDictionary(path => path, File.ReadAllText, StringComparer.Ordinal);
        var reservations = ReserveFreshSlots(contexts, required);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var provider = new OpenRouterHeaderExtractor(http, new RemoteInferenceOptions
        {
            ApiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")!,
            Model = Model,
        });
        using var budgeted = new BudgetedClassifier(provider, RequiredContinuationCalls);

        var results = new List<object>();
        string? halted = null;
        foreach (var cell in required)
        {
            if (halted is not null) break;
            var context = contexts[cell.Pack];
            var authority = Authority[cell.Pack];
            budgeted.DocumentId = "DOC-0252";
            budgeted.Repeat = cell.Repeat;
            budgeted.Stage = "direct-semantic-probe-continuation";
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var raw = await budgeted.BoundaryCutAsync(
                    DirectSemanticProbeRetryPreflightTests.RetryProbePrompt,
                    context.Request, CancellationToken.None, authority.Expected);
                stopwatch.Stop();
                var rawSha = CanonicalArtifactHash.OfText(raw);
                using var reply = JsonDocument.Parse(raw);
                var decisions = ReadDecisions(reply.RootElement, context.Items, out var fault);
                WriteCapture(cell, context, authority, raw, rawSha, stopwatch.ElapsedMilliseconds,
                    fault is null ? "CAPTURE_COMPLETE" : "CAPTURE_INVALID", decisions, fault);
                if (fault is not null)
                {
                    halted = $"{cell.Identity}: {fault}. Raw evidence retained.";
                    break;
                }

                results.Add(new { repeat = cell.Repeat, pack = cell.Pack, decisions, rawResponseSha256 = rawSha });
            }
            catch (Exception error)
            {
                stopwatch.Stop();
                halted = $"{cell.Identity}: {error.GetType().Name}: {error.Message}";
                WriteFailure(cell, context, authority, stopwatch.ElapsedMilliseconds, halted);
                break;
            }
        }

        Persist(required, reservations, results, budgeted, gates, halted);
        foreach (var (path, content) in priorContents)
            Assert.Equal(content, File.ReadAllText(path));

        Assert.Null(halted);
        Assert.Equal(RequiredContinuationCalls, budgeted.CallsMade);
        Assert.Equal(RequiredContinuationCalls, results.Count);
    }

    private static IReadOnlyList<string> VerifyGates(
        out IReadOnlyDictionary<string, ProbeContext> contexts,
        out PdfStructuredSourceAuthority plan,
        out IReadOnlyList<ContinuationCell> required)
    {
        var lines = new List<string>();
        var sourcePath = TestRepository.Path(Doc0252Pdf);
        plan = PdfStructuredSourceAuthorityBuilder.Build(sourcePath);
        var build = DirectSemanticProbePreflightTests.Build(plan);
        var prompt = DirectSemanticProbeRetryPreflightTests.RetryProbePrompt;
        var packs = DirectSemanticProbePreflightTests.ComposeAllPacks(plan);
        contexts = build.Items.GroupBy(item => item.PackId, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key.Split(':')[1],
                group => new ProbeContext(group.Key,
                    group.OrderBy(item => item.Identity, StringComparer.Ordinal).ToArray(),
                    DirectSemanticProbePreflightTests.ComposeRequest(
                        packs[group.Key], group.OrderBy(item => item.Identity, StringComparer.Ordinal).ToArray())),
                StringComparer.Ordinal);

        var prior = ReadPriorLedger();
        required = DeriveRequiredCells(prior);
        lines.Add(File.Exists(Path.Combine(TestRepository.Path(OutputRoot), "direct-semantic-probe-continuation-run.v1.json"))
            ? $"headPostRun: MATCH current={Head()}"
            : Check("head", AuthorizedCommit, Head()));
        lines.Add(Check("sourceHash", SourceSha256, CanonicalArtifactHash.OfBytes(sourcePath)));
        lines.Add(Check("sourceUniverse", SourceUniverseSha256, plan.SourceUniverseSha256));
        lines.Add(Check("goldHash", GoldSha256, CanonicalGoldRegistry.EntryAt(HistoricalGoldVintages.Doc0252R1Path, HistoricalGoldVintages.Doc0252R1Sha256).GoldSha256));
        lines.Add(Check("schemaHash", SchemaSha256, CanonicalHash(
            DirectSemanticProbePreflightTests.ProbeSchema(
                build.Items.Select(item => item.ItemId).ToArray()))));
        lines.Add(Check("promptHash", PromptSha256, CanonicalArtifactHash.OfText(prompt)));
        lines.Add(Check("priorSuccessfulCells", "7", prior.Successful.Count.ToString()));
        lines.Add(Check("priorFailedCells", "1", prior.Failed.Count.ToString()));
        lines.Add(Check("priorAttemptedCells", "8", prior.Attempted.Count.ToString()));
        lines.Add(Check("requiredContinuationCells", "2", required.Count.ToString()));
        lines.Add(Check("requiredIdentities", "r3:PACK_005,r3:PACK_006",
            string.Join(',', required.Select(cell => cell.Identity))));
        lines.Add(Check("priorReservedSlots", "9", prior.Reserved.Count.ToString()));
        var continuationRoot = TestRepository.Path(OutputRoot);
        lines.Add(Directory.Exists(continuationRoot)
            ? Check("continuationPostRunMaterialization", "true",
                ContinuationRootIsComplete().ToString().ToLowerInvariant())
            : "continuationPreRunFresh: MATCH expected=absent actual=absent");

        foreach (var cell in required)
        {
            var context = contexts[cell.Pack];
            var authority = Authority[cell.Pack];
            var input = JsonSerializer.Serialize(new { systemPrompt = prompt, userMessage = context.Request });
            lines.Add(Check($"{cell.Identity}:providerInput", authority.Input,
                SemanticAuthorityTransportCall.Sha256Utf8(input)));
            var owned = DirectSemanticProbePreflightTests.PacketAliases(packs[context.PackId]).Count;
            var maxTokens = OpenRouterHeaderExtractor.BoundaryOutputBudgetFor(
                context.Request, owned, MaxOutputTokens);
            lines.Add(Check($"{cell.Identity}:expectedItemCount", authority.Expected.ToString(), owned.ToString()));
            lines.Add(Check($"{cell.Identity}:maxTokens", authority.MaxTokens.ToString(), maxTokens.ToString()));
            lines.Add(Check($"{cell.Identity}:compatibility", "true",
                TransportCompatibility.Validate(prompt, context.Request,
                    TransportCompatibility.JsonObjectResponseFormat).IsCompatible.ToString().ToLowerInvariant()));
        }

        var allContexts = contexts.Values.OrderBy(context => context.PackId, StringComparer.Ordinal);
        var planHash = CanonicalSemanticRequestComposer.Hash(string.Join("\u0000", allContexts.Select(context =>
            SemanticAuthorityTransportCall.Sha256Utf8(JsonSerializer.Serialize(
                new { systemPrompt = prompt, userMessage = context.Request })))));
        lines.Add(Check("providerInputPlan", ProviderInputPlanSha256, planHash));
        lines.Add(Check("continuationCalls", "2", RequiredContinuationCalls.ToString()));
        lines.Add(Check("pack001Calls", "0", required.Count(cell => cell.Pack == "PACK_001").ToString()));
        lines.Add(Check("replacementCalls", "0", "0"));
        lines.Add(Check("safetyMarginCalls", "0", "0"));
        lines.Add(Check("secondModelReplay", "0", "0"));
        lines.Add(Check("stage1TransportCalls", "0", "0"));
        lines.Add(Check("stage2Calls", "0", "0"));
        return lines;
    }

    private static PriorLedger ReadPriorLedger()
    {
        var root = TestRepository.Path(PriorRoot);
        var run = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "direct-semantic-probe-retry-run.v1.json"))).RootElement;
        var successful = run.GetProperty("results").EnumerateArray()
            .Select(item => new ContinuationCell(item.GetProperty("repeat").GetInt32(),
                item.GetProperty("pack").GetString()!)).ToHashSet();
        var failed = Directory.GetFiles(root, "*failure.v1.json", SearchOption.AllDirectories)
            .Select(path => JsonDocument.Parse(File.ReadAllText(path)).RootElement)
            .Select(item => new ContinuationCell(item.GetProperty("repeat").GetInt32(),
                item.GetProperty("packId").GetString()!.Split(':')[1])).ToHashSet();
        var reserved = Directory.GetFiles(root, "*capture-slot.v1.json", SearchOption.AllDirectories)
            .Select(path => new ContinuationCell(
                int.Parse(new DirectoryInfo(Path.GetDirectoryName(path)!).Name[1..]),
                Path.GetFileName(path).Split('.')[0])).ToHashSet();
        return new PriorLedger(successful, failed, successful.Union(failed).ToHashSet(), reserved);
    }

    private static IReadOnlyList<ContinuationCell> DeriveRequiredCells(PriorLedger prior)
    {
        var all = Enumerable.Range(1, 3).SelectMany(repeat => Authority.Keys
                .Order(StringComparer.Ordinal)
                .Select(pack => new ContinuationCell(repeat, pack)))
            .ToHashSet();
        return prior.Failed.Union(all.Except(prior.Attempted))
            .OrderBy(cell => cell.Repeat).ThenBy(cell => cell.Pack, StringComparer.Ordinal).ToArray();
    }

    private static List<string> PriorFiles() =>
        Directory.GetFiles(TestRepository.Path(PriorRoot), "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).ToList();

    private static bool ContinuationRootIsComplete()
    {
        var root = TestRepository.Path(OutputRoot);
        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => path[(root.Length + 1)..].Replace('\\', '/'))
            .Order(StringComparer.Ordinal).ToArray();
        var expected = new[]
        {
            "direct-semantic-probe-continuation-run.v1.json",
            "r3/PACK_005.capture-slot.v1.json",
            "r3/PACK_005.continuation-capture.v1.json",
            "r3/PACK_006.capture-slot.v1.json",
            "r3/PACK_006.continuation-capture.v1.json",
        };
        if (!expected.SequenceEqual(files, StringComparer.Ordinal)) return false;

        using var run = JsonDocument.Parse(File.ReadAllText(Path.Combine(root,
            "direct-semantic-probe-continuation-run.v1.json")));
        var runRoot = run.RootElement;
        if (runRoot.GetProperty("actualProviderCalls").GetInt32() != 2 ||
            runRoot.GetProperty("successfulContinuationCells").GetInt32() != 2 ||
            runRoot.GetProperty("replacementCalls").GetInt32() != 0 ||
            runRoot.GetProperty("safetyMarginCalls").GetInt32() != 0 ||
            runRoot.GetProperty("secondModelReplay").GetInt32() != 0 ||
            runRoot.GetProperty("stage1TransportCalls").GetInt32() != 0 ||
            runRoot.GetProperty("stage2Calls").GetInt32() != 0 ||
            runRoot.GetProperty("placementCalls").GetInt32() != 0)
            return false;

        foreach (var path in new[]
        {
            Path.Combine(root, "r3", "PACK_005.continuation-capture.v1.json"),
            Path.Combine(root, "r3", "PACK_006.continuation-capture.v1.json"),
        })
        {
            using var capture = JsonDocument.Parse(File.ReadAllText(path));
            var value = capture.RootElement;
            if (value.GetProperty("status").GetString() != "CAPTURE_COMPLETE" ||
                !value.GetProperty("rawResponseCaptured").GetBoolean())
                return false;
            var body = Convert.FromBase64String(value.GetProperty("rawResponseUtf8Base64").GetString()!);
            var hash = Convert.ToHexStringLower(SHA256.HashData(body));
            if (!string.Equals(hash, value.GetProperty("rawResponseSha256").GetString(), StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static List<object> ReserveFreshSlots(
        IReadOnlyDictionary<string, ProbeContext> contexts, IReadOnlyList<ContinuationCell> required)
    {
        var result = new List<object>();
        foreach (var cell in required)
        {
            var directory = Path.Combine(TestRepository.Path(OutputRoot), $"r{cell.Repeat}");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{cell.Pack}.capture-slot.v1.json");
            if (File.Exists(path))
                throw new InvalidOperationException($"CONTINUATION_CAPTURE_SLOT_ALREADY_EXISTS:{cell.Identity}");
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
            writer.Write(JsonSerializer.Serialize(new
            {
                schemaVersion = "a99-direct-semantic-probe-continuation-capture-reservation-v1",
                status = "CAPTURE_SLOT_RESERVED",
                lineage = "direct-semantic-discrimination-probe-continuation-v1",
                repeat = cell.Repeat,
                packId = contexts[cell.Pack].PackId,
                promptSha256 = PromptSha256,
                schemaSha256 = SchemaSha256,
                providerInputSha256 = Authority[cell.Pack].Input,
            }, FreezeArtifact.Json).ReplaceLineEndings("\n"));
            writer.Flush();
            stream.Flush(flushToDisk: true);
            result.Add(new { identity = cell.Identity, slot = Path.GetFileName(path) });
        }
        return result;
    }

    private static object[]? ReadDecisions(JsonElement reply,
        DirectSemanticProbePreflightTests.ProbeItem[] items, out string? fault)
    {
        fault = null;
        if (reply.ValueKind != JsonValueKind.Object ||
            !reply.TryGetProperty("decisions", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            fault = "reply carries no decisions array";
            return null;
        }
        var allowed = items.Select(item => item.ItemId).ToHashSet(StringComparer.Ordinal);
        var labels = new[] { "STRUCTURAL_UNIT", "DOCUMENT_LABEL", "NON_STRUCTURAL" };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var decisions = new List<object>();
        foreach (var entry in array.EnumerateArray())
        {
            var id = entry.TryGetProperty("itemId", out var idValue) && idValue.ValueKind == JsonValueKind.String
                ? idValue.GetString() : null;
            var label = entry.TryGetProperty("classification", out var labelValue) &&
                labelValue.ValueKind == JsonValueKind.String ? labelValue.GetString() : null;
            if (id is null || !allowed.Contains(id)) { fault = $"unknown itemId '{id}'"; return null; }
            if (label is null || !labels.Contains(label, StringComparer.Ordinal))
            { fault = $"classification '{label}' is outside the closed set"; return null; }
            if (!seen.Add(id)) { fault = $"itemId '{id}' answered twice"; return null; }
            decisions.Add(new { itemId = id, classification = label });
        }
        var missing = allowed.Except(seen, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length > 0) { fault = $"{missing.Length} item(s) unanswered"; return null; }
        return [.. decisions];
    }

    private static void WriteCapture(ContinuationCell cell, ProbeContext context,
        (int Expected, int MaxTokens, string Input) authority, string raw, string rawSha,
        long elapsedMs, string status, object[]? decisions, string? fault)
    {
        var directory = Path.Combine(TestRepository.Path(OutputRoot), $"r{cell.Repeat}");
        File.WriteAllText(Path.Combine(directory, $"{cell.Pack}.continuation-capture.v1.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = "a99-direct-semantic-probe-continuation-capture-v1",
                status,
                lineage = "direct-semantic-discrimination-probe-continuation-v1",
                documentId = "DOC-0252",
                repeat = cell.Repeat,
                packId = context.PackId,
                promptSha256 = PromptSha256,
                schemaSha256 = SchemaSha256,
                providerInputSha256 = authority.Input,
                expectedItemCount = authority.Expected,
                maxTokens = authority.MaxTokens,
                model = Model,
                providerRoute = "OpenRouter",
                temperature = 0,
                reasoningEffort = "none",
                responseFormat = "json_object",
                sourceSha256 = SourceSha256,
                sourceUniverseSha256 = SourceUniverseSha256,
                itemIds = context.Items.Select(item => item.ItemId),
                systemPromptUtf8Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    DirectSemanticProbeRetryPreflightTests.RetryProbePrompt)),
                userMessageUtf8Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(context.Request)),
                rawResponseSha256 = rawSha,
                rawResponseBytes = Encoding.UTF8.GetByteCount(raw),
                rawResponseUtf8Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)),
                rawResponseCaptured = true,
                elapsedMs,
                fault,
                decisions,
            }, FreezeArtifact.Json).ReplaceLineEndings("\n"));
    }

    private static void WriteFailure(ContinuationCell cell, ProbeContext context,
        (int Expected, int MaxTokens, string Input) authority, long elapsedMs, string fault)
    {
        var directory = Path.Combine(TestRepository.Path(OutputRoot), $"r{cell.Repeat}");
        File.WriteAllText(Path.Combine(directory, $"{cell.Pack}.continuation-failure.v1.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = "a99-direct-semantic-probe-continuation-failure-v1",
                status = "TRANSPORT_FAILED",
                lineage = "direct-semantic-discrimination-probe-continuation-v1",
                repeat = cell.Repeat,
                packId = context.PackId,
                promptSha256 = PromptSha256,
                providerInputSha256 = authority.Input,
                expectedItemCount = authority.Expected,
                maxTokens = authority.MaxTokens,
                elapsedMs,
                fault,
                rawResponseCaptured = false,
            }, FreezeArtifact.Json).ReplaceLineEndings("\n"));
    }

    private static void Persist(IReadOnlyList<ContinuationCell> required, IReadOnlyList<object> reservations,
        IReadOnlyList<object> results, BudgetedClassifier classifier, IReadOnlyList<string> gates, string? halted)
    {
        File.WriteAllText(Path.Combine(TestRepository.Path(OutputRoot), "direct-semantic-probe-continuation-run.v1.json"),
            JsonSerializer.Serialize(new
            {
                artifactKind = "a99_direct_semantic_probe_continuation_run",
                schemaVersion = "a99-direct-semantic-probe-continuation-run-v1",
                lineage = "direct-semantic-discrimination-probe-continuation-v1",
                supersedesAuthorization = "prior continuation authorization",
                providerAuthorized = true,
                documentId = "DOC-0252",
                promptSha256 = PromptSha256,
                schemaSha256 = SchemaSha256,
                providerInputPlanSha256 = ProviderInputPlanSha256,
                requiredCells = required.Select(cell => cell.Identity),
                proposedProviderCalls = RequiredContinuationCalls,
                actualProviderCalls = classifier.CallsMade,
                successfulContinuationCells = results.Count,
                replacementCalls = 0,
                safetyMarginCalls = 0,
                secondModelReplay = 0,
                stage1TransportCalls = 0,
                stage2Calls = 0,
                placementCalls = 0,
                halted,
                reservations,
                results,
                callLedger = classifier.Ledger.Select(call => new
                {
                    call.Ordinal, call.DocumentId, call.Repeat, call.Stage,
                    call.SystemPromptSha256, call.RequestSha256, call.ResponseSha256,
                    call.RequestChars, call.ResponseChars, call.ElapsedMs,
                }).ToArray(),
                priorLineage = new
                {
                    successfulCells = 7,
                    failedCells = 1,
                    attemptedCells = 8,
                    reservationsPreserved = true,
                },
                gates,
            }, FreezeArtifact.Json).ReplaceLineEndings("\n"));
    }

    private static string Head() => Git("rev-parse HEAD").Trim();

    private static string Git(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = TestRepository.Root(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        process.WaitForExit();
        return process.StandardOutput.ReadToEnd();
    }

    private static string CanonicalHash(object value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(value, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }).ReplaceLineEndings("\n"))));

    private static string Check(string name, string expected, string actual) =>
        $"{name}: {(string.Equals(expected, actual, StringComparison.Ordinal) ? "MATCH" : "MISMATCH")} " +
        $"expected={expected} actual={actual}";

    private sealed record ProbeContext(string PackId,
        DirectSemanticProbePreflightTests.ProbeItem[] Items, string Request);

    private sealed record ContinuationCell(int Repeat, string Pack)
    {
        public string Identity => $"r{Repeat}:{Pack}";
    }

    private sealed record PriorLedger(
        HashSet<ContinuationCell> Successful,
        HashSet<ContinuationCell> Failed,
        HashSet<ContinuationCell> Attempted,
        HashSet<ContinuationCell> Reserved);
}
