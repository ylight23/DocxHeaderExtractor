using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// A diagnostic probe, prepared and frozen before anything is sent.
/// <para>
/// Stage 1 removed placement, role, selection mode and coordinate serialization from the model's
/// task, and the two stable masthead families survived untouched at 3/3 each. That eliminates the
/// architecture as an explanation but leaves two possibilities open, because Stage 1 was still an
/// open-set extraction task: the model may hold the distinction and over-admit title-like spans
/// when asked to list everything, or it may not hold the distinction at all.
/// </para>
/// <para>
/// This probe asks each frozen span directly and closes the answer to three labels. It is a
/// research instrument and not a production contract: naming candidate spans to a model would be a
/// recall gate, and nothing here authorizes one.
/// </para>
/// </summary>
public sealed class DirectSemanticProbePreflightTests
{
    private const string PreflightRoot = "eval/a99-closed-loop/direct-semantic-probe-preflight-v1";
    private const string OutputRoot = "eval/a99-closed-loop/direct-semantic-probe-v1/DOC-0252";
    private const string Doc0252Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";

    private const string GoldSha256 = "e0001e940bc71c78d0dc2c8df44434f49421ff97679f1f968b192e98a05dd66e";
    private const string SourceSha256 = "a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61";
    private const string SourceUniverseSha256 = "2a953bf785ed1af00bc908ff9e5d6a1d988b04c0d980ecd95336bc5a9702f46f";
    private const string Model = "qwen/qwen3.7-flash";
    private const int MaxOutputTokens = 32768;
    private const int Repeats = 3;

    private static readonly string[] StageOnePacks =
    [
        "COHERENT_REGION_SEGMENTATION_V1:PACK_005",
        "COHERENT_REGION_SEGMENTATION_V1:PACK_006",
    ];

    /// <summary>The negatives, as families rather than exact tuples.</summary>
    private static readonly (string Family, string[] Aliases)[] MastheadFamilies =
    [
        ("F1", ["L0513:S0", "L0514:S0"]),
        ("F2", ["L0515:S0"]),
        ("F3", ["L0516:S0", "L0517:S0", "L0518:S0"]),
    ];

    [Fact]
    public void Freeze_the_direct_semantic_probe_before_any_call()
    {
        Assert.Equal(GoldSha256, CanonicalGoldRegistry.Entry("DOC-0252").GoldSha256);
        var plan = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Doc0252Pdf));
        Assert.Equal(SourceUniverseSha256, plan.SourceUniverseSha256);

        var gold = GoldClaims();
        Assert.Equal(41, gold.Count);

        // Every pack, because the document-title control does not live in the Stage-1 packs and
        // moving it into one of them would change the context its question depends on.
        var packs = ComposeAllPacks(plan);
        var ownership = packs.ToDictionary(
            pair => pair.Key,
            pair => PacketAliases(pair.Value),
            StringComparer.Ordinal);

        string PackOf(string alias) => ownership
            .First(pair => pair.Value.Contains(alias)).Key;

        // ---- §5 positive controls: every Gold claim the Stage-1 packs own -------------------------
        var stageOneOwned = StageOnePacks
            .SelectMany(pack => ownership[pack]).ToHashSet(StringComparer.Ordinal);
        var positives = gold
            .Where(claim => stageOneOwned.Contains(FirstAlias(claim.Key)))
            .OrderBy(claim => claim.Key, StringComparer.Ordinal)
            .Select(claim => new ProbeItem(
                ItemId(claim.Key), claim.Key, PackOf(FirstAlias(claim.Key)),
                "STRUCTURAL_UNIT", claim.Value, TextOf(plan, claim.Key)))
            .ToArray();
        Assert.Equal(14, positives.Length);

        // The controls are not cherry-picked: several of them look exactly like the metadata this
        // probe calls non-structural - a day banner carrying a date and a chair's name, for
        // instance - and they are Gold. Excluding them would make the probe easy and meaningless.
        var metadataShaped = positives.Count(item =>
            item.Text.Contains("2025", StringComparison.Ordinal)
            || item.Text.Contains("Chair", StringComparison.OrdinalIgnoreCase));
        Assert.True(metadataShaped >= 2,
            "the positive controls must include Gold claims that look like metadata");

        // ---- §4 negatives, derived from the families ------------------------------------------------
        var negatives = new List<ProbeItem>();
        foreach (var (family, aliases) in MastheadFamilies)
        {
            var identity = string.Join("|", aliases.Select(alias =>
            {
                var atom = plan.Atoms.First(item => item.Alias == alias);
                return $"{alias}:0-{atom.Text.Length}";
            }));
            Assert.DoesNotContain(identity, gold.Keys);   // a negative must not be approved Gold
            negatives.Add(new ProbeItem(
                ItemId(identity), identity, PackOf(aliases[0]),
                "NON_STRUCTURAL", $"masthead-family-{family}", TextOf(plan, identity)));
        }
        Assert.Equal(3, negatives.Count);
        // Which pack owns each family is derived, not assumed: a family that straddles a pack
        // boundary would need its own call rather than being folded into PACK_006's.
        foreach (var negative in negatives)
            Assert.Contains(negative.PackId, ownership.Keys);

        // ---- §6 the document-label control, explicitly declared rather than invented ----------------
        var documentTitle = gold.Single(claim => claim.Value == "DocumentTitle");
        var titlePack = PackOf(FirstAlias(documentTitle.Key));
        Assert.DoesNotContain(titlePack, StageOnePacks);
        var labelControl = new ProbeItem(
            ItemId(documentTitle.Key), documentTitle.Key, titlePack,
            "DOCUMENT_LABEL", documentTitle.Value, TextOf(plan, documentTitle.Key));

        var items = positives.Concat(negatives).Append(labelControl)
            .OrderBy(item => item.Identity, StringComparer.Ordinal).ToArray();
        Assert.Equal(18, items.Length);

        // Item ids must not leak the answer, and must be stable.
        Assert.All(items, item => Assert.DoesNotContain("STRUCTURAL", item.ItemId, StringComparison.Ordinal));
        Assert.Equal(items.Length, items.Select(item => item.ItemId).Distinct(StringComparer.Ordinal).Count());

        // ---- §2 the probe contract -------------------------------------------------------------------
        var schema = ProbeSchema(items.Select(item => item.ItemId).ToArray());
        var schemaJson = JsonSerializer.Serialize(schema);
        foreach (var forbidden in new[]
        {
            "parent-node", "parentClaimId", "relationHints", "semanticRole", "selectionMode",
            "ROOT", "NONE", "hierarchy", "start", "end", "offset",
        })
        {
            Assert.DoesNotContain(forbidden, schemaJson, StringComparison.Ordinal);
        }
        var schemaHash = CanonicalHash(schema);

        // ---- §4 the expected labels must not reach the model ------------------------------------------
        var prompt = ProbePrompt;
        var promptHash = CanonicalArtifactHash.OfText(prompt);
        foreach (var leak in new[] { "masthead", "F1", "F2", "F3", "expected", "Gold", "DOC-0252" })
            Assert.DoesNotMatch(WholeWord(leak), prompt);

        // ---- §7/§9 the call plan: one call per context, three repeats -----------------------------------
        var byPack = items.GroupBy(item => item.PackId, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal).ToArray();
        Assert.Equal(3, byPack.Length);   // the title's pack, PACK_005, PACK_006

        var calls = byPack.Select(group =>
        {
            var packet = ProbePacket(packs[group.Key], group.ToArray());
            var request = packet + "\nSCHEMA=" + JsonSerializer.Serialize(
                ProbeSchema(group.Select(item => item.ItemId).ToArray()));
            var owned = ownership[group.Key].Count;
            return new
            {
                packId = group.Key,
                items = group.Count(),
                negatives = group.Count(item => item.ExpectedLabel == "NON_STRUCTURAL"),
                positives = group.Count(item => item.ExpectedLabel == "STRUCTURAL_UNIT"),
                documentLabels = group.Count(item => item.ExpectedLabel == "DOCUMENT_LABEL"),
                evidencePacketSha256 = CanonicalSemanticRequestComposer.Hash(packet),
                requestSha256 = CanonicalSemanticRequestComposer.Hash(request),
                providerInputSha256 = SemanticAuthorityTransportCall.Sha256Utf8(
                    JsonSerializer.Serialize(new { systemPrompt = prompt, userMessage = request })),
                expectedItemCount = owned,
                maxTokens = OpenRouterHeaderExtractor.BoundaryOutputBudgetFor(request, owned, MaxOutputTokens),
            };
        }).ToArray();

        // Transport authority must clear the floor that truncated a paid call once already.
        Assert.All(calls, call => Assert.True(call.maxTokens > 256));

        var planHash = CanonicalSemanticRequestComposer.Hash(
            string.Join("\u0000", calls.Select(call => call.providerInputSha256)));
        var proposedCalls = calls.Length * Repeats;
        Assert.Equal(9, proposedCalls);

        // Deterministic.
        Assert.Equal(schemaHash, CanonicalHash(ProbeSchema(items.Select(item => item.ItemId).ToArray())));

        // ---- §14 capture identities ----------------------------------------------------------------------
        var slots = Enumerable.Range(1, Repeats).Select(repeat =>
        {
            var directory = Path.Combine(TestRepository.Path(OutputRoot), $"r{repeat}");
            return new
            {
                repeat,
                directoryExists = Directory.Exists(directory),
                existingFiles = Directory.Exists(directory)
                    ? Directory.GetFiles(directory).Select(Path.GetFileName).ToArray()
                    : [],
            };
        }).ToArray();
        if (!File.Exists(Path.Combine(TestRepository.Path(OutputRoot), "direct-semantic-probe-run.v1.json")))
            Assert.All(slots, slot => Assert.Empty(slot.existingFiles));

        FreezeArtifact.AssertJson(PreflightRoot, "direct-semantic-probe-preflight.v1.json", new
        {
            artifactKind = "a99_direct_semantic_probe_preflight",
            schemaVersion = "a99-direct-semantic-probe-preflight-v1",
            probeId = "DIRECT_SEMANTIC_DISCRIMINATION",
            providerCalls = 0,
            modelCalls = 0,
            providerAuthorized = false,
            diagnosticOnly = true,
            productionBehaviour = false,
            productionRecallGateIntroduced = false,

            priorResult = new
            {
                experiment = "PHYSICAL_STAGE1_MEMBERSHIP_ONLY",
                classification = "TWO_STAGE_SEPARATION_DOES_NOT_FIX_MEMBERSHIP_POLICY",
                gold = "14/14 bound x 3 repeats",
                familyPresence = new { f1 = "3/3", f2 = "3/3", f3 = "1/3" },
                frozen = true,
            },

            question = new
            {
                h1 = "The model holds the distinction but over-admits title-like spans when asked to "
                    + "list every heading in an open set.",
                h2 = "The model does not hold the distinction, and fails it even when asked directly "
                    + "about one named span.",
                whyStageOneCannotAnswerIt = "Stage 1 removed placement and role but was still an "
                    + "open-set extraction task, so over-admission and misclassification produce the "
                    + "same observable output.",
                onlyIntendedChange = "open-set extraction -> closed semantic decision over a named span",
            },

            contract = new
            {
                labels = new[] { "STRUCTURAL_UNIT", "DOCUMENT_LABEL", "NON_STRUCTURAL" },
                definitions = new
                {
                    STRUCTURAL_UNIT = "The span opens or names a distinct structural unit in this "
                        + "document at this location.",
                    DOCUMENT_LABEL = "The span is an accepted document-level label or title, but is "
                        + "not a structural tree unit.",
                    NON_STRUCTURAL = "The span identifies or describes content, an event, an "
                        + "organisation, a venue, a date, a meeting mode, participants or other "
                        + "material, but does not itself open or name an accepted structural unit.",
                },
                reasonCodeScored = false,
                freeTextDeterminesScoring = false,
                schemaSha256 = schemaHash,
                promptSha256 = promptHash,
                forbiddenFields = new[]
                {
                    "parent-node", "ROOT", "NONE", "semanticRole", "selectionMode", "offsets", "hierarchy",
                },
            },

            items = new
            {
                total = items.Length,
                negatives = negatives.Count,
                positives = positives.Length,
                documentLabelControls = 1,
                expectedLabelsShownToModel = false,
                metadataShapedPositiveControls = metadataShaped,
                controlSelectionNote = "The positive controls are every Gold claim the Stage-1 packs "
                    + "own, not a chosen subset. Several of them look like the metadata this probe "
                    + "calls non-structural - a day banner carrying a date and a chair's name - and "
                    + "they are approved Gold. A probe that excluded them would be easy and would "
                    + "prove nothing.",
                rows = items.Select(item => new
                {
                    item.ItemId, item.Identity, item.PackId, item.ExpectedLabel,
                    goldRole = item.GoldRole,
                    text = item.Text.Length > 70 ? item.Text[..70] : item.Text,
                }),
            },

            documentLabelControl = new
            {
                identity = labelControl.Identity,
                pack = titlePack,
                inStageOnePacks = false,
                handling = "Declared as its own call with its own pack context, per the rule against "
                    + "silently broadening pack scope. Its context could not be moved into a Stage-1 "
                    + "pack without changing the question it answers.",
                whyItMatters = "The model called the meeting-mode masthead a document title under the "
                    + "baseline. Whether it can tell that span from the document's actual title is "
                    + "the sharpest form of the question this probe asks.",
            },

            callPlan = new
            {
                repeats = Repeats,
                callsPerRepeat = calls.Length,
                proposedProviderCalls = proposedCalls,
                notSixBecause = "The document-label control needs its own pack context, so a repeat is "
                    + "three calls rather than two. Batching it into a Stage-1 pack would change the "
                    + "context the decision depends on.",
                calls,
                providerModelInputPlanSha256 = planHash,
            },

            transportCallAuthority = new
            {
                model = Model,
                temperature = 0,
                reasoningEffort = "none",
                responseFormat = "json_object",
                maxOutputTokens = MaxOutputTokens,
                perCall = calls.Select(call => new { call.packId, call.expectedItemCount, call.maxTokens }),
                derivedFrom = "OpenRouterHeaderExtractor.BoundaryOutputBudgetFor, the production "
                    + "formula, rather than restated here",
                aboveTruncationFloor = true,
            },

            capture = new
            {
                replayComplete = true,
                reservationBeforeFirstCall = true,
                rawPersistedBeforeParse = true,
                onParseFailure = new
                {
                    rawResponseCaptured = true,
                    parseStatus = "FAILED",
                    semanticScoring = "STOP",
                },
                identities = new { required = proposedCalls, fresh = true, slots },
            },

            metrics = new
            {
                perItemPerRepeat = new[] { "expected class", "returned class", "correct" },
                aggregates = new[] { "MASTHEAD_NEGATIVE_ACCURACY", "GOLD_POSITIVE_ACCURACY" },
                perFamily = new[] { "F1 correct / 3", "F2 correct / 3", "F3 correct / 3" },
            },

            predeclaredInterpretation = new object[]
            {
                new
                {
                    outcome = "A",
                    condition = "the masthead families answer NON_STRUCTURAL and the Gold controls "
                        + "answer STRUCTURAL_UNIT",
                    classification = "DIRECT_SEMANTIC_CAPABILITY_PRESENT",
                    meaning = "The model holds the distinction when asked. What remains is the "
                        + "open-set membership formulation, not basic semantic capability.",
                },
                new
                {
                    outcome = "B",
                    condition = "F1 and F2 stay STRUCTURAL_UNIT or DOCUMENT_LABEL while the Gold "
                        + "controls are correct",
                    classification = "DIRECT_SEMANTIC_DISCRIMINATION_LIMIT_OBSERVED",
                    meaning = "This model, under this policy, fails the distinction in a direct task.",
                    doNotGeneralize = "to language models in general",
                },
                new
                {
                    outcome = "C",
                    condition = "the Gold controls also regress heavily",
                    classification = "PROBE_CONTRACT_OR_CONTEXT_INVALID",
                    meaning = "The instrument is wrong. No inference about the model is permitted.",
                },
            },

            secondModel = new
            {
                deferred = true,
                note = "If outcome B occurs, the same frozen probe could be replayed byte-identically "
                    + "against a different model to separate a limitation of this model from "
                    + "ambiguity in the task. Not now, and not part of this authorization.",
            },

            limitations = new
            {
                singleDocument = "DOC-0252",
                materializedGold = "48 / 3955",
                crossGenreSemanticCapabilityEstablished = false,
                note = "Naming frozen spans to a model is acceptable here only because this measures "
                    + "capability rather than producing output. It is not a production candidate gate "
                    + "and authorizes none.",
            },
        });
    }

    // ---- the probe contract -------------------------------------------------------------------

    /// <summary>
    /// The probe's system prompt. It defines the three labels functionally and says nothing about
    /// mastheads, meetings or any genre - a definition that named the answer would measure reading
    /// comprehension rather than semantic discrimination.
    /// </summary>
    internal const string ProbePrompt = """
        You are classifying specific, already-selected spans of one document.

        For each item you are given an identifier and the exact source text of a span, together with
        the surrounding document evidence. Decide what that span does in this document, and return
        exactly one classification for every item you are given.

        STRUCTURAL_UNIT - the span opens or names a distinct structural unit of the document at this
        location, and content belonging to that unit follows it.

        DOCUMENT_LABEL - the span is the document's own accepted label or title. It names the
        document as a whole rather than opening a unit inside it.

        NON_STRUCTURAL - the span identifies or describes something - content, an occasion, a body,
        a place, a date, a format, people, or other material - without itself opening or naming a
        unit of the document.

        Judge what the span does, not how it is formatted. Prominence, capitalisation and position
        are evidence, never the decision. Return one entry per item, using the identifier exactly as
        given, and classify every item.
        """;

    internal static object ProbeSchema(string[] itemIds) => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            decisions = new
            {
                type = "array",
                minItems = itemIds.Length,
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new
                    {
                        itemId = new { type = "string", @enum = itemIds },
                        classification = new
                        {
                            type = "string",
                            @enum = new[] { "STRUCTURAL_UNIT", "DOCUMENT_LABEL", "NON_STRUCTURAL" },
                        },
                        reasonCode = new { type = "string" },
                    },
                    required = new[] { "itemId", "classification" },
                },
            },
        },
        required = new[] { "decisions" },
    };

    /// <summary>The pack's own evidence, plus the items to classify inside it.</summary>
    internal static string ProbePacket(string stageOneRequest, ProbeItem[] items)
    {
        var marker = stageOneRequest.LastIndexOf("\nSCHEMA=", StringComparison.Ordinal);
        var packet = marker < 0 ? stageOneRequest : stageOneRequest[..marker];
        using var document = JsonDocument.Parse(packet);

        return JsonSerializer.Serialize(new
        {
            probe = "direct-semantic-classification",
            documentEvidence = JsonSerializer.Deserialize<JsonElement>(packet),
            itemsToClassify = items.Select(item => new { item.ItemId, sourceText = item.Text }),
        });
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static string ItemId(string identity) =>
        "ITEM-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..8].ToUpperInvariant();

    private static string CanonicalHash(object value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(value, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }).ReplaceLineEndings("\n"))));

    private static System.Text.RegularExpressions.Regex WholeWord(string term) =>
        new($@"\b{System.Text.RegularExpressions.Regex.Escape(term)}\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    internal static string TextOf(PdfStructuredSourceAuthority plan, string identity) =>
        string.Join(" ", identity.Split('|').Select(part =>
        {
            var split = part.LastIndexOf(':');
            var alias = part[..split];
            var span = part[(split + 1)..].Split('-');
            var atom = plan.Atoms.First(item => item.Alias == alias);
            var start = int.Parse(span[0]);
            var end = Math.Min(int.Parse(span[1]), atom.Text.Length);
            return atom.Text[start..end];
        }));

    internal static Dictionary<string, string> ComposeAllPacks(PdfStructuredSourceAuthority plan)
    {
        var model = new CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel(
            new UnreachableClassifier(),
            SemanticCoordinateContract.PdfSemanticMembershipV1,
            CanonicalSemanticExperiment.Baseline,
            SemanticEvidencePackingPolicies.CoherentRegionSegmentationV1);
        return model.ComposeRequests(plan.CreateProductionInput("DOC-0252"))
            .ToDictionary(segment => segment.PackId, segment => segment.RequestBytes, StringComparer.Ordinal);
    }

    private static HashSet<string> PacketAliases(string requestBytes)
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

    private static Dictionary<string, string> GoldClaims()
    {
        using var gold = CanonicalGoldRegistry.Resolve("DOC-0252");
        return gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
            .ToDictionary(
                claim => claim.GetProperty("identity").GetString()!,
                claim => claim.TryGetProperty("semanticRole", out var role) && role.ValueKind == JsonValueKind.String
                    ? role.GetString()!
                    : "(none)",
                StringComparer.Ordinal);
    }

    internal sealed record ProbeItem(
        string ItemId, string Identity, string PackId, string ExpectedLabel,
        string GoldRole, string Text);

    private sealed class UnreachableClassifier : IHeaderClassifier
    {
        public string ModelName => throw new InvalidOperationException();
        public int ContextSize => throw new InvalidOperationException();
        public string RuntimeDescription => throw new InvalidOperationException();
        public int SharedPrefixTokens => throw new InvalidOperationException();
        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default, int expectedItemCount = 0) =>
            throw new InvalidOperationException("PROVIDER_CALLS must remain 0 in a preflight.");
        public Task<ChunkResult> ClassifyAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChunkResult> CritiqueAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChunkResult> ClassifyHierarchyAsync(IReadOnlyList<HierarchyItem> context, IReadOnlyList<HierarchyItem> headings, CancellationToken ct = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
