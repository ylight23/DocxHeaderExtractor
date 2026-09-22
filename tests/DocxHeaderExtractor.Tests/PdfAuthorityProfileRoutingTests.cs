using System.Reflection;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Policy;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Proves the profile parameter actually routes, through the real entry point
/// <see cref="CanonicalSemanticPdfAuthorityAdapter.RunAsync"/> - not the builders directly.
/// <para>
/// Two claims, both load-bearing for "route PDF execution by authority profile": omitting the
/// profile (every existing caller, including ordinary Web/CLI/MCP uploads) reproduces the exact
/// legacy runtime source-universe hash already frozen by
/// <see cref="PdfS2eRuntimeAuthorityTests"/>; supplying <c>StructuredSourceParts</c> reproduces the
/// three structured authority hashes and the provider-bound request-byte hash already frozen by
/// <see cref="PdfStructuredSourceAuthorityBuilderTests"/> - reached this time by giving the adapter
/// a recording, transport-free classifier and letting it build everything itself.
/// </para>
/// </summary>
public sealed class PdfAuthorityProfileRoutingTests
{
    private const string Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";

    private const string LegacyRuntimeSourceUniverseHash =
        "cb3c9af67a7f17fd9560b56cf23bb9648a9fcfe3ea4eb5d333ac7282176bfc66";

    private const string StructuredSourceAliasUniverseHash =
        "2a953bf785ed1af00bc908ff9e5d6a1d988b04c0d980ecd95336bc5a9702f46f";
    private const string StructuredProviderModelInputPlanHash =
        "a73e9e3c1fbeb4a83f1937e864fb310e4a0402b993252b82bb6a741ce4ff7dc3";

    [Fact]
    public async Task Omitting_the_profile_keeps_the_frozen_legacy_source_universe_identity()
    {
        var path = Path(Pdf);
        var universe = PdfCanonicalSourceUniverseBuilder.Build(path);
        Assert.Equal(LegacyRuntimeSourceUniverseHash, universe.SourceUniverseSha256);

        // The adapter's own default, reached the same way every existing caller reaches it: by
        // not supplying a profile at all.
        using var recording = new RequestCapturingClassifier();
        await CanonicalSemanticPdfAuthorityAdapter.RunAsync(path, recording, CancellationToken.None);

        Assert.NotEmpty(recording.Requests);
        // No request carries the structured contract's marker clause - proof the legacy contract,
        // not the structured one, was what actually got sent.
        Assert.All(recording.Requests, request =>
            Assert.DoesNotContain("sourceParts", request.UserMessage, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Structured_profile_reaches_the_frozen_provider_model_input_plan_through_the_real_adapter()
    {
        var path = Path(Pdf);
        var plan = PdfStructuredSourceAuthorityBuilder.Build(path);
        Assert.Equal(StructuredSourceAliasUniverseHash, plan.SourceUniverseSha256);

        using var recording = new RequestCapturingClassifier();
        await CanonicalSemanticPdfAuthorityAdapter.RunAsync(
            path, recording, CancellationToken.None,
            profile: PdfSemanticAuthorityProfile.StructuredSourceParts);

        Assert.NotEmpty(recording.Requests);
        Assert.All(recording.Requests, request =>
            Assert.Contains("sourceParts", request.UserMessage, StringComparison.Ordinal));

        var providerModelInputPlanHash = CanonicalSemanticRequestComposer.Hash(string.Join(
            "\u0000", recording.Requests.Select(request => CanonicalSemanticRequestComposer.Hash(request.UserMessage))));
        Assert.Equal(StructuredProviderModelInputPlanHash, providerModelInputPlanHash);
    }

    // ---- §21: successor call-plan recomputation through the actual routed execution paths --------

    private const string Doc0001Docx = "bench/01-style-chuan.docx";
    private const int Repeats = 3;
    private const int MaxProviderCallsProposed = 27;

    [Fact]
    public async Task Successor_call_plan_recomputes_through_real_routing_and_stays_within_the_proposed_cap()
    {
        var docxPrimaryCallsPerRepeat = await Doc0001PrimaryCallsAsync();
        var pdfPrimaryCallsPerRepeat = (await Doc0252StructuredRequestsAsync()).Count;
        var total = (docxPrimaryCallsPerRepeat + pdfPrimaryCallsPerRepeat) * Repeats;

        // Recomputed, not hard-coded: prior evidence expected 1 and 6. If either figure moved, this
        // assertion is what would say so, rather than the cap silently absorbing the difference.
        Assert.Equal(1, docxPrimaryCallsPerRepeat);
        Assert.Equal(6, pdfPrimaryCallsPerRepeat);
        Assert.Equal(21, total);
        Assert.True(total <= MaxProviderCallsProposed,
            $"recomputed total {total} exceeds the proposed cap {MaxProviderCallsProposed}");
    }

    [Fact]
    public async Task Structured_experiment_route_reaches_the_frozen_provider_model_input_plan_six_of_six()
    {
        var requests = await Doc0252StructuredRequestsAsync();

        Assert.Equal(6, requests.Count);
        var providerModelInputPlanHash = CanonicalSemanticRequestComposer.Hash(string.Join(
            "\u0000", requests.Select(request => CanonicalSemanticRequestComposer.Hash(request.UserMessage))));
        Assert.Equal(StructuredProviderModelInputPlanHash, providerModelInputPlanHash);
    }

    private static async Task<int> Doc0001PrimaryCallsAsync()
    {
        var docxPath = Path(Doc0001Docx);
        var source = new OpenXmlDocumentSource().Read(docxPath);
        var features = NumberingStyleFeatures.FromSourceDocument(source);
        var derived = new DocumentFeatureDeriver().Derive(source);
        var built = DocxPolicyStateBuilder.Build(source, features, derived, new ExtractionOptions());
        var state = new DocxPolicyState(source, features, derived, built.Paragraphs, built.StyleTrust);
        var mode = DocumentModeClassifier.Measure(state.Paragraphs.Cast<IPolicyParagraph>().ToArray());

        using var recording = new RequestCapturingClassifier();
        await CanonicalSemanticDocxAuthorityAdapter.RunAsync(state, mode, recording, CancellationToken.None);
        return recording.Requests.Count;
    }

    private static async Task<IReadOnlyList<CapturedRequest>> Doc0252StructuredRequestsAsync()
    {
        var path = Path(Pdf);
        using var recording = new RequestCapturingClassifier();
        await CanonicalSemanticPdfAuthorityAdapter.RunAsync(
            path, recording, CancellationToken.None,
            profile: PdfSemanticAuthorityProfile.StructuredSourceParts);
        return recording.Requests;
    }

    // ---- §24: exhaustive authority-discovery invariants -------------------------------------------

    [Fact]
    public void Exactly_two_pdf_coordinate_profiles_are_registered_and_legacy_is_the_default()
    {
        var profileType = typeof(PdfSemanticAuthorityProfile);
        var staticInstances = profileType
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == profileType)
            .Select(field => (PdfSemanticAuthorityProfile)field.GetValue(null)!)
            .ToArray();

        Assert.Equal(2, staticInstances.Length);
        Assert.Contains(staticInstances, profile => profile.ProfileId == "LEGACY_OCCURRENCE");
        Assert.Contains(staticInstances, profile => profile.ProfileId == "STRUCTURED_SOURCE_PARTS");
        Assert.Equal(2, staticInstances.Select(profile => profile.ProfileId).Distinct(StringComparer.Ordinal).Count());

        // The default the adapter falls back to when no profile is supplied - proven behaviourally
        // by Omitting_the_profile_keeps_the_frozen_legacy_source_universe_identity above; this pins
        // the identity of that default so the two checks cannot silently drift apart.
        Assert.Same(PdfSemanticAuthorityProfile.LegacyOccurrence,
            staticInstances.Single(profile => profile.ProfileId == "LEGACY_OCCURRENCE"));
    }

    [Fact]
    public void Both_pdf_source_universes_implement_the_one_shared_semantic_core_seam()
    {
        // ACTIVE_PDF_PROFILE_ROUTER_IMPLEMENTATIONS = 1: one adapter switches on profile id; both
        // universes it can build satisfy the same interface the semantic core downstream of that
        // switch runs against, so there is exactly one place the routing decision is made.
        Assert.Contains(typeof(IPdfSemanticSourceAuthority), typeof(PdfCanonicalSourceUniverse).GetInterfaces());
        Assert.Contains(typeof(IPdfSemanticSourceAuthority), typeof(PdfStructuredSourceAuthority).GetInterfaces());
    }

    [Fact]
    public void The_retired_synthetic_request_plan_hash_is_not_an_active_gate_anywhere_in_this_file()
    {
        // Named, not merely absent: 97952bd1... was the predecessor SYNTHETIC_REQUEST_PLAN hash
        // superseded by CanonicalSemanticRequestComposer (see PdfStructuredSourceAuthorityBuilderTests,
        // "requestAuthorityCorrection"). It must never reappear as a value this file compares against.
        const string RetiredSyntheticRequestPlanHash =
            "97952bd190c4ffffb77f30e12924efaf648c92d840228169a0c9adec74dd6d1d";
        Assert.DoesNotContain(RetiredSyntheticRequestPlanHash, StructuredProviderModelInputPlanHash, StringComparison.Ordinal);
        Assert.NotEqual(RetiredSyntheticRequestPlanHash, StructuredProviderModelInputPlanHash);
    }

    [Fact]
    public void The_structured_evidence_pack_exposes_only_the_partition_no_second_request_serializer()
    {
        // FINAL_MODEL_INPUT_PRODUCERS = 1: re-asserted from this file's own routing angle, alongside
        // CanonicalSemanticRequestComposerTests' assembly-wide reflection proof.
        var packType = typeof(PdfStructuredSourceAuthorityBuilder).Assembly
            .GetType("DocxHeaderExtractor.DocumentProcessing.Pipeline.PdfStructuredEvidencePack")!;
        var fieldNames = packType.GetProperties().Select(property => property.Name).ToArray();
        Assert.DoesNotContain("RequestPayload", fieldNames);
        Assert.DoesNotContain("RequestSha256", fieldNames);
    }

    [Fact]
    public void No_active_segment_atom_alias_uses_the_retired_running_number_scheme()
    {
        // ACTIVE_PDF_SEGMENT_ALIAS_SCHEMES = 1: L{row}:S{segment}. The retired S0001.. scheme has no
        // builder left to produce it (PdfStructuredSourceAuthorityBuilderTests already proves the
        // one remaining sample is historical, not reachable); this checks the live atoms directly.
        var plan = PdfStructuredSourceAuthorityBuilder.Build(Path(Pdf));
        Assert.NotEmpty(plan.Atoms);
        Assert.All(plan.Atoms, atom => Assert.Matches(@"^L\d{4}:S\d+$", atom.Alias));
        Assert.DoesNotContain(plan.Atoms, atom => System.Text.RegularExpressions.Regex.IsMatch(atom.Alias, @"^S\d{4}$"));
    }

    [Fact]
    public void Coherent_region_policy_builds_the_generic_eight_pack_successor_without_source_loss()
    {
        var plan = PdfStructuredSourceAuthorityBuilder.Build(Path(Pdf));
        var policy = SemanticEvidencePackingPolicies.CoherentRegionSegmentationV1;
        var packs = policy.BuildPacks(plan.Evidence, plan.LayoutBlockByAtom);

        Assert.Equal(SemanticEvidencePackingPolicies.CoherentRegionSegmentationV1Id, policy.PolicyId);
        Assert.Equal(8, packs.Count);
        Assert.Equal(
            new[] { (0, 37), (38, 157), (158, 277), (278, 397), (398, 516), (517, 573), (574, 627), (628, 649) },
            packs.Select(pack => (pack.Owned[0].SourceOrdinal, pack.Owned[^1].SourceOrdinal)));
        Assert.Equal(650, packs.SelectMany(pack => pack.Owned).Count());
        Assert.Equal(650, packs.SelectMany(pack => pack.Owned)
            .Select(item => item.SourceId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("2a953bf785ed1af00bc908ff9e5d6a1d988b04c0d980ecd95336bc5a9702f46f",
            plan.SourceUniverseSha256);
    }

    [Fact]
    public async Task Targeted_successor_packs_capture_structured_requests_through_the_real_adapter()
    {
        var plan = PdfStructuredSourceAuthorityBuilder.Build(Path(Pdf));
        var policy = SemanticEvidencePackingPolicies.CoherentRegionSegmentationV1;
        var targetPackIds = policy.BuildPacks(plan.Evidence, plan.LayoutBlockByAtom)
            .Skip(4).Take(3).Select(pack => pack.PackId).ToHashSet(StringComparer.Ordinal);

        using var recording = new RequestCapturingClassifier();
        await CanonicalSemanticPdfAuthorityAdapter.RunAsync(
            Path(Pdf), recording, CancellationToken.None,
            profile: PdfSemanticAuthorityProfile.StructuredSourceParts,
            packingPolicy: policy,
            selectedPackIds: targetPackIds,
            runPlacement: false);

        Assert.Equal(3, recording.Requests.Count);
        Assert.Equal(new[] { 119, 57, 54 }, recording.Requests.Select(request => request.ExpectedItemCount));
        Assert.All(recording.Requests, request =>
            Assert.Equal(
                "2207221eb8782c13296023aefe5b3cfe9a771eba652f029745948e2534fe580e",
                CanonicalArtifactHash.OfText(request.SystemPrompt)));
        Assert.Equal(
            new[]
            {
                "21d4edc1895351d81c1fb79a072f7e863b0c321ad98012aab39a1977798c13da",
                "3a7f173d774f9ae594344c82f1954098812a01af19227a76234c5419b4d16f96",
                "cedecb1b8241abc4e08fdb4c089da0ca38550c8350d600de9f6392598533a055",
            },
            recording.Requests.Select(request => CanonicalSemanticRequestComposer.Hash(request.UserMessage)));
        Assert.All(recording.Requests, request => Assert.Contains("sourceParts", request.UserMessage));
        Assert.All(recording.Requests, request => Assert.DoesNotContain("S0001", request.UserMessage));
    }

    private static string Path(string relativePath) =>
        System.IO.Path.Combine(TestRepository.Root(), relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
}
