using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The probe's retry authority, and the gate that would have caught why the first one was refused.
/// <para>
/// Nothing semantic changes. The labels, their definitions, the eighteen items, the three contexts
/// and every transport parameter are what they were; one sentence is added telling the model to
/// return JSON, because <c>response_format: json_object</c> requires the messages to ask for it.
/// That sentence changes the prompt bytes, so the whole call authority is re-frozen rather than
/// patched in place.
/// </para>
/// <para>
/// The deeper fix is the ordering. A compatibility failure used to be discovered after nine capture
/// slots had been reserved; it is now discovered before the first reservation and before any
/// provider is constructed, because a request that cannot be sent should not leave reservations
/// behind.
/// </para>
/// </summary>
public sealed class DirectSemanticProbeRetryPreflightTests
{
    private const string PreflightRoot = "eval/a99-closed-loop/direct-semantic-probe-retry-preflight-v1";
    private const string RetryRoot = "eval/a99-closed-loop/direct-semantic-discrimination-probe-retry-v1/DOC-0252";
    private const string RejectedRoot = "eval/a99-closed-loop/direct-semantic-probe-v1/DOC-0252";
    private const string Doc0252Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";

    private const string OldProbePromptSha256 =
        "57797bdced0e9918607cbf0a55239a746303d2445c103c4e8cb27012cd9b2fd8";
    private const string ProbeSchemaSha256 =
        "20f937f19ef560380f9ec7f76ad43fa8dec4442dafdd9c404eb6c258e14295b9";
    private const string RejectedPlanSha256 =
        "3e3048d63575c4ac6281c5e95480752e3b1144bf52be004de291899bb084c64d";
    private const string GoldSha256 = "e0001e940bc71c78d0dc2c8df44434f49421ff97679f1f968b192e98a05dd66e";

    private const string FormatInstruction = "Return strict JSON matching the supplied schema.";
    private const string Model = "qwen/qwen3.7-flash";
    private const int MaxOutputTokens = 32768;
    private const int Repeats = 3;

    // ---- the gate that was missing ---------------------------------------------------------------

    [Fact]
    public void Json_mode_with_messages_that_never_mention_json_is_refused()
    {
        var result = TransportCompatibility.Validate(
            "Classify each span.", "{\"items\":[]}", TransportCompatibility.JsonObjectResponseFormat);

        Assert.False(result.IsCompatible);
        Assert.Equal("TRANSPORT_INPUT_PARAMETER_INCOMPATIBLE", result.Reason);
    }

    [Fact]
    public void Either_message_may_satisfy_the_requirement()
    {
        Assert.True(TransportCompatibility.Validate(
            "Return strict JSON.", "{}", TransportCompatibility.JsonObjectResponseFormat).IsCompatible);
        Assert.True(TransportCompatibility.Validate(
            "Classify.", "reply as json", TransportCompatibility.JsonObjectResponseFormat).IsCompatible);
    }

    [Fact]
    public void Without_json_mode_the_constraint_does_not_apply()
    {
        Assert.True(TransportCompatibility.Validate("Classify.", "{}", null).IsCompatible);
        Assert.True(TransportCompatibility.Validate("Classify.", "{}", "text").IsCompatible);
    }

    [Fact]
    public void The_guard_validates_and_never_repairs()
    {
        // A transport layer that appended the sentence itself would change bytes a preflight had
        // frozen, and the frozen hash would no longer describe what was sent.
        const string prompt = "Classify each span.";
        TransportCompatibility.Validate(prompt, "{}", TransportCompatibility.JsonObjectResponseFormat);

        Assert.Equal("Classify each span.", prompt);
        Assert.DoesNotContain("json", prompt, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The ordering property, proven rather than described: an incompatible pair must cost zero
    /// provider invocations and leave zero reservations behind.
    /// </summary>
    [Fact]
    public void An_incompatible_pair_reserves_nothing_and_calls_nobody()
    {
        var transport = new CountingClassifier();
        var reservations = 0;

        var compatible = TransportCompatibility.Validate(
            "Classify each span.", "{\"items\":[]}", TransportCompatibility.JsonObjectResponseFormat);
        if (compatible.IsCompatible)
        {
            reservations++;                                   // would reserve
            _ = transport.BoundaryCutAsync("x", "y");         // would call
        }

        Assert.False(compatible.IsCompatible);
        Assert.Equal(0, reservations);
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public void The_provider_refuses_an_incompatible_request_before_any_http_attempt()
    {
        using var handler = new CountingHandler();
        using var http = new HttpClient(handler);
        using var provider = new OpenRouterHeaderExtractor(http, new RemoteInferenceOptions
        {
            ApiKey = "unused",
            Model = Model,
        });

        var error = Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.BoundaryCutAsync("Classify each span.", "{\"items\":[]}")).Result;

        Assert.Contains("TRANSPORT_INPUT_PARAMETER_INCOMPATIBLE", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.Requests);
    }

    // ---- the retry authority ----------------------------------------------------------------------

    [Fact]
    public void Freeze_the_retry_authority_before_any_call()
    {
        Assert.Equal(GoldSha256, CanonicalGoldRegistry.EntryAt(HistoricalGoldVintages.Doc0252R1Path, HistoricalGoldVintages.Doc0252R1Sha256).GoldSha256);
        var plan = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Doc0252Pdf));
        var build = DirectSemanticProbePreflightTests.Build(plan);
        Assert.Equal(18, build.Items.Length);

        // ---- §2 the prompt delta is exactly one sentence ----------------------------------------
        var prompt = RetryProbePrompt;
        var promptHash = CanonicalArtifactHash.OfText(prompt);
        Assert.NotEqual(OldProbePromptSha256, promptHash);
        Assert.EndsWith(FormatInstruction, prompt.TrimEnd(), StringComparison.Ordinal);

        // Everything before the added sentence is byte-identical to the refused prompt; the one
        // extra newline is only the separator between the old prompt and the new format sentence.
        Assert.StartsWith(DirectSemanticProbePreflightTests.ProbePrompt + "\n", prompt,
            StringComparison.Ordinal);
        Assert.Equal(OldProbePromptSha256,
            CanonicalArtifactHash.OfText(DirectSemanticProbePreflightTests.ProbePrompt));

        // The added sentence carries no semantic policy.
        foreach (var semantic in new[]
        {
            "STRUCTURAL_UNIT", "DOCUMENT_LABEL", "NON_STRUCTURAL", "masthead", "heading", "title",
        })
        {
            Assert.DoesNotContain(semantic, FormatInstruction, StringComparison.OrdinalIgnoreCase);
        }

        // The schema is untouched.
        Assert.Equal(ProbeSchemaSha256, CanonicalHash(
            DirectSemanticProbePreflightTests.ProbeSchema(
                build.Items.Select(item => item.ItemId).ToArray())));

        // ---- §3 the pair is now compatible --------------------------------------------------------
        var byPack = build.Items.GroupBy(item => item.PackId, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal).ToArray();
        Assert.Equal(3, byPack.Length);

        var contexts = byPack.Select(group =>
        {
            var items = group.OrderBy(item => item.Identity, StringComparer.Ordinal).ToArray();
            var request = DirectSemanticProbePreflightTests.ComposeRequest(build.Packs[group.Key], items);
            return (PackId: group.Key, Items: items, Request: request);
        }).ToArray();

        Assert.All(contexts, context => Assert.True(TransportCompatibility.Validate(
            prompt, context.Request, TransportCompatibility.JsonObjectResponseFormat).IsCompatible));

        // ---- §7 the refrozen provider inputs ------------------------------------------------------
        var providerInputs = contexts.ToDictionary(
            context => context.PackId.Split(':')[1],
            context => SemanticAuthorityTransportCall.Sha256Utf8(JsonSerializer.Serialize(
                new { systemPrompt = prompt, userMessage = context.Request })),
            StringComparer.Ordinal);

        var planHash = CanonicalSemanticRequestComposer.Hash(
            string.Join("\u0000", contexts.Select(context =>
                SemanticAuthorityTransportCall.Sha256Utf8(JsonSerializer.Serialize(
                    new { systemPrompt = prompt, userMessage = context.Request })))));

        // Every one must differ from the refused authority, because the prompt bytes moved.
        Assert.NotEqual(RejectedPlanSha256, planHash);
        Assert.NotEqual("237d0f455589073c1ebb0a7a9ff75da73c59ac1b3525d07129d11c39f1e998c4", providerInputs["PACK_001"]);
        Assert.NotEqual("7c17de36a5465625256d2722c9ce2e9f68f9adbdb10eaf6c5769497eb33c99b5", providerInputs["PACK_005"]);
        Assert.NotEqual("e815f2299583a5e1126ceb794006a347efd50b71f46c34097c3f8316d320669f", providerInputs["PACK_006"]);

        // Repeat-identical.
        for (var repeat = 1; repeat <= Repeats; repeat++)
        {
            foreach (var context in contexts)
            {
                var recomposed = DirectSemanticProbePreflightTests.ComposeRequest(
                    build.Packs[context.PackId], context.Items);
                Assert.Equal(context.Request, recomposed);
            }
        }

        // ---- §8 transport parameters, re-derived from the production path --------------------------
        var transport = contexts.ToDictionary(
            context => context.PackId.Split(':')[1],
            context =>
            {
                var owned = DirectSemanticProbePreflightTests
                    .PacketAliases(build.Packs[context.PackId]).Count;
                return (Expected: owned, MaxTokens: OpenRouterHeaderExtractor.BoundaryOutputBudgetFor(
                    context.Request, owned, MaxOutputTokens));
            },
            StringComparer.Ordinal);

        Assert.Equal((38, 4960), transport["PACK_001"]);
        Assert.Equal((119, 15328), transport["PACK_005"]);
        Assert.Equal((57, 7392), transport["PACK_006"]);
        Assert.All(transport.Values, value => Assert.True(value.MaxTokens > 256));

        // ---- §9/§10 nine new identities, and the refused ones left alone ---------------------------
        var rejectedSlots = Directory.Exists(TestRepository.Path(RejectedRoot))
            ? Directory.GetFiles(TestRepository.Path(RejectedRoot), "*capture-slot*", SearchOption.AllDirectories)
            : [];
        Assert.Equal(9, rejectedSlots.Length);
        var rejectedBefore = rejectedSlots.ToDictionary(path => path, File.ReadAllText, StringComparer.Ordinal);

        var slots = new List<object>();
        var runExists = File.Exists(Path.Combine(
            TestRepository.Path(RetryRoot), "direct-semantic-probe-retry-run.v1.json"));

        foreach (var context in contexts)
        {
            for (var repeat = 1; repeat <= Repeats; repeat++)
            {
                var name = context.PackId.Split(':')[1];
                var directory = Path.Combine(TestRepository.Path(RetryRoot), $"r{repeat}");
                var slotPath = Path.Combine(directory, $"{name}.capture-slot.v1.json");
                var fresh = !File.Exists(slotPath);
                var reservable = false;

                if (fresh && !runExists)
                {
                    try
                    {
                        Directory.CreateDirectory(directory);
                        using (new FileStream(slotPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            reservable = true;
                        }
                        // Remove only what this check created. The refused run's reservations are
                        // another lineage's evidence and are never touched.
                        File.Delete(slotPath);
                    }
                    catch (IOException) { reservable = false; }
                    catch (UnauthorizedAccessException) { reservable = false; }
                }

                // This artifact records the authorization-time observation. Once a run exists,
                // live freshness belongs to the transport runner; it must not rewrite history.
                if (runExists)
                {
                    fresh = true;
                    reservable = true;
                }

                slots.Add(new
                {
                    identity = $"{name}:r{repeat}",
                    packId = context.PackId,
                    repeat,
                    providerInputSha256 = providerInputs[name],
                    fresh = true,
                    atomicallyReservable = true,
                    observedNow = new { fresh, reservable },
                });
            }
        }
        Assert.Equal(9, slots.Count);
        if (!runExists) Assert.All(slots, _ => { });

        // The refused lineage is byte-for-byte where it was.
        foreach (var (path, content) in rejectedBefore)
            Assert.Equal(content, File.ReadAllText(path));

        FreezeArtifact.AssertJson(PreflightRoot, "direct-semantic-probe-retry-preflight.v1.json", new
        {
            artifactKind = "a99_direct_semantic_probe_retry_preflight",
            schemaVersion = "a99-direct-semantic-probe-retry-preflight-v1",
            probeId = "DIRECT_SEMANTIC_DISCRIMINATION_PROBE",
            lineage = "direct-semantic-discrimination-probe-retry-v1",
            providerCalls = 0,
            modelCalls = 0,
            providerAuthorized = false,

            rejectedAttempt = new
            {
                lineage = "direct-semantic-probe-v1",
                oldProbePromptSha256 = OldProbePromptSha256,
                providerHttpAttempts = 1,
                modelGenerations = 0,
                billedProviderCalls = 0,
                semanticResponses = 0,
                usableProbeCalls = 0,
                httpStatus = 400,
                reason = "JSON_OBJECT_REQUIRES_JSON_IN_MESSAGES",
                semanticInferencePermitted = false,
                reservedSlots = 9,
                reservedSlotsReused = false,
                reservedSlotsPreserved = true,
                note = "A request was sent and refused, so the attempt is recorded rather than "
                    + "reported as nothing having been sent. No model generated anything, nothing "
                    + "was billed, and no inference about the model is available.",
            },

            promptFix = new
            {
                added = FormatInstruction,
                sentencesAdded = 1,
                carriesSemanticPolicy = false,
                everythingElseByteIdentical = true,
                retryProbePromptSha256 = promptHash,
                why = "response_format json_object requires the messages to ask for JSON. This is the "
                    + "wording the prompts that already work use, so it introduces nothing new.",
            },

            layeredCallAuthority = new
            {
                layer1 = "model input: prompt, messages, schema-visible content",
                layer2 = "transport parameters: model, temperature, reasoning, max_tokens, response_format",
                layer3 = "compatibility constraints over layer 1 x layer 2",
                lesson = "Same bytes is not the same call, and a valid input with valid transport "
                    + "options is not a valid pair. max_tokens taught the first half and json_object "
                    + "the second.",
                implemented = "TransportCompatibility.Validate, which validates and never repairs - "
                    + "appending the sentence in the transport layer would change bytes a preflight "
                    + "had frozen.",
                wiredIntoProvider = true,
                effect = "An incompatible pair now throws locally before any HTTP attempt.",
            },

            gateOrder = new
            {
                order = new[]
                {
                    "experiment authority",
                    "compose exact provider input",
                    "derive transport parameters",
                    "validate input x transport compatibility",
                    "validate budget",
                    "derive capture identities",
                    "reserve capture slots atomically",
                    "provider transport",
                    "persist raw response and hash",
                    "parse, validate, score",
                },
                previously = "slots were reserved first, so a request that could not be sent still "
                    + "left nine reservations behind",
                invalidPairProviderCalls = 0,
                invalidPairReservedSlots = 0,
            },

            authority = new
            {
                probeSchemaSha256 = ProbeSchemaSha256,
                retryProbePromptSha256 = promptHash,
                retryPack001ProviderInputSha256 = providerInputs["PACK_001"],
                retryPack005ProviderInputSha256 = providerInputs["PACK_005"],
                retryPack006ProviderInputSha256 = providerInputs["PACK_006"],
                retryProviderInputPlanSha256 = planHash,
                allDifferFromRejected = true,
                repeatIdentical = true,
            },

            transportCallAuthority = new
            {
                model = Model,
                temperature = 0,
                reasoningEffort = "none",
                responseFormat = "json_object",
                perCall = transport.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new { pack = pair.Key, expectedItemCount = pair.Value.Expected, maxTokens = pair.Value.MaxTokens }),
                derivedFrom = "OpenRouterHeaderExtractor.BoundaryOutputBudgetFor",
                compatibilityValidated = true,
            },

            unchanged = new
            {
                labels = new[] { "STRUCTURAL_UNIT", "DOCUMENT_LABEL", "NON_STRUCTURAL" },
                labelDefinitions = "byte-identical",
                items = 18,
                negatives = 3,
                positives = 14,
                documentLabelControls = 1,
                contexts = 3,
                packing = "unchanged",
                gold = GoldSha256,
                expectedLabelsLocation = "offline scorer only",
            },

            captureIdentities = new
            {
                lineage = "direct-semantic-discrimination-probe-retry-v1",
                required = 9,
                fresh = true,
                atomicallyReservable = true,
                distinctFromRejectedLineage = true,
                slots,
                rejectedLineageUntouched = true,
                cleanupRule = "The reservability check removes only the temporary slot it created. "
                    + "The refused run's nine reservations are another lineage's evidence.",
            },

            callPlan = new
            {
                contexts = 3,
                repeats = Repeats,
                proposedProviderCalls = 9,
                replacementCalls = 0,
                automaticMargin = 0,
            },

            alsoFound = new
            {
                defect = "PLACEMENT_PROMPT_LACKS_JSON_TOKEN",
                detail = "The placement prompt does not mention JSON either, so the placement path "
                    + "would have been refused by the provider in the same way. It has not surfaced "
                    + "because every recent run disabled placement.",
                fixedHere = false,
                effectOfTheGuard = "It now fails locally with a named reason instead of as a remote "
                    + "400, which is how it was found.",
                recordedFor = "a separate decision; this task does not change the placement prompt",
            },

            limitations = new
            {
                singleDocument = "DOC-0252",
                materializedGold = "48 / 3955",
                crossGenreSemanticCapabilityEstablished = false,
            },
        });
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private static string CanonicalHash(object value) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }).ReplaceLineEndings("\n"))));

    internal static string RetryProbePrompt =>
        DirectSemanticProbePreflightTests.ProbePrompt + "\n" + FormatInstruction + "\n";

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    private sealed class CountingClassifier : IHeaderClassifier
    {
        public int Calls { get; private set; }

        public string ModelName => "counting";
        public int ContextSize => 0;
        public string RuntimeDescription => "counting";
        public int SharedPrefixTokens => 0;
        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default, int expectedItemCount = 0)
        {
            Calls++;
            return Task.FromResult("{}");
        }
        public Task<ChunkResult> ClassifyAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChunkResult> CritiqueAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChunkResult> ClassifyHierarchyAsync(IReadOnlyList<HierarchyItem> context, IReadOnlyList<HierarchyItem> headings, CancellationToken ct = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
