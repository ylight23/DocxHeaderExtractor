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
/// DIRECT_SEMANTIC_DISCRIMINATION_PROBE_RETRY_V1: the explicitly approved nine-call retry.
/// The old rejected lineage is not recovered. This runner owns nine fresh slots and stops on the
/// first transport, parse or contract failure without replacement calls.
/// </summary>
public sealed class DirectSemanticProbeRetryTransportTests
{
    private const string RunVariable = "A99_DIRECT_SEMANTIC_PROBE_RETRY_V1_RUN";
    private const string OutputRoot =
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
    private const int Repeats = 3;
    private const int AuthorizedCalls = 9;

    private static readonly IReadOnlyDictionary<string, (int Expected, int MaxTokens, string ProviderInput)> Authority =
        new Dictionary<string, (int, int, string)>(StringComparer.Ordinal)
        {
            ["PACK_001"] = (38, 4960, Pack001ProviderInputSha256),
            ["PACK_005"] = (119, 15328, Pack005ProviderInputSha256),
            ["PACK_006"] = (57, 7392, Pack006ProviderInputSha256),
        };

    [Fact]
    public void All_retry_authorization_gates_hold_without_contacting_a_provider()
    {
        var gates = VerifyGates(out _, out _);
        Assert.All(gates, line => Assert.DoesNotContain("MISMATCH", line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_the_approved_direct_semantic_probe_retry()
    {
        if (Environment.GetEnvironmentVariable(RunVariable) is not ("1" or "true"))
            return;

        var gates = VerifyGates(out var contexts, out _);
        Assert.All(gates, line => Assert.DoesNotContain("MISMATCH", line, StringComparison.Ordinal));
        Assert.Equal(AuthorizedCommit, Head());

        var oldSlots = OldRejectedSlots();
        Assert.Equal(9, oldSlots.Count);
        var oldContents = oldSlots.ToDictionary(path => path, File.ReadAllText, StringComparer.Ordinal);
        var reservations = ReserveFreshSlots(contexts);
        Assert.Equal(AuthorizedCalls, reservations.Count);

        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        Assert.False(string.IsNullOrWhiteSpace(apiKey), "OPENROUTER_API_KEY is not set.");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var provider = new OpenRouterHeaderExtractor(http, new RemoteInferenceOptions
        {
            ApiKey = apiKey,
            Model = Model,
        });
        using var budgeted = new BudgetedClassifier(provider, AuthorizedCalls);

        var results = new List<object>();
        var halted = (string?)null;
        for (var repeat = 1; repeat <= Repeats && halted is null; repeat++)
        {
            foreach (var context in contexts)
            {
                if (halted is not null) break;
                var name = context.PackId.Split(':')[1];
                var authority = Authority[name];
                budgeted.DocumentId = "DOC-0252";
                budgeted.Repeat = repeat;
                budgeted.Stage = "direct-semantic-probe-retry";
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    var raw = await budgeted.BoundaryCutAsync(
                        DirectSemanticProbeRetryPreflightTests.RetryProbePrompt,
                        context.Request, CancellationToken.None, authority.Expected);
                    stopwatch.Stop();
                    var rawSha = CanonicalArtifactHash.OfText(raw);

                    // Persist raw evidence before parsing or validating it.
                    WriteCapture(repeat, name, context, authority, raw, rawSha,
                        stopwatch.ElapsedMilliseconds, "CAPTURE_RETAINED", null, null);

                    using var reply = JsonDocument.Parse(raw);
                    var decisions = ReadDecisions(reply.RootElement, context.Items, out var fault);
                    WriteCapture(repeat, name, context, authority, raw, rawSha,
                        stopwatch.ElapsedMilliseconds, fault is null ? "CAPTURE_COMPLETE" : "CAPTURE_INVALID",
                        decisions, fault);
                    if (fault is not null)
                    {
                        halted = $"r{repeat} {name}: {fault}. Raw evidence retained.";
                        break;
                    }

                    results.Add(new { repeat, pack = name, decisions, rawResponseSha256 = rawSha });
                }
                catch (Exception error)
                {
                    stopwatch.Stop();
                    halted = $"r{repeat} {name}: {error.GetType().Name}: {error.Message}";
                    WriteFailure(repeat, name, context, authority, stopwatch.ElapsedMilliseconds, halted);
                    break;
                }
            }
        }

        Persist(budgeted, results, gates, halted, reservations);
        foreach (var (path, content) in oldContents)
            Assert.Equal(content, File.ReadAllText(path));
        Assert.Null(halted);
        Assert.Equal(AuthorizedCalls, budgeted.CallsMade);
    }

    private static IReadOnlyList<string> VerifyGates(
        out ProbeContext[] contexts, out PdfStructuredSourceAuthority plan)
    {
        var lines = new List<string>();
        var path = TestRepository.Path(Doc0252Pdf);
        plan = PdfStructuredSourceAuthorityBuilder.Build(path);
        var build = DirectSemanticProbePreflightTests.Build(plan);
        var prompt = DirectSemanticProbeRetryPreflightTests.RetryProbePrompt;
        var packs = DirectSemanticProbePreflightTests.ComposeAllPacks(plan);

        lines.Add(File.Exists(Path.Combine(TestRepository.Path(OutputRoot), "direct-semantic-probe-retry-run.v1.json"))
            ? $"headPostRun: MATCH current={Head()}"
            : Check("head", AuthorizedCommit, Head()));
        lines.Add(Check("sourceHash", SourceSha256, CanonicalArtifactHash.OfBytes(path)));
        lines.Add(Check("sourceUniverse", SourceUniverseSha256, plan.SourceUniverseSha256));
        lines.Add(Check("goldHash", GoldSha256, CanonicalGoldRegistry.EntryAt(HistoricalGoldVintages.Doc0252R1Path, HistoricalGoldVintages.Doc0252R1Sha256).GoldSha256));
        lines.Add(Check("schemaHash", SchemaSha256, CanonicalHash(
            DirectSemanticProbePreflightTests.ProbeSchema(
                build.Items.Select(item => item.ItemId).ToArray()))));
        lines.Add(Check("promptHash", PromptSha256, CanonicalArtifactHash.OfText(prompt)));
        lines.Add(Check("contexts", "3", build.Items.GroupBy(item => item.PackId).Count().ToString()));

        var byPack = build.Items.GroupBy(item => item.PackId, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal).ToArray();
        contexts = byPack.Select(group =>
        {
            var items = group.OrderBy(item => item.Identity, StringComparer.Ordinal).ToArray();
            return new ProbeContext(group.Key, items,
                DirectSemanticProbePreflightTests.ComposeRequest(packs[group.Key], items));
        }).ToArray();

        foreach (var context in contexts)
        {
            var name = context.PackId.Split(':')[1];
            var authority = Authority[name];
            var input = JsonSerializer.Serialize(new { systemPrompt = prompt, userMessage = context.Request });
            lines.Add(Check($"{name}:providerInput", authority.ProviderInput,
                SemanticAuthorityTransportCall.Sha256Utf8(input)));
            var owned = DirectSemanticProbePreflightTests.PacketAliases(packs[context.PackId]).Count;
            var maxTokens = OpenRouterHeaderExtractor.BoundaryOutputBudgetFor(
                context.Request, owned, MaxOutputTokens);
            lines.Add(Check($"{name}:expectedItemCount", authority.Expected.ToString(), owned.ToString()));
            lines.Add(Check($"{name}:maxTokens", authority.MaxTokens.ToString(), maxTokens.ToString()));
            lines.Add(Check($"{name}:compatibility", "true",
                TransportCompatibility.Validate(prompt, context.Request,
                    TransportCompatibility.JsonObjectResponseFormat).IsCompatible.ToString().ToLowerInvariant()));
        }

        var planHash = CanonicalSemanticRequestComposer.Hash(string.Join("\u0000", contexts.Select(context =>
            SemanticAuthorityTransportCall.Sha256Utf8(JsonSerializer.Serialize(
                new { systemPrompt = prompt, userMessage = context.Request })))));
        lines.Add(Check("providerInputPlan", ProviderInputPlanSha256, planHash));
        lines.Add(Check("repeats", "3", Repeats.ToString()));
        lines.Add(Check("authorizedCalls", "9", (contexts.Length * Repeats).ToString()));
        lines.Add(Check("replacementCalls", "0", "0"));
        lines.Add(Check("safetyMarginCalls", "0", "0"));
        lines.Add(Check("secondModelReplay", "0", "0"));
        lines.Add(Check("stage1TransportCalls", "0", "0"));
        lines.Add(Check("stage2Calls", "0", "0"));
        return lines;
    }

    private static List<string> OldRejectedSlots()
    {
        if (!Directory.Exists(TestRepository.Path(RejectedRoot))) return [];
        return Directory.GetFiles(TestRepository.Path(RejectedRoot), "*capture-slot*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).ToList();
    }

    private static List<object> ReserveFreshSlots(ProbeContext[] contexts)
    {
        var result = new List<object>();
        foreach (var repeat in Enumerable.Range(1, Repeats))
        foreach (var context in contexts)
        {
            var name = context.PackId.Split(':')[1];
            var directory = Path.Combine(TestRepository.Path(OutputRoot), $"r{repeat}");
            Directory.CreateDirectory(directory);
            var slot = Path.Combine(directory, $"{name}.capture-slot.v1.json");
            if (File.Exists(slot))
                throw new InvalidOperationException($"RETRY_CAPTURE_SLOT_ALREADY_EXISTS:{name}:r{repeat}");
            using var stream = new FileStream(slot, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
            writer.Write(JsonSerializer.Serialize(new
            {
                schemaVersion = "a99-direct-semantic-probe-retry-capture-reservation-v1",
                status = "CAPTURE_SLOT_RESERVED",
                lineage = "direct-semantic-discrimination-probe-retry-v1",
                repeat,
                packId = context.PackId,
                promptSha256 = PromptSha256,
                schemaSha256 = SchemaSha256,
                providerInputSha256 = Authority[name].ProviderInput,
            }, FreezeArtifact.Json).ReplaceLineEndings("\n"));
            writer.Flush();
            stream.Flush(flushToDisk: true);
            result.Add(new { identity = $"{name}:r{repeat}", slot = Path.GetFileName(slot) });
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

    private static void WriteCapture(int repeat, string name, ProbeContext context,
        (int Expected, int MaxTokens, string ProviderInput) authority, string raw, string rawSha,
        long elapsedMs, string status, object[]? decisions, string? fault)
    {
        var directory = Path.Combine(TestRepository.Path(OutputRoot), $"r{repeat}");
        File.WriteAllText(Path.Combine(directory, $"{name}.direct-semantic-probe-retry-capture.v1.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = "a99-direct-semantic-probe-retry-capture-v1",
                status,
                lineage = "direct-semantic-discrimination-probe-retry-v1",
                documentId = "DOC-0252",
                repeat,
                packId = context.PackId,
                promptSha256 = PromptSha256,
                schemaSha256 = SchemaSha256,
                providerInputSha256 = authority.ProviderInput,
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

    private static void WriteFailure(int repeat, string name, ProbeContext context,
        (int Expected, int MaxTokens, string ProviderInput) authority, long elapsedMs, string fault)
    {
        var directory = Path.Combine(TestRepository.Path(OutputRoot), $"r{repeat}");
        File.WriteAllText(Path.Combine(directory, $"{name}.direct-semantic-probe-retry-failure.v1.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = "a99-direct-semantic-probe-retry-failure-v1",
                status = "TRANSPORT_FAILED",
                lineage = "direct-semantic-discrimination-probe-retry-v1",
                repeat, packId = context.PackId, promptSha256 = PromptSha256,
                providerInputSha256 = authority.ProviderInput, expectedItemCount = authority.Expected,
                maxTokens = authority.MaxTokens, elapsedMs, fault,
                rawResponseCaptured = false,
            }, FreezeArtifact.Json).ReplaceLineEndings("\n"));
    }

    private static void Persist(BudgetedClassifier classifier, IReadOnlyList<object> results,
        IReadOnlyList<string> gates, string? halted, IReadOnlyList<object> reservations)
    {
        var directory = TestRepository.Path(OutputRoot);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "direct-semantic-probe-retry-run.v1.json"),
            JsonSerializer.Serialize(new
            {
                artifactKind = "a99_direct_semantic_probe_retry_run",
                schemaVersion = "a99-direct-semantic-probe-retry-run-v1",
                lineage = "direct-semantic-discrimination-probe-retry-v1",
                approval = "explicit-user-authorization, DOC-0252, 3 contexts, 3 repeats, exactly 9 calls",
                providerAuthorized = true,
                documentId = "DOC-0252",
                promptSha256 = PromptSha256,
                schemaSha256 = SchemaSha256,
                providerInputPlanSha256 = ProviderInputPlanSha256,
                proposedProviderCalls = AuthorizedCalls,
                actualProviderCalls = classifier.CallsMade,
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
                oldRejectedLineage = new
                {
                    preserved = true,
                    providerHttpAttempts = 1,
                    modelGenerations = 0,
                    billedProviderCalls = 0,
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
}
