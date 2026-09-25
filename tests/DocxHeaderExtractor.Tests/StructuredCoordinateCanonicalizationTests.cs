using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The v2 structured contract as request authority, and the record of what moved.
/// <para>
/// v2 changes exactly one thing: the reply no longer carries a selection mode, because the mode is
/// a comparison against a source the harness owns and the model does not hold. v1 stays exactly as
/// it was - same schema hash, same prompt clause, same binder - because the runs that produced the
/// corpus ran on it and their results are only readable against the contract they ran under.
/// </para>
/// </summary>
public sealed class StructuredCoordinateCanonicalizationTests
{
    private const string ArtifactRoot = "eval/a99-closed-loop/structured-coordinate-canonicalization-v1";
    private const string Doc0252Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";

    private const string V1SchemaSha256 = "69b99b9099b964a5cf5985b8ec618db49c8ee5c3fa8a2bb8f69993cdc2e24f6f";
    private const string SourceUniverseSha256 = "2a953bf785ed1af00bc908ff9e5d6a1d988b04c0d980ecd95336bc5a9702f46f";
    private const string GoldSha256 = "e0001e940bc71c78d0dc2c8df44434f49421ff97679f1f968b192e98a05dd66e";

    [Fact]
    public void Freeze_the_v2_request_authority_and_what_it_changed()
    {
        Assert.Equal(GoldSha256, CanonicalGoldRegistry.EntryAt(HistoricalGoldVintages.Doc0252R1Path, HistoricalGoldVintages.Doc0252R1Sha256).GoldSha256);
        var plan = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Doc0252Pdf));
        Assert.Equal(SourceUniverseSha256, plan.SourceUniverseSha256);

        var v1 = SemanticCoordinateContract.PdfStructuredSourceParts;
        var v2 = SemanticCoordinateContract.PdfStructuredSourcePartsV2;

        // v1 is historical authority and is asserted unchanged, not merely left alone.
        Assert.Equal(V1SchemaSha256, v1.SchemaHash());
        Assert.NotEqual(v1.SchemaHash(), v2.SchemaHash());
        Assert.Equal("a99-semantic-source-parts-v2", v2.ProtocolVersion);

        var v1Prompt = CanonicalSemanticEngine.SystemPromptFor(v1, HistoricalRequest.Of(CanonicalSemanticExperiment.Baseline));
        var v2Prompt = CanonicalSemanticEngine.SystemPromptFor(v2, HistoricalRequest.Of(CanonicalSemanticExperiment.Baseline));
        Assert.NotEqual(CanonicalArtifactHash.OfText(v1Prompt), CanonicalArtifactHash.OfText(v2Prompt));

        // The model is no longer asked for a mode, in the schema or in the words.
        Assert.DoesNotContain("selectionMode", JsonSerializer.Serialize(v2.SchemaFactory()), StringComparison.Ordinal);
        foreach (var mode in new[] { "WHOLE_ALIAS", "VERBATIM_TEXT", "selectionMode" })
            Assert.DoesNotContain(mode, v2Prompt, StringComparison.Ordinal);

        // ---- request authority through the real routed path --------------------------------------
        var v1Requests = Compose(plan, v1);
        var v2Requests = Compose(plan, v2);
        Assert.Equal(v1Requests.Keys, v2Requests.Keys);
        Assert.Equal(v2Requests.Values, Compose(plan, v2).Values);   // deterministic

        var changed = v1Requests.Count(pair => pair.Value != v2Requests[pair.Key]);
        Assert.Equal(v1Requests.Count, changed);                      // every pack carries the new schema

        FreezeArtifact.AssertJson(ArtifactRoot, "structured-coordinate-canonicalization.v1.json", new
        {
            artifactKind = "a99_structured_coordinate_canonicalization",
            schemaVersion = "a99-structured-coordinate-canonicalization-v1",
            providerCalls = 0,
            modelCalls = 0,

            change = new
            {
                summary = "The source-part selection mode moves from the model's reply to the harness.",
                cause = "A model names an occurrence and quotes the words it means. Whether that quote is "
                    + "the whole atom or a piece of it is a comparison against the source, and the source is "
                    + "the harness's. Asking for it too produced replies that were right about meaning and "
                    + "refused on shape.",
                evidence = "EXP_MASTHEAD_METADATA r1 PACK_005: four headings named and quoted correctly, all "
                    + "four refused as UnexpectedVerbatimText, all four scored as false negatives.",
                rejectedAlternative = "Letting the binder ignore verbatimText when the reply says WHOLE_ALIAS. "
                    + "That hides the contradiction inside the component whose strictness is the guarantee; "
                    + "canonicalization resolves it before the binder is reached.",
            },

            fieldOwnership = new
            {
                model = new[] { "sourceAlias", "verbatimText (only when part of an occurrence is meant)" },
                harness = new[]
                {
                    "selectionMode (WHOLE_ALIAS or VERBATIM_TEXT)",
                    "exact UTF-16 start and end offsets",
                    "ordered multi-part identity",
                    "refusal when a quote is absent or ambiguous",
                },
            },

            contracts = new
            {
                v1 = new
                {
                    protocolVersion = v1.ProtocolVersion,
                    schemaSha256 = v1.SchemaHash(),
                    bindingId = v1.Binding.BindingId,
                    promptSha256 = CanonicalArtifactHash.OfText(v1Prompt),
                    status = "historical authority, unchanged; every recorded run remains readable against it",
                },
                v2 = new
                {
                    protocolVersion = v2.ProtocolVersion,
                    schemaSha256 = v2.SchemaHash(),
                    bindingId = v2.Binding.BindingId,
                    promptSha256 = CanonicalArtifactHash.OfText(v2Prompt),
                    pendingSelectionMode = SemanticSourcePartCanonicalizer.PendingSelectionMode,
                    pendingModeNote = "Internal only. It never reaches the binder, which still refuses "
                        + "any mode it does not know.",
                },
            },

            canonicalizationRules = new[]
            {
                new { rule = "A", given = "alias, no quote", derived = "WHOLE_ALIAS" },
                new { rule = "B", given = "quote equals the whole atom", derived = "WHOLE_ALIAS, quote dropped" },
                new { rule = "C", given = "quote is a proper substring, locatable", derived = "VERBATIM_TEXT" },
                new { rule = "D", given = "quote does not occur in the atom", derived = "refused: TextNotInAtom" },
                new { rule = "E", given = "quote occurs more than once, nothing says which",
                      derived = "refused: AmbiguousSelection" },
            },

            proofs = new
            {
                goldDerivationParity = new
                {
                    document = "DOC-0252",
                    goldSha256 = GoldSha256,
                    claims = 41,
                    parts = 42,
                    partsWhoseModeWasRederived = 42,
                    changedModes = 0,
                    changedIdentities = 0,
                    changedCoordinates = 0,
                    ambiguous = 0,
                    refused = 0,
                    note = "The primary invariant. Every approved part's recorded mode is recovered by "
                        + "comparison alone, with no counterexample in the corpus.",
                },
                e1Counterfactual = new
                {
                    experiment = "EXP_MASTHEAD_METADATA",
                    pack = "COHERENT_REGION_SEGMENTATION_V1:PACK_005",
                    repeat = "r1",
                    aliases = new[] { "L0400:S0", "L0420:S0", "L0470:S0", "L0507:S0" },
                    refusedUnderV1 = 4,
                    boundUnderV2 = 4,
                    unexpectedVerbatimTextUnderV2 = 0,
                    isRescore = false,
                    note = "A coordinate-shape replay over bytes already captured. 463a758 still records "
                        + "4 refused and 4 false negatives, because that is what the contract it ran under "
                        + "produced. This does not rewrite it.",
                },
                contractLayerConsistency = new
                {
                    before = true,
                    after = false,
                    layersPermittingWholeAliasWithVerbatimText = 0,
                    note = "Under v1 four layers admitted the shape the binder refused. Under v2 no layer "
                        + "offers it, because no layer names a mode.",
                },
            },

            requestAuthority = new
            {
                document = "DOC-0252",
                sourceUniverseSha256 = SourceUniverseSha256,
                packingPolicy = SemanticEvidencePackingPolicies.Default.PolicyId,
                packs = v1Requests.Count,
                packsChanged = changed,
                v1 = Hashes(v1Requests),
                v2 = Hashes(v2Requests),
                v1ProviderModelInputPlanSha256 = PlanHash(v1Requests),
                v2ProviderModelInputPlanSha256 = PlanHash(v2Requests),
                note = "Every pack's bytes change, because the schema on the wire changed. That is the "
                    + "intended rebaseline and not a regression: v2 has no recorded runs to preserve.",
            },

            nonRegression = new
            {
                docxAliasSpan = "unchanged",
                pdfLegacyOccurrence = "unchanged",
                v1StructuredBinder = "unchanged; v2 delegates to it rather than reimplementing resolution",
                goldClaims = "unmodified",
                packingPolicies = "unmodified",
                evaluator = "unmodified",
                historicalTransportCaptures = "unmodified",
                semanticHeadingPolicy = "unmodified",
                relationPolicy = "unmodified",
            },

            scope = "A coordinate-encoding change. It does not make the model better at deciding which text "
                + "is a heading, and it is not evidence about the three semantic mechanisms. It removes one "
                + "class of loss in which a correct semantic decision was discarded for its shape.",
        });
    }

    [Fact]
    public void The_v2_route_reaches_the_same_binder_as_v1()
    {
        // v2 adds no second answer to where a heading is; it prepares the question differently.
        Assert.Equal("STRUCTURED_SOURCE_PARTS_V2",
            SemanticCoordinateContract.PdfStructuredSourcePartsV2.Binding.BindingId);

        var plan = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Doc0252Pdf));
        var parts = new[] { new SemanticSourcePart("L0359:S0", SemanticSourcePartCanonicalizer.PendingSelectionMode) };
        var canonical = SemanticSourcePartCanonicalizer.Canonicalize(plan.Atoms, parts);

        var viaBinder = SemanticSourcePartBinder.Bind(plan.Atoms, new SemanticSourcePartsProposal(canonical.Parts));
        Assert.True(viaBinder.IsBound);
    }

    private static Dictionary<string, string> Compose(
        PdfStructuredSourceAuthority plan, SemanticCoordinateContract contract)
    {
        var model = new CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel(
            new UnreachableClassifier(), contract, HistoricalRequest.Of(CanonicalSemanticExperiment.Baseline));

        return model.ComposeRequests(plan.CreateProductionInput("DOC-0252"))
            .ToDictionary(segment => segment.PackId, segment => segment.RequestBytes, StringComparer.Ordinal);
    }

    private static Dictionary<string, string> Hashes(Dictionary<string, string> requests) =>
        requests.ToDictionary(
            pair => pair.Key, pair => CanonicalSemanticRequestComposer.Hash(pair.Value), StringComparer.Ordinal);

    private static string PlanHash(Dictionary<string, string> requests) =>
        CanonicalSemanticRequestComposer.Hash(
            string.Join("\u0000", requests.Values.Select(CanonicalSemanticRequestComposer.Hash)));

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
