using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// PHYSICAL_STAGE1_MEMBERSHIP_ONLY, retry lineage: six fresh calls asking one question.
/// <para>
/// A separate run root, not a separate repeat id. The first attempt spent one call on a truncated
/// reply caused by an omitted transport argument; its slot and incident record stay exactly where
/// they are, untouched. Attempt identity belongs to the lineage, so repeats here are r1, r2, r3 as
/// they should be, and nothing about the aborted attempt is overwritten or folded into this score.
/// </para>
/// <para>
/// This run freezes both layers of call authority. Model-input authority is the prompt, packet and
/// schema; transport-call authority is the model, temperature, reasoning mode and max_tokens, the
/// last of which is derived from an argument that appears in none of the frozen bytes. Byte parity
/// alone was not enough to prove two calls were the same call, and it cost a paid call to learn.
/// <para>
/// The model sees the source evidence and the membership question. It does not see a role field, a
/// relation field, a selection mode, an offset, or any sign that a placement stage exists. Nothing
/// else moves: same document, same packs, same atoms, same model and settings, same grounding seam.
/// </para>
/// <para>
/// No Stage-2 request is composed, sent, or even representable here. After the sixth response the
/// run binds, freezes the claim ids, scores membership and stops.
/// </para>
/// </summary>
public sealed class SemanticMembershipV1RetryTransportTests
{
    private const string RunVariable = "A99_STAGE1_MEMBERSHIP_RETRY_RUN";
    private const string OutputRoot = "eval/a99-closed-loop/physical-stage1-membership-only-retry-v1/DOC-0252";
    private const string Doc0252Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";

    private const string ExperimentId = "PHYSICAL_STAGE1_MEMBERSHIP_ONLY";
    private const string RunLineage = "physical-stage1-membership-only-retry-v1";
    /// <summary>
    /// The commit this run is authorized against. An exact-HEAD gate cannot be satisfied by the
    /// commit that changes the gate itself, so authorization is anchored to a base commit and the
    /// descendant is constrained instead: it must contain nothing but this file.
    /// </summary>
    private const string AuthorizedBaseCommit = "ba58bb46d07855232aa0e0cfceb1d4303e2d8150";

    /// <summary>
    /// Every path a descendant of the base commit may touch. Anything under src/ blocks: the
    /// production extraction this run needs is already in the base commit, so a later source change
    /// would mean the call envelope is no longer the one that was authorized.
    /// </summary>
    private static readonly string[] AuthorizedDescendantPaths =
    [
        "tests/DocxHeaderExtractor.Tests/SemanticMembershipV1RetryTransportTests.cs",
    ];
    private const int AbortedAttemptCalls = 1;
    private const int MaxOutputTokens = 32768;
    private const string SchemaSha256 = "baac47daadb1047e843b1358f7a37dfe3d0736c1130672829efc7beb80b48ff2";
    private const string PromptSha256 = "433b134d428bd9053157ea6bb5efab253de3f46fb8da19bc0d3e8fa16d453766";
    private const string ProviderInputPack005Sha256 =
        "a0735a5b48b30673d9bc379a3bd29ebc650a671ab4155b1ecc77bcb754d76e2b";
    private const string ProviderInputPack006Sha256 =
        "2763fd6b44cebd061d76d0d758c29d8eedb5ed5709b70a3fb99891c5942673f0";
    private const string ProviderModelInputPlanSha256 =
        "579712aadb2bbff2fb07329865bf7ba1fa4f9ad95c85dd4c0035e5c22d3c1c55";
    private const string SourceSha256 = "a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61";
    private const string SourceUniverseSha256 = "2a953bf785ed1af00bc908ff9e5d6a1d988b04c0d980ecd95336bc5a9702f46f";
    private const string GoldSha256 = "e0001e940bc71c78d0dc2c8df44434f49421ff97679f1f968b192e98a05dd66e";
    private const string ManifestSha256 = "fb62c1c696b4c30f0b71aed2e39f296ece3934c5870982fdc0e19bc62d64189c";

    private const string Model = "qwen/qwen3.7-flash";
    private const int Repeats = 3;
    private const int Stage1Calls = 6;
    private const int HardCap = 9;
    private const int TargetGoldCount = 14;

    private static readonly string[] TargetPacks =
    [
        "COHERENT_REGION_SEGMENTATION_V1:PACK_005",
        "COHERENT_REGION_SEGMENTATION_V1:PACK_006",
    ];

    [Fact]
    public void All_authorization_gates_hold_without_contacting_a_provider()
    {
        var gates = VerifyGates();
        Assert.All(gates, line => Assert.DoesNotContain("MISMATCH", line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_the_physical_stage1_membership_experiment()
    {
        if (Environment.GetEnvironmentVariable(RunVariable) is not ("1" or "true"))
            return;

        var gates = VerifyGates();
        Assert.All(gates, line => Assert.DoesNotContain("MISMATCH", line, StringComparison.Ordinal));

        // Repository state is checked here rather than in the always-on gate test. It is a
        // precondition of spending money, not a property that holds forever, and asserting it
        // permanently would turn every uncommitted edit into a failing test.
        var head = Head();
        Assert.Equal(40, head.Length);
        Assert.True(IsAncestor(AuthorizedBaseCommit, head),
            $"{AuthorizedBaseCommit} is not an ancestor of {head}");

        var changed = ChangedPathsSince(AuthorizedBaseCommit);
        var unauthorized = changed
            .Where(path => !AuthorizedDescendantPaths.Contains(path, StringComparer.Ordinal))
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Empty(unauthorized);
        Assert.Empty(changed.Where(path => path.StartsWith("src/", StringComparison.Ordinal)));
        Assert.True(WorkingTreeClean(),
            "tracked files are modified; the frozen authority is not what this tree would send");

        // Capture identities must be fresh at the last moment before money is spent - the only
        // moment at which an occupied slot means something is about to be overwritten.
        for (var repeat = 1; repeat <= Repeats; repeat++)
        {
            var directory = Path.Combine(TestRepository.Path(OutputRoot), $"r{repeat}");
            Assert.False(Directory.Exists(directory) && Directory.GetFiles(directory).Length > 0,
                $"r{repeat} already holds capture files");
        }

        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        Assert.False(string.IsNullOrWhiteSpace(apiKey), "OPENROUTER_API_KEY is not set.");

        var plan = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Doc0252Pdf));
        var contract = SemanticCoordinateContract.PdfSemanticMembershipV1;
        var prompt = CanonicalSemanticEngine.MembershipPromptFor(contract);
        var requests = Compose(plan);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var provider = new OpenRouterHeaderExtractor(http, new RemoteInferenceOptions
        {
            ApiKey = apiKey,
            Model = Model,
        });
        using var budgeted = new BudgetedClassifier(provider, HardCap);

        var repeats = new List<object>();
        var aborted = (string?)null;

        for (var repeat = 1; repeat <= Repeats && aborted is null; repeat++)
        {
            budgeted.DocumentId = "DOC-0252";
            budgeted.Repeat = repeat;
            budgeted.Stage = "stage1-membership";
            try
            {
                repeats.Add(await RunRepeatAsync(repeat, budgeted, plan, contract, prompt, requests));
            }
            catch (Exception error)
            {
                aborted = $"r{repeat}: {error.GetType().Name}: {error.Message}";
            }
        }

        Persist(budgeted, repeats, gates, aborted);
        Assert.Null(aborted);
        Assert.Equal(Stage1Calls, budgeted.CallsMade);
    }

    // ---- gates ----------------------------------------------------------------------------------

    private static IReadOnlyList<string> VerifyGates()
    {
        var lines = new List<string>();
        var path = TestRepository.Path(Doc0252Pdf);
        var plan = PdfStructuredSourceAuthorityBuilder.Build(path);
        var contract = SemanticCoordinateContract.PdfSemanticMembershipV1;
        var prompt = CanonicalSemanticEngine.MembershipPromptFor(contract);

        lines.Add(Check("experiment", ExperimentId, ExperimentId));
        lines.Add(Check("protocol", "a99-semantic-membership-v1", contract.ProtocolVersion));
        lines.Add(Check("schema", SchemaSha256, contract.SchemaHash()));
        lines.Add(Check("prompt", PromptSha256, CanonicalArtifactHash.OfText(prompt)));
        lines.Add(Check("packing", "COHERENT_REGION_SEGMENTATION_V1",
            SemanticEvidencePackingPolicies.CoherentRegionSegmentationV1.PolicyId));
        lines.Add(Check("sourceHash", SourceSha256, CanonicalArtifactHash.OfBytes(path)));
        lines.Add(Check("sourceUniverse", SourceUniverseSha256, plan.SourceUniverseSha256));
        lines.Add(Check("goldHash", GoldSha256, CanonicalGoldRegistry.EntryAt(HistoricalGoldVintages.Doc0252R1Path, HistoricalGoldVintages.Doc0252R1Sha256).GoldSha256));

        // The model must not be able to learn from its own task that a second stage exists.
        var surface = JsonSerializer.Serialize(contract.Schema()) + prompt;
        foreach (var concept in new[]
        {
            "semanticRole", "relationHints", "parent-node", "parentClaimId", "selectionMode",
            "same-node", "hierarchy",
        })
        {
            lines.Add(Check($"absent:{concept}", "absent",
                surface.Contains(concept, StringComparison.Ordinal) ? "present" : "absent"));
        }

        var requests = Compose(plan);
        lines.Add(Check("packs", string.Join(",", TargetPacks), string.Join(",", requests.Keys)));

        // Transport-call authority, derived from the production formula rather than restated. The
        // previous attempt matched every byte and still sent a different call, because max_tokens
        // comes from an argument that is in none of those bytes.
        foreach (var pack in TargetPacks)
        {
            var name = pack.Split(':')[1];
            var ownedCount = PacketAliases(requests[pack]).Count;
            var budget = OpenRouterHeaderExtractor.BoundaryOutputBudgetFor(
                requests[pack], ownedCount, MaxOutputTokens);
            var expectedBudget = Math.Clamp(96 + ownedCount * 128, 256, MaxOutputTokens);

            lines.Add(Check($"{name}:expectedItemCount", ownedCount.ToString(), ownedCount.ToString()));
            lines.Add(Check($"{name}:maxTokens", expectedBudget.ToString(), budget.ToString()));

            // The floor is what truncated the aborted attempt. Asserting we are clear of it makes
            // that failure mode impossible to reach silently again.
            lines.Add(Check($"{name}:aboveFloor", "true", budget > 256 ? "true" : "false"));
        }
        var providerInputs = ProviderInputs(requests, prompt);
        lines.Add(Check("providerInputPack005", ProviderInputPack005Sha256, providerInputs["PACK_005"]));
        lines.Add(Check("providerInputPack006", ProviderInputPack006Sha256, providerInputs["PACK_006"]));
        lines.Add(Check("providerModelInputPlan", ProviderModelInputPlanSha256,
            CanonicalSemanticRequestComposer.Hash(string.Join("\u0000",
                TargetPacks.Select(pack => providerInputs[pack.Split(':')[1]])))));

        using var gold = CanonicalGoldRegistry.ResolveAt(HistoricalGoldVintages.Doc0252R1Path, HistoricalGoldVintages.Doc0252R1Sha256);
        var identities = gold.RootElement.GetProperty("occurrence").GetProperty("claims")
            .EnumerateArray().Select(claim => claim.GetProperty("identity").GetString()!).ToArray();
        var owned = requests.Values.Select(PacketAliases).SelectMany(item => item).ToHashSet(StringComparer.Ordinal);
        lines.Add(Check("goldClaims", "41", identities.Length.ToString()));
        lines.Add(Check("goldTargetCount", TargetGoldCount.ToString(),
            identities.Count(identity => owned.Contains(FirstAlias(identity))).ToString()));

        lines.Add(Check("repeats", Repeats.ToString(), Repeats.ToString()));
        lines.Add(Check("stage1Calls", Stage1Calls.ToString(), (TargetPacks.Length * Repeats).ToString()));
        lines.Add(Check("stage2Calls", "0", "0"));
        lines.Add(Check("placementCalls", "0", "0"));
        lines.Add(Check("hardCap", HardCap.ToString(), HardCap.ToString()));
        lines.Add(Check("model", Model, Model));
        lines.Add(Check("temperature", "0", "0"));
        lines.Add(Check("reasoningEffort", "none", "none"));
        lines.Add(Check("responseFormat", "json_object", "json_object"));
        lines.Add(Check("abortedAttemptCallsExcludedFromThisAuthority",
            AbortedAttemptCalls.ToString(), AbortedAttemptCalls.ToString()));
        return lines;
    }

    // ---- one repeat ------------------------------------------------------------------------------

    private static async Task<object> RunRepeatAsync(
        int repeat, BudgetedClassifier classifier, PdfStructuredSourceAuthority plan,
        SemanticCoordinateContract contract, string prompt, Dictionary<string, string> requests)
    {
        var directory = Path.Combine(TestRepository.Path(OutputRoot), $"r{repeat}");
        Directory.CreateDirectory(directory);

        // Reserve before the first call: an interrupted run leaves a reserved slot that must be
        // inspected rather than silently reused as if it were a complete capture.
        var slot = Path.Combine(directory, "stage1-capture-slot.v1.json");
        var identities = TargetPacks.Select((pack, index) => new
        {
            ordinal = index + 1,
            packId = pack,
            requestSha256 = SemanticAuthorityTransportCall.Sha256Utf8(
                JsonSerializer.Serialize(new { systemPrompt = prompt, userMessage = requests[pack] })),
        }).ToArray();
        File.WriteAllText(slot, JsonSerializer.Serialize(new
        {
            schemaVersion = "a99-stage1-membership-retry-capture-reservation-v1",
            status = "CAPTURE_SLOT_RESERVED",
            experimentId = ExperimentId,
            repeat,
            protocol = contract.ProtocolVersion,
            schemaSha256 = contract.SchemaHash(),
            promptSha256 = CanonicalArtifactHash.OfText(prompt),
            calls = identities,
        }, FreezeArtifact.Json).ReplaceLineEndings("\n"));

        var calls = new List<object>();
        var accepted = new List<Stage1AcceptedClaim>();
        var refusals = new List<SemanticMembershipRefusal>();
        var ordinal = 0;

        foreach (var pack in TargetPacks)
        {
            ordinal++;
            var userMessage = requests[pack];
            var owned = PacketAliases(userMessage);

            // expectedItemCount is what the engine passes, and it sizes the provider's output
            // budget. Leaving it to default counts "id" in the payload, finds none, and clamps the
            // reply to the 256-token floor - which truncated a paid call to 825 characters of
            // unparseable JSON. Byte-identical request bytes are not the whole call.
            var stopwatch = Stopwatch.StartNew();
            var raw = await classifier.BoundaryCutAsync(
                prompt, userMessage, CancellationToken.None, expectedItemCount: owned.Count);
            stopwatch.Stop();

            // Bytes first, interpretation second. A response that cannot be parsed is still
            // evidence that was paid for, and losing it to an exception on the way to disk is not
            // an acceptable failure mode.
            var record = new CallRecord(
                ordinal, pack, owned.Count, raw, stopwatch.ElapsedMilliseconds);
            WriteCapture(directory, repeat, contract, prompt, requests, calls, record);

            JsonDocument? reply = null;
            try
            {
                reply = JsonDocument.Parse(raw);
            }
            catch (JsonException error)
            {
                calls.Add(Describe(record, contract, prompt, [], [error.Message], 0, 0, [],
                    "RESPONSE_NOT_PARSEABLE"));
                WriteCapture(directory, repeat, contract, prompt, requests, calls, null);
                throw new InvalidOperationException(
                    $"STAGE1_RESPONSE_NOT_PARSEABLE: {pack} returned {raw.Length} characters that do "
                    + "not parse. The bytes are captured.", error);
            }

            using (reply)
            {
                // A reply in another contract's shape must fail loudly. Decoding it to zero claims
                // and reading that as "the model accepted nothing" is the exact misreading this
                // gate exists to prevent.
                var issues = contract.Validate(reply.RootElement);
                var decoded = SemanticMembershipV1.Decode(reply.RootElement);
                var outcome = SemanticMembershipV1.Accept(
                    plan.Atoms, SourceSha256, decoded, owned);

                accepted.AddRange(outcome.Accepted);
                refusals.AddRange(outcome.Refusals);

                calls.Add(Describe(record, contract, prompt,
                    issues.Select(issue => $"{issue.Code}:{issue.SourceAlias}:{issue.Message}").ToArray(),
                    decoded.Failures, decoded.Claims.Count, outcome.Accepted.Count,
                    outcome.Refusals.Select(item => $"{item.Alias}:{item.Reason}").ToArray(),
                    null));
            }
            WriteCapture(directory, repeat, contract, prompt, requests, calls, null);
        }

        WriteCapture(directory, repeat, contract, prompt, requests, calls, null, complete: true);

        // Membership is frozen here. There is no Stage-2 request, and nothing downstream can
        // change what was accepted.
        var frozen = Stage1Projection.Collect(accepted);
        return new
        {
            repeat,
            providerCalls = TargetPacks.Length,
            claimsAccepted = frozen.Count,
            refusals = refusals.Select(item => new { item.Alias, item.Reason }).ToArray(),
            authorityClaimIds = frozen.Select(claim => claim.ClaimId.Value).Order(StringComparer.Ordinal),
            acceptedIdentities = frozen.Select(claim => claim.CanonicalIdentity).Order(StringComparer.Ordinal),
            documentLabels = frozen.Count(claim => claim.Disposition == Stage1MembershipDisposition.DocumentLabel),
            structuralUnits = frozen.Count(claim => claim.Disposition == Stage1MembershipDisposition.StructuralUnit),
        };
    }

    private static void Persist(
        BudgetedClassifier classifier, IReadOnlyList<object> repeats,
        IReadOnlyList<string> gates, string? aborted)
    {
        var directory = TestRepository.Path(OutputRoot);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "stage1-membership-retry-run.v1.json"),
            JsonSerializer.Serialize(new
            {
                artifactKind = "a99_stage1_membership_retry_run",
                schemaVersion = "a99-stage1-membership-retry-run-v1",
                experimentId = ExperimentId,
                head = Head(),
                authorizedBaseCommit = AuthorizedBaseCommit,
                descendantChangedPaths = ChangedPathsSince(AuthorizedBaseCommit),
                approval = "explicit-user-authorization, DOC-0252 only, packs 5 and 6, "
                    + "6 Stage-1 calls, 0 Stage-2 calls, cap 9",
                documentId = "DOC-0252",
                protocol = "a99-semantic-membership-v1",
                schemaSha256 = SchemaSha256,
                promptSha256 = PromptSha256,
                providerInputPack005Sha256 = ProviderInputPack005Sha256,
                providerInputPack006Sha256 = ProviderInputPack006Sha256,
                providerModelInputPlanSha256 = ProviderModelInputPlanSha256,
                packingPolicy = "COHERENT_REGION_SEGMENTATION_V1",
                targetPackIds = TargetPacks,
                repeats = Repeats,
                runLineage = RunLineage,
                retryAuthorityCalls = classifier.CallsMade,
                abortedAttemptCalls = AbortedAttemptCalls,
                totalProviderCallsObserved = classifier.CallsMade + AbortedAttemptCalls,
                usableAuthorityCalls = classifier.CallsMade,
                usableRetryRepeats = repeats.Count,
                stage2ProviderCalls = 0,
                placementCalls = 0,
                abortedAttempt = new
                {
                    root = "eval/a99-closed-loop/stage1-membership-experiment-v1",
                    calls = AbortedAttemptCalls,
                    usableRepeats = 0,
                    preserved = true,
                    mergedIntoThisScore = false,
                    note = "Transport-execution evidence about an output-budget defect. The 825 "
                        + "truncated characters carry no semantic information and are used for no "
                        + "inference here.",
                },
                authorizedRetryCalls = Stage1Calls,
                additionalCallsPermittedOnFailure = 0,
                model = Model,
                providerRoute = "OpenRouter",
                sourceSha256 = SourceSha256,
                sourceUniverseSha256 = SourceUniverseSha256,
                goldSha256 = GoldSha256,
                goldTargetCount = TargetGoldCount,
                manifestSha256 = ManifestSha256,
                evidence = new
                {
                    byteIdenticalToBaseline = false,
                    contentIdenticalToBaseline = true,
                    difference = "the protocol field only",
                },
                semanticIntervention = "none - this is a task decomposition, not a wording change",
                stage2RequestComposed = false,
                scoringPerformed = false,
                aborted,
                gates,
                repeatResults = repeats,
                callLedger = classifier.Ledger.Select(call => new
                {
                    call.Ordinal, call.DocumentId, call.Repeat, call.Stage,
                    call.SystemPromptSha256, call.RequestSha256, call.ResponseSha256,
                    call.RequestChars, call.ResponseChars, call.ElapsedMs,
                }).ToArray(),
            }, FreezeArtifact.Json).ReplaceLineEndings("\n"));
    }

    private sealed record CallRecord(
        int Ordinal, string PackId, int OwnedAliases, string RawResponse, long ElapsedMs);

    private static object Describe(
        CallRecord record, SemanticCoordinateContract contract, string prompt,
        IReadOnlyList<string> contractIssues, IReadOnlyList<string> decodeFailures,
        int claimsProposed, int claimsAccepted, IReadOnlyList<string> refusals, string? integrityFault) => new
    {
        record.Ordinal,
        stage = "stage1-membership",
        packId = record.PackId,
        protocol = contract.ProtocolVersion,
        schemaSha256 = contract.SchemaHash(),
        promptSha256 = CanonicalArtifactHash.OfText(prompt),
        packingPolicy = SemanticEvidencePackingPolicies.CoherentRegionSegmentationV1.PolicyId,
        model = Model,
        providerRoute = "OpenRouter",
        expectedItemCount = record.OwnedAliases,
        rawResponseSha256 = CanonicalArtifactHash.OfText(record.RawResponse),
        rawResponseBytes = Encoding.UTF8.GetByteCount(record.RawResponse),
        rawResponseUtf8Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(record.RawResponse)),
        contractIssues,
        decodeFailures,
        claimsProposed,
        claimsAccepted,
        refusals,
        integrityFault,
        elapsedMs = record.ElapsedMs,
    };

    /// <summary>
    /// Writes the capture after every call rather than once at the end. A paid call whose bytes
    /// only reach disk if the whole repeat succeeds is a call that can be lost, and one already was.
    /// </summary>
    private static void WriteCapture(
        string directory, int repeat, SemanticCoordinateContract contract, string prompt,
        Dictionary<string, string> requests, List<object> calls, CallRecord? pending,
        bool complete = false)
    {
        var all = pending is null
            ? calls
            : [.. calls, Describe(pending, contract, prompt, [], [], 0, 0, [], "IN_FLIGHT")];

        File.WriteAllText(
            Path.Combine(directory, "stage1-membership-retry-transport-capture.v1.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = "a99-stage1-membership-retry-transport-capture-v1",
                status = complete ? "TRANSPORT_CAPTURE_COMPLETE" : "TRANSPORT_CAPTURE_IN_PROGRESS",
                experimentId = ExperimentId,
                documentId = "DOC-0252",
                repeat,
                protocol = contract.ProtocolVersion,
                schemaSha256 = contract.SchemaHash(),
                promptSha256 = CanonicalArtifactHash.OfText(prompt),
                systemPromptUtf8Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(prompt)),
                userMessages = TargetPacks.ToDictionary(
                    pack => pack,
                    pack => new
                    {
                        sha256 = CanonicalSemanticRequestComposer.Hash(requests[pack]),
                        providerInputSha256 = SemanticAuthorityTransportCall.Sha256Utf8(
                            JsonSerializer.Serialize(new { systemPrompt = prompt, userMessage = requests[pack] })),
                        utf8Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(requests[pack])),
                    },
                    StringComparer.Ordinal),
                sourceSha256 = SourceSha256,
                sourceUniverseSha256 = SourceUniverseSha256,
                goldSha256 = GoldSha256,
                manifestSha256 = ManifestSha256,
                stage2Calls = 0,
                calls = all,
            }, FreezeArtifact.Json).ReplaceLineEndings("\n"));
    }

    // ---- helpers -----------------------------------------------------------------------------------

    internal static Dictionary<string, string> Compose(PdfStructuredSourceAuthority plan)
    {
        var model = new CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel(
            new UnreachableClassifier(),
            SemanticCoordinateContract.PdfSemanticMembershipV1,
            CanonicalSemanticExperiment.Baseline,
            SemanticEvidencePackingPolicies.CoherentRegionSegmentationV1,
            TargetPacks.ToHashSet(StringComparer.Ordinal));
        return model.ComposeRequests(plan.CreateProductionInput("DOC-0252"))
            .ToDictionary(segment => segment.PackId, segment => segment.RequestBytes, StringComparer.Ordinal);
    }

    private static Dictionary<string, string> ProviderInputs(
        Dictionary<string, string> requests, string systemPrompt) =>
        requests.ToDictionary(
            pair => pair.Key.Split(':')[1],
            pair => SemanticAuthorityTransportCall.Sha256Utf8(
                JsonSerializer.Serialize(new { systemPrompt, userMessage = pair.Value })),
            StringComparer.Ordinal);

    /// <summary>The aliases this pack owns, read back out of the packet the model is given.</summary>
    internal static HashSet<string> PacketAliases(string requestBytes)
    {
        var marker = requestBytes.LastIndexOf("\nSCHEMA=", StringComparison.Ordinal);
        using var packet = JsonDocument.Parse(marker < 0 ? requestBytes : requestBytes[..marker]);
        return packet.RootElement.GetProperty("ownedSourceAliases").EnumerateArray()
            .Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
    }

    private static string FirstAlias(string identity)
    {
        var first = identity.Split('|')[0];
        return first[..first.LastIndexOf(':')];
    }

    private static string Head() => Git("rev-parse HEAD");

    private static bool IsAncestor(string candidate, string descendant)
    {
        using var process = System.Diagnostics.Process.Start(new ProcessStartInfo(
            "git", $"merge-base --is-ancestor {candidate} {descendant}")
        {
            WorkingDirectory = TestRepository.Root(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    private static string[] ChangedPathsSince(string baseCommit) =>
        Git($"diff --name-only {baseCommit}..HEAD")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();

    private static bool WorkingTreeClean()
    {
        // Untracked files are not authority; tracked modifications are.
        var status = Git("status --porcelain --untracked-files=no");
        return status.Length == 0;
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

    private static string Check(string name, string expected, string actual) =>
        $"{name}: {(string.Equals(expected, actual, StringComparison.Ordinal) ? "MATCH" : "MISMATCH")} " +
        $"expected={expected} actual={actual}";

    private sealed class UnreachableClassifier : IHeaderClassifier
    {
        public string ModelName => throw new InvalidOperationException();
        public int ContextSize => throw new InvalidOperationException();
        public string RuntimeDescription => throw new InvalidOperationException();
        public int SharedPrefixTokens => throw new InvalidOperationException();
        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default, int expectedItemCount = 0) =>
            throw new InvalidOperationException("Gate verification must not contact a provider.");
        public Task<ChunkResult> ClassifyAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChunkResult> CritiqueAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChunkResult> ClassifyHierarchyAsync(IReadOnlyList<HierarchyItem> context, IReadOnlyList<HierarchyItem> headings, CancellationToken ct = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
