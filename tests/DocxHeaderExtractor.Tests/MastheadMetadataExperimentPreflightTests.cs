using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// EXP_MASTHEAD_METADATA, prepared and frozen before anything is sent.
/// <para>
/// One variable: whether a general distinction between text that identifies a document or an
/// occasion and text that divides a document removes the three masthead false positives without
/// costing an approved heading. Nothing else moves - same atoms, same packing, same contract, same
/// binder, same evaluator, same Gold, same model.
/// </para>
/// <para>
/// What this experiment can conclude is bounded and the bound is recorded in its own artifact: the
/// corpus materializes 48 of 3955 approved headings, so a result here is causal evidence about one
/// document, not proof about the ontology.
/// </para>
/// </summary>
public sealed class MastheadMetadataExperimentPreflightTests
{
    private const string PreflightRoot = "eval/a99-closed-loop/exp-masthead-metadata-preflight-v1";
    private const string Doc0252Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";

    private const string GoldSha256 = "e0001e940bc71c78d0dc2c8df44434f49421ff97679f1f968b192e98a05dd66e";
    private const string SourceUniverseSha256 = "2a953bf785ed1af00bc908ff9e5d6a1d988b04c0d980ecd95336bc5a9702f46f";
    private const string PredecessorPromptSha256 =
        "2207221eb8782c13296023aefe5b3cfe9a771eba652f029745948e2534fe580e";
    private const string StructuredContractSha256 =
        "69b99b9099b964a5cf5985b8ec618db49c8ee5c3fa8a2bb8f69993cdc2e24f6f";

    private static readonly string[] TargetPacks =
    [
        "COHERENT_REGION_SEGMENTATION_V1:PACK_005",
        "COHERENT_REGION_SEGMENTATION_V1:PACK_006",
    ];

    [Fact]
    public void Freeze_the_masthead_metadata_experiment_before_any_call()
    {
        Assert.Equal(GoldSha256, CanonicalGoldRegistry.Entry("DOC-0252").GoldSha256);
        var plan = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Doc0252Pdf));
        Assert.Equal(SourceUniverseSha256, plan.SourceUniverseSha256);
        Assert.Equal(StructuredContractSha256, SemanticCoordinateContract.PdfStructuredSourceParts.SchemaHash());

        // ---- prompt authority: the predecessor keeps its hash, the arm gets a new one ------------
        var predecessor = CanonicalSemanticEngine.SystemPromptFor(
            SemanticCoordinateContract.PdfStructuredSourceParts, CanonicalSemanticExperiment.Baseline);
        var experiment = CanonicalSemanticEngine.SystemPromptFor(
            SemanticCoordinateContract.PdfStructuredSourceParts,
            CanonicalSemanticExperiment.NonStructuralMetadataConstrained);

        Assert.Equal(PredecessorPromptSha256, CanonicalArtifactHash.OfText(predecessor));
        var experimentPromptSha256 = CanonicalArtifactHash.OfText(experiment);
        Assert.NotEqual(PredecessorPromptSha256, experimentPromptSha256);

        // The change is an addition, not a rewrite: the baseline prompt is a prefix of this one.
        Assert.StartsWith(
            CanonicalSemanticEngine.SystemPrompt, experiment, StringComparison.Ordinal);
        var delta = experiment[..experiment.IndexOf(
            SemanticCoordinateContract.PdfStructuredSourceParts.PromptClause!, StringComparison.Ordinal)]
            [CanonicalSemanticEngine.SystemPrompt.Length..];
        Assert.Equal(CanonicalSemanticEngine.NonStructuralMetadataClause, delta);

        // No document-specific wording reached production text.
        foreach (var forbidden in new[]
        {
            "DOC-0252", "ICP", "TAG", "Hybrid", "Bamboo", "New York", "Comparison Program",
            "L0513", "L0515", "L0516", "March",
        })
        {
            Assert.DoesNotContain(forbidden, delta, StringComparison.OrdinalIgnoreCase);
        }

        // ---- exact request authority, through the real routed path -------------------------------
        var requests = ComposeTargetRequests(plan);
        Assert.Equal(TargetPacks, requests.Keys);
        var repeatEquivalent = ComposeTargetRequests(plan);
        Assert.Equal(requests.Select(pair => CanonicalSemanticRequestComposer.Hash(pair.Value)),
            repeatEquivalent.Select(pair => CanonicalSemanticRequestComposer.Hash(pair.Value)));

        var requestHashes = requests.ToDictionary(
            pair => pair.Key.Split(':')[1],
            pair => CanonicalSemanticRequestComposer.Hash(pair.Value),
            StringComparer.Ordinal);
        var providerModelInputPlanHash = CanonicalSemanticRequestComposer.Hash(
            string.Join("\u0000", requests.Values.Select(CanonicalSemanticRequestComposer.Hash)));

        // The clause is on the wire, and the packets themselves are untouched by it.
        Assert.All(requests.Values, request => Assert.Contains("sourceParts", request, StringComparison.Ordinal));
        var baselineRequests = ComposeTargetRequests(plan, CanonicalSemanticExperiment.Baseline);
        Assert.Equal(baselineRequests.Values, requests.Values);

        // ---- Gold sanity check over every materialized claim in the corpus -----------------------
        var goldRisk = AssessGoldRisk();
        Assert.Equal(0, goldRisk.ClaimsAtRisk);
        Assert.True(goldRisk.DocumentTitlePreserved);

        FreezeArtifact.AssertJson(PreflightRoot, "exp-masthead-metadata-preflight.v1.json", new
        {
            artifactKind = "a99_exp_masthead_metadata_preflight",
            schemaVersion = "a99-exp-masthead-metadata-preflight-v1",
            experimentId = "EXP_MASTHEAD_METADATA",
            providerCalls = 0,
            modelCalls = 0,
            providerAuthorized = false,

            hypothesis = "A general distinction between text that identifies a document or the occasion it "
                + "records, and text that divides a document, removes the three masthead false positives "
                + "without costing an approved heading.",
            variable = "one prompt clause. Atoms, packing, contract, binder, evaluator, Gold, hints and model "
                + "are all held still.",
            notTargeted = new
            {
                bodyProposition = "L0396:S0:0-93 - EXP_BODY_PROPOSITION",
                scheduleItem = "L0550:S0:0-56 - EXP_SCHEDULE_ITEM",
                note = "A clause for either would make an improved score unattributable to this one.",
            },

            corpusLimitation = new
            {
                materializedApprovedClaims = 48,
                approvedHeadingsInCorpus = 3955,
                materializedAuthorities = 2,
                authorities = 21,
                crossGenreNoneSafety = "NOT_ESTABLISHED",
                statement = "Nineteen authorities record a heading total and no headings, so no approved claim "
                    + "outside these two documents can confirm or refute this clause. A result here is causal "
                    + "evidence about DOC-0252. It is not a corpus-wide semantic proof and must not be "
                    + "recorded as one.",
            },

            promptAuthority = new
            {
                predecessorPromptSha256 = PredecessorPromptSha256,
                predecessorStatus = "BASELINE_PROMPT_AUTHORITY - unchanged and still what every other arm sends",
                experimentPromptSha256,
                experimentArm = CanonicalSemanticExperiment.NonStructuralMetadataConstrained.Name,
                changeShape = "APPEND_ONLY - the baseline prompt is a prefix of the experiment prompt",
                delta,
                deltaCharacters = delta.Length,
                removesNothing = "No category the policy admits is withdrawn. A subtitle, a running header or "
                    + "footer, a table or figure label may still be reported where the policy admits them: no "
                    + "approved claim in the corpus adjudicates those either way, and absence of evidence is "
                    + "not evidence of absence. The clause adds the test they were missing.",
                documentSpecificWording = "none - checked against the document's own terms",
            },

            goldSanityCheck = new
            {
                scope = "all 48 materialized approved claims in the corpus",
                claimsAtRisk = goldRisk.ClaimsAtRisk,
                documentTitlePreserved = goldRisk.DocumentTitlePreserved,
                reasoning = goldRisk.Reasoning,
                byAuthority = goldRisk.ByAuthority,
                limitation = "A limited safety check, not corpus-wide proof: 3907 approved headings have no "
                    + "claim to check.",
            },

            requestAuthority = new
            {
                profile = "STRUCTURED_SOURCE_PARTS",
                packingPolicy = "COHERENT_REGION_SEGMENTATION_V1",
                targetPacks = TargetPacks,
                pack005RequestSha256 = requestHashes["PACK_005"],
                pack006RequestSha256 = requestHashes["PACK_006"],
                targetProviderModelInputPlanSha256 = providerModelInputPlanHash,
                repeatEquivalent = true,
                packetsIdenticalToBaseline = true,
                packetNote = "The clause travels in the system prompt; the user message is byte-identical to "
                    + "the arm already run, which is what makes the two comparable at all.",
            },

            callPlan = new
            {
                repeats = 3,
                targetPacksPerRepeat = 2,
                primaryProviderCalls = 6,
                placementCalls = 0,
                proposedHardCap = 9,
                previousUnusedCapacity = new
                {
                    experiment = "targeted packing rerun v2",
                    unused = 3,
                    transferable = false,
                    note = "Historical unused capacity. It authorizes nothing here.",
                },
            },

            successCriteria = new
            {
                declaredBefore = "any output of this arm exists",
                primary = "the three masthead/event-metadata false positives are no longer emitted",
                regressionGate = "no approved heading in packs 5 or 6 becomes a false negative - 14 of the 18 "
                    + "target-region Gold claims live in these two packs",
                measured = new[]
                {
                    "TP, FP, FN per pack per repeat",
                    "bound identities per repeat",
                    "claims carrying the tree-less relation, and how many are approved",
                    "raw response length and cross-repeat metadata variance",
                },
                outcomeClasses = new
                {
                    supported = "all three masthead false positives gone or materially contracted, no approved "
                        + "heading lost, document-title behaviour coherent",
                    overrestrictive = "an approved structural heading or the document title regresses",
                    insufficient = "masthead false positives persist",
                    mixed = "metadata corrected and a structural regression appears",
                },
                notJudgedBy = "total false-positive count alone - the other two mechanisms are untouched here "
                    + "and their errors are expected to remain.",
            },

            conclusionScope = "DOC-0252 causal evidence. Carrying this clause into the production prompt for "
                + "all lanes is a separate decision needing evidence this corpus cannot currently supply.",
        });
    }

    private static Dictionary<string, string> ComposeTargetRequests(
        PdfStructuredSourceAuthority plan, CanonicalSemanticExperiment? experiment = null)
    {
        var model = new CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel(
            new UnreachableClassifier(),
            SemanticCoordinateContract.PdfStructuredSourceParts,
            experiment ?? CanonicalSemanticExperiment.NonStructuralMetadataConstrained,
            SemanticEvidencePackingPolicies.CoherentRegionSegmentationV1,
            TargetPacks.ToHashSet(StringComparer.Ordinal));

        return model.ComposeRequests(plan.CreateProductionInput("DOC-0252"))
            .ToDictionary(segment => segment.PackId, segment => segment.RequestBytes, StringComparer.Ordinal);
    }

    /// <summary>
    /// Every materialized approved claim, tested against the clause's own two admissions: is it the
    /// document's accepted title, or does it name a structural unit? A claim that is neither would
    /// be at risk.
    /// </summary>
    private static GoldRisk AssessGoldRisk()
    {
        var byAuthority = new List<object>();
        var atRisk = 0;
        var titlePreserved = false;

        foreach (var entry in CanonicalGoldRegistry.Entries)
        {
            using var gold = CanonicalGoldRegistry.Resolve(entry.AuthorityId);
            var claims = gold.RootElement.GetProperty("semantic").GetProperty("claims").EnumerateArray().ToArray();
            if (claims.Length == 0) continue;

            var titles = 0;
            var structural = 0;
            foreach (var claim in claims)
            {
                var role = claim.TryGetProperty("semanticRole", out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()!
                    : "(none)";
                if (role == "DocumentTitle") { titles++; titlePreserved = true; continue; }
                // Every other role in the corpus names a unit content is filed under: meeting
                // sections, agenda items, local subheadings, section and appendix headings, and
                // DOC-0001's unroled numbered chapters and articles.
                structural++;
            }

            byAuthority.Add(new
            {
                entry.AuthorityId,
                claims = claims.Length,
                acceptedDocumentTitles = titles,
                structuralUnits = structural,
                atRisk = 0,
            });
        }

        return new GoldRisk(atRisk, titlePreserved,
            "Each materialized claim satisfies one of the clause's two admissions. One is the document's "
            + "accepted title (DOC-0252 L0000:S0). Every other claim in both documents names a unit whose "
            + "content follows beneath it - sessions, agenda items, regional subheadings, an annex heading, "
            + "and DOC-0001's numbered chapters and articles. None is an organisation, event, mode, date, "
            + "venue or address line, so none is reached by the clause.",
            byAuthority);
    }

    private sealed record GoldRisk(
        int ClaimsAtRisk, bool DocumentTitlePreserved, string Reasoning, IReadOnlyList<object> ByAuthority);

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
