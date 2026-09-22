using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// DIRECT_SEMANTIC_DISCRIMINATION_PROBE: nine calls, one question per span.
/// <para>
/// Three contexts, three repeats, and the model is asked only what each already-selected span does
/// in the document. The expected labels exist solely in the offline scorer; nothing in the prompt,
/// schema or packet tells the model which answer is wanted, and the item ids are content hashes
/// that leak nothing.
/// </para>
/// <para>
/// There is no replacement call. If any of the nine fails transport, integrity or capture, the raw
/// evidence is kept and the run stops without issuing the next one.
/// </para>
/// </summary>
public sealed class DirectSemanticProbeTransportTests
{
    private const string RunVariable = "A99_DIRECT_SEMANTIC_PROBE_RUN";
    private const string OutputRoot = "eval/a99-closed-loop/direct-semantic-probe-v1/DOC-0252";
    private const string Doc0252Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";

    private const string ProbeId = "DIRECT_SEMANTIC_DISCRIMINATION_PROBE";
    /// <summary>
    /// The commit this run is authorized against. Exact-HEAD is self-referential once more - the
    /// runner did not exist at df1d9bc - so the established pattern applies: anchor to the base and
    /// constrain the descendant to the runner wiring, with any src/ change blocking outright.
    /// </summary>
    private const string AuthorizedBaseCommit = "df1d9bc56b0515805452d2fa9a0f2f18be76fb14";

    private static readonly string[] AuthorizedDescendantPaths =
    [
        "tests/DocxHeaderExtractor.Tests/DirectSemanticProbeTransportTests.cs",
        "tests/DocxHeaderExtractor.Tests/DirectSemanticProbePreflightTests.cs",
    ];
    private const string SchemaSha256 = "20f937f19ef560380f9ec7f76ad43fa8dec4442dafdd9c404eb6c258e14295b9";
    private const string PromptSha256 = "57797bdced0e9918607cbf0a55239a746303d2445c103c4e8cb27012cd9b2fd8";
    private const string Pack001ProviderInputSha256 =
        "237d0f455589073c1ebb0a7a9ff75da73c59ac1b3525d07129d11c39f1e998c4";
    private const string Pack005ProviderInputSha256 =
        "7c17de36a5465625256d2722c9ce2e9f68f9adbdb10eaf6c5769497eb33c99b5";
    private const string Pack006ProviderInputSha256 =
        "e815f2299583a5e1126ceb794006a347efd50b71f46c34097c3f8316d320669f";
    private const string ProviderInputPlanSha256 =
        "3e3048d63575c4ac6281c5e95480752e3b1144bf52be004de291899bb084c64d";
    private const string SourceSha256 = "a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61";
    private const string SourceUniverseSha256 = "2a953bf785ed1af00bc908ff9e5d6a1d988b04c0d980ecd95336bc5a9702f46f";
    private const string GoldSha256 = "e0001e940bc71c78d0dc2c8df44434f49421ff97679f1f968b192e98a05dd66e";

    private const string Model = "qwen/qwen3.7-flash";
    private const int MaxOutputTokens = 32768;
    private const int Repeats = 3;
    private const int AuthorizedCalls = 9;

    private static readonly Dictionary<string, (int Expected, int MaxTokens, string ProviderInput)> Authority =
        new(StringComparer.Ordinal)
        {
            ["PACK_001"] = (38, 4960, Pack001ProviderInputSha256),
            ["PACK_005"] = (119, 15328, Pack005ProviderInputSha256),
            ["PACK_006"] = (57, 7392, Pack006ProviderInputSha256),
        };

    [Fact]
    public void All_authorization_gates_hold_without_contacting_a_provider()
    {
        var gates = VerifyGates(out _, out _);
        Assert.All(gates, line => Assert.DoesNotContain("MISMATCH", line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_the_direct_semantic_discrimination_probe()
    {
        if (Environment.GetEnvironmentVariable(RunVariable) is not ("1" or "true"))
            return;

        var gates = VerifyGates(out var contexts, out var plan);
        Assert.All(gates, line => Assert.DoesNotContain("MISMATCH", line, StringComparison.Ordinal));

        // Repository state, at the last moment before money is spent.
        var head = Head();
        Assert.True(IsAncestor(AuthorizedBaseCommit, head),
            $"{AuthorizedBaseCommit} is not an ancestor of {head}");
        Assert.True(WorkingTreeClean(), "tracked files are modified");

        var changed = Git($"diff --name-only {AuthorizedBaseCommit}..HEAD")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim()).ToArray();
        Assert.Empty(changed.Where(path => !AuthorizedDescendantPaths.Contains(path, StringComparer.Ordinal)));
        Assert.Empty(changed.Where(path => path.StartsWith("src/", StringComparison.Ordinal)));

        // Nine identities, every one fresh and exclusively reservable, before the first call.
        var reservations = ReserveAll(contexts);
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

                budgeted.DocumentId = "DOC-0252";
                budgeted.Repeat = repeat;
                budgeted.Stage = "direct-semantic-probe";

                var name = context.PackId.Split(':')[1];
                var authority = Authority[name];
                var stopwatch = Stopwatch.StartNew();
                string raw;
                try
                {
                    raw = await budgeted.BoundaryCutAsync(
                        DirectSemanticProbePreflightTests.ProbePrompt, context.Request,
                        CancellationToken.None, expectedItemCount: authority.Expected);
                }
                catch (Exception error)
                {
                    halted = $"r{repeat} {name}: transport failed: {error.GetType().Name}: {error.Message}";
                    break;
                }
                stopwatch.Stop();

                // Bytes first. Everything after this point can fail without losing the evidence.
                var rawSha = CanonicalArtifactHash.OfText(raw);
                WriteCapture(repeat, name, context, authority, raw, rawSha, stopwatch.ElapsedMilliseconds,
                    parseStatus: "PENDING", decisions: null, fault: null);

                JsonDocument? reply = null;
                try
                {
                    reply = JsonDocument.Parse(raw);
                }
                catch (JsonException error)
                {
                    WriteCapture(repeat, name, context, authority, raw, rawSha,
                        stopwatch.ElapsedMilliseconds, "FAILED", null, error.Message);
                    halted = $"r{repeat} {name}: response is not parseable JSON. Raw evidence retained.";
                    break;
                }

                using (reply)
                {
                    var decisions = ReadDecisions(reply.RootElement, context.Items, out var fault);
                    WriteCapture(repeat, name, context, authority, raw, rawSha,
                        stopwatch.ElapsedMilliseconds, fault is null ? "OK" : "INVALID", decisions, fault);
                    if (fault is not null)
                    {
                        halted = $"r{repeat} {name}: {fault}. Raw evidence retained.";
                        break;
                    }
                    results.Add(new { repeat, pack = name, decisions });
                }
            }
        }

        Persist(budgeted, results, gates, halted, reservations);
        Assert.Null(halted);
        Assert.Equal(AuthorizedCalls, budgeted.CallsMade);
    }

    // ---- gates ----------------------------------------------------------------------------------

    private static IReadOnlyList<string> VerifyGates(
        out ProbeContext[] contexts, out PdfStructuredSourceAuthority plan)
    {
        var lines = new List<string>();
        var path = TestRepository.Path(Doc0252Pdf);
        plan = PdfStructuredSourceAuthorityBuilder.Build(path);
        var build = DirectSemanticProbePreflightTests.Build(plan);

        lines.Add(Check("probe", ProbeId, ProbeId));
        lines.Add(Check("sourceHash", SourceSha256, CanonicalArtifactHash.OfBytes(path)));
        lines.Add(Check("sourceUniverse", SourceUniverseSha256, plan.SourceUniverseSha256));
        lines.Add(Check("goldHash", GoldSha256, CanonicalGoldRegistry.Entry("DOC-0252").GoldSha256));
        lines.Add(Check("items", "18", build.Items.Length.ToString()));

        var prompt = DirectSemanticProbePreflightTests.ProbePrompt;
        lines.Add(Check("promptHash", PromptSha256, CanonicalArtifactHash.OfText(prompt)));

        var byPack = build.Items.GroupBy(item => item.PackId, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal).ToArray();
        lines.Add(Check("contexts", "3", byPack.Length.ToString()));

        contexts = byPack.Select(group =>
        {
            var items = group.OrderBy(item => item.Identity, StringComparer.Ordinal).ToArray();
            var request = DirectSemanticProbePreflightTests.ComposeRequest(build.Packs[group.Key], items);
            return new ProbeContext(group.Key, items, request);
        }).ToArray();

        // Model-input authority, and the schema shown for the whole item set.
        lines.Add(Check("schemaHash", SchemaSha256, CanonicalHash(
            DirectSemanticProbePreflightTests.ProbeSchema(
                build.Items.Select(item => item.ItemId).ToArray()))));

        foreach (var context in contexts)
        {
            var name = context.PackId.Split(':')[1];
            var authority = Authority[name];
            var providerInput = SemanticAuthorityTransportCall.Sha256Utf8(JsonSerializer.Serialize(
                new { systemPrompt = prompt, userMessage = context.Request }));
            lines.Add(Check($"{name}:providerInput", authority.ProviderInput, providerInput));

            var owned = DirectSemanticProbePreflightTests.PacketAliases(build.Packs[context.PackId]).Count;
            var budget = OpenRouterHeaderExtractor.BoundaryOutputBudgetFor(
                context.Request, owned, MaxOutputTokens);
            lines.Add(Check($"{name}:expectedItemCount", authority.Expected.ToString(), owned.ToString()));
            lines.Add(Check($"{name}:maxTokens", authority.MaxTokens.ToString(), budget.ToString()));
        }

        lines.Add(Check("providerInputPlan", ProviderInputPlanSha256,
            CanonicalSemanticRequestComposer.Hash(string.Join("\u0000", contexts.Select(context =>
                SemanticAuthorityTransportCall.Sha256Utf8(JsonSerializer.Serialize(
                    new { systemPrompt = prompt, userMessage = context.Request })))))));

        // The expected labels are the scorer's and must not be anywhere the model can see.
        var surface = prompt + string.Join("", contexts.Select(context => context.Request));
        foreach (var leak in new[] { "NON_STRUCTURAL\":", "expectedLabel", "masthead", "DocumentTitle" })
            lines.Add(Check($"noLeak:{leak}", "absent",
                surface.Contains(leak, StringComparison.OrdinalIgnoreCase) ? "present" : "absent"));

        lines.Add(Check("repeats", Repeats.ToString(), Repeats.ToString()));
        lines.Add(Check("authorizedCalls", AuthorizedCalls.ToString(),
            (contexts.Length * Repeats).ToString()));
        lines.Add(Check("replacementCallsPermitted", "0", "0"));
        lines.Add(Check("model", Model, Model));
        return lines;
    }

    /// <summary>
    /// Reserves all nine slots exclusively before the first call. Reserving up front rather than
    /// per call means a run cannot get halfway in and then discover it was never able to record
    /// what it was about to buy.
    /// </summary>
    private static List<object> ReserveAll(ProbeContext[] contexts)
    {
        var reservations = new List<object>();
        for (var repeat = 1; repeat <= Repeats; repeat++)
        {
            foreach (var context in contexts)
            {
                var name = context.PackId.Split(':')[1];
                var directory = Path.Combine(TestRepository.Path(OutputRoot), $"r{repeat}");
                Directory.CreateDirectory(directory);
                var slot = Path.Combine(directory, $"{name}.capture-slot.v1.json");

                using (var handle = new FileStream(slot, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(handle))
                {
                    writer.Write(JsonSerializer.Serialize(new
                    {
                        schemaVersion = "a99-direct-semantic-probe-capture-reservation-v1",
                        status = "CAPTURE_SLOT_RESERVED",
                        probeId = ProbeId,
                        repeat,
                        packId = context.PackId,
                        items = context.Items.Length,
                        promptSha256 = PromptSha256,
                        schemaSha256 = SchemaSha256,
                        providerInputSha256 = Authority[name].ProviderInput,
                    }, FreezeArtifact.Json).ReplaceLineEndings("\n"));
                }
                reservations.Add(new { identity = $"{name}:r{repeat}", slot = Path.GetFileName(slot) });
            }
        }
        return reservations;
    }

    // ---- reading a reply ---------------------------------------------------------------------

    /// <summary>
    /// Reads the decisions, refusing anything the contract did not offer. A reply that answered a
    /// different item, or with a label outside the closed set, is an instrument failure and must
    /// not be quietly dropped into a score.
    /// </summary>
    private static object[]? ReadDecisions(
        JsonElement reply, DirectSemanticProbePreflightTests.ProbeItem[] items, out string? fault)
    {
        fault = null;
        if (reply.ValueKind != JsonValueKind.Object
            || !reply.TryGetProperty("decisions", out var array)
            || array.ValueKind != JsonValueKind.Array)
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
            var itemId = entry.TryGetProperty("itemId", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()! : null;
            var label = entry.TryGetProperty("classification", out var value)
                && value.ValueKind == JsonValueKind.String ? value.GetString()! : null;

            if (itemId is null || !allowed.Contains(itemId)) { fault = $"unknown itemId '{itemId}'"; return null; }
            if (label is null || !labels.Contains(label, StringComparer.Ordinal))
            { fault = $"classification '{label}' is outside the closed set"; return null; }
            if (!seen.Add(itemId)) { fault = $"itemId '{itemId}' answered twice"; return null; }

            decisions.Add(new
            {
                itemId,
                classification = label,
                reasonCode = entry.TryGetProperty("reasonCode", out var reason)
                    && reason.ValueKind == JsonValueKind.String ? reason.GetString() : null,
            });
        }

        var missing = allowed.Except(seen, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length > 0) { fault = $"{missing.Length} item(s) unanswered: {string.Join(",", missing)}"; return null; }
        return [.. decisions];
    }

    // ---- capture --------------------------------------------------------------------------------

    private static void WriteCapture(
        int repeat, string name, ProbeContext context,
        (int Expected, int MaxTokens, string ProviderInput) authority,
        string raw, string rawSha, long elapsedMs, string parseStatus, object[]? decisions, string? fault)
    {
        var directory = Path.Combine(TestRepository.Path(OutputRoot), $"r{repeat}");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, $"{name}.direct-semantic-probe-capture.v1.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = "a99-direct-semantic-probe-capture-v1",
                status = parseStatus == "OK" ? "CAPTURE_COMPLETE" : "CAPTURE_RETAINED",
                probeId = ProbeId,
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
                systemPromptUtf8Base64 = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(DirectSemanticProbePreflightTests.ProbePrompt)),
                userMessageUtf8Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(context.Request)),
                rawResponseSha256 = rawSha,
                rawResponseBytes = Encoding.UTF8.GetByteCount(raw),
                rawResponseUtf8Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)),
                rawResponseCaptured = true,
                parseStatus,
                fault,
                decisions,
                elapsedMs,
            }, FreezeArtifact.Json).ReplaceLineEndings("\n"));
    }

    private static void Persist(
        BudgetedClassifier classifier, IReadOnlyList<object> results,
        IReadOnlyList<string> gates, string? halted, IReadOnlyList<object> reservations)
    {
        var directory = TestRepository.Path(OutputRoot);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "direct-semantic-probe-run.v1.json"),
            JsonSerializer.Serialize(new
            {
                artifactKind = "a99_direct_semantic_probe_run",
                schemaVersion = "a99-direct-semantic-probe-run-v1",
                probeId = ProbeId,
                head = Head(),
                authorizedBaseCommit = AuthorizedBaseCommit,
                descendantChangedPaths = Git($"diff --name-only {AuthorizedBaseCommit}..HEAD")
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim()),
                approval = "explicit-user-authorization, DOC-0252, 3 contexts x 3 repeats, "
                    + "exactly 9 calls, no replacement and no safety margin",
                documentId = "DOC-0252",
                diagnosticOnly = true,
                productionRecallGateIntroduced = false,
                promptSha256 = PromptSha256,
                schemaSha256 = SchemaSha256,
                providerInputPlanSha256 = ProviderInputPlanSha256,
                pack001ProviderInputSha256 = Pack001ProviderInputSha256,
                pack005ProviderInputSha256 = Pack005ProviderInputSha256,
                pack006ProviderInputSha256 = Pack006ProviderInputSha256,
                authorizedCalls = AuthorizedCalls,
                callsMade = classifier.CallsMade,
                replacementCalls = 0,
                stage1Calls = 0,
                stage2Calls = 0,
                placementCalls = 0,
                secondModelReplay = false,
                model = Model,
                sourceSha256 = SourceSha256,
                goldSha256 = GoldSha256,
                scoringPerformed = false,
                halted,
                reservations,
                gates,
                results,
                callLedger = classifier.Ledger.Select(call => new
                {
                    call.Ordinal, call.DocumentId, call.Repeat, call.Stage,
                    call.SystemPromptSha256, call.RequestSha256, call.ResponseSha256,
                    call.RequestChars, call.ResponseChars, call.ElapsedMs,
                }).ToArray(),
            }, FreezeArtifact.Json).ReplaceLineEndings("\n"));
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static string CanonicalHash(object value) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(value, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }).ReplaceLineEndings("\n"))));

    private static string Head() => Git("rev-parse HEAD");

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

    private static bool WorkingTreeClean() => Git("status --porcelain --untracked-files=no").Length == 0;

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

    private static string Check(string name, string expected, string actual) =>
        $"{name}: {(string.Equals(expected, actual, StringComparison.Ordinal) ? "MATCH" : "MISMATCH")} " +
        $"expected={expected} actual={actual}";

    internal sealed record ProbeContext(
        string PackId, DirectSemanticProbePreflightTests.ProbeItem[] Items, string Request);
}
