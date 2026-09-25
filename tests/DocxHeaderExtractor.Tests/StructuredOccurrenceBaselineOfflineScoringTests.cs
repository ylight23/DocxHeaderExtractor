using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Re-derives the A99-S2P-STRUCTURED-BASELINE-V1 result from the provider responses that run
/// already captured, after the decoder defect that discarded them was repaired.
/// <para>
/// The 21 provider calls were spent once and are not spent again: <c>raw-responses.v1.json</c>
/// holds every reply verbatim, and everything below reads it. What changed between the run and
/// this file is not the model's answer but the harness's ability to read it - the structured
/// contract asked for <c>sourceParts</c>, the model answered in <c>sourceParts</c>, and the decoder
/// that received the reply belonged to a contract that required a top-level <c>sourceAlias</c>.
/// Every heading was dropped, silently, and the run scored zero proposals against replies that were
/// well formed. That zero was a measurement failure, not an accuracy result.
/// </para>
/// </summary>
public sealed class StructuredOccurrenceBaselineOfflineScoringTests
{
    private const string RunRoot = "eval/a99-closed-loop/occurrence-baseline-structured-v1";
    private const string RawResponses = RunRoot + "/raw-responses.v1.json";
    private const string RawResponsesSha256 =
        "c1122ec1361c7e2895e95261b7c83dd27e4c25933397987ecc4be490833b5df6";

    private const string Doc0252Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private const string Doc0252GoldSha256 =
        "870c06ac4585d89f50496b5ae004f8a06c8072584e163184f817634fe03b468e";

    /// <summary>
    /// The Gold this result was produced against, pinned by path and hash rather than by authority
    /// id. DOC-0252's selections were later corrected for four headings whose migrated coordinates
    /// stopped one character short of their own approved wording, which moved the authority's hash;
    /// resolving by id here would score a finished run against a Gold it never ran against.
    /// </summary>
    private const string PredecessorGoldPath =
        "eval/a99-closed-loop/gold-current/documents/DOC-0252.structured-boundary-predecessor.gold.v1.json";
    private const string EvaluatorId = "a99-pdf-gold-evaluator-v4-structured-source-parts-semantic-role";

    private const int Repeats = 3;
    private const int Doc0252CallsPerRepeat = 6;
    private const int Doc0001CallsPerRepeat = 1;

    // ---- the defect, reproduced from the evidence that produced it ------------------------------

    [Fact]
    public void A_captured_structured_reply_is_undecodable_by_the_alias_scalar_contract()
    {
        var heading = FirstCapturedStructuredHeading();

        // Validation passes: the reply carries no fabricated numeric coordinate and is well formed.
        Assert.Empty(SemanticCoordinateContract.PdfStructuredSourceParts.Validate(
            JsonDocument.Parse(CapturedResponses("DOC-0252")[0]).RootElement));

        // The entry addresses its claim the way the structured schema asked, and carries no
        // top-level sourceAlias at all.
        Assert.True(heading.TryGetProperty("sourceParts", out var parts));
        Assert.Equal(JsonValueKind.Array, parts.ValueKind);
        Assert.False(heading.TryGetProperty("sourceAlias", out _));

        // Read by the contract that issued the schema: one proposal, no failure.
        var structured = SemanticCoordinateContract.PdfStructuredSourceParts.Decode(heading);
        Assert.Single(structured.Proposals);
        Assert.Empty(structured.Failures);

        // Read by the alias-scalar contract's decoder - what production did - nothing survives, and
        // this is now a reported failure rather than a silent null.
        var legacy = SemanticCoordinateContract.PdfAliasSelection.Decode(heading);
        Assert.Empty(legacy.Proposals);
        Assert.Equal("ALIAS_SCALAR_ENTRY_UNREADABLE", Assert.Single(legacy.Failures).Code);
    }

    [Fact]
    public void A_legacy_reply_is_not_rescued_by_the_structured_decoder()
    {
        // The mirror of the defect, which must also be impossible: a reply in the alias-scalar
        // shape read under the structured contract fails visibly instead of half-decoding.
        using var document = JsonDocument.Parse(
            """{"isHeading":true,"sourceAlias":"S0001","selectionMode":"WHOLE_ALIAS"}""");

        var structured = SemanticCoordinateContract.PdfStructuredSourceParts.Decode(document.RootElement);

        Assert.Empty(structured.Proposals);
        Assert.Equal("STRUCTURED_ENTRY_MISSING_SOURCE_PARTS", Assert.Single(structured.Failures).Code);
    }

    [Fact]
    public void A_structured_reply_carrying_source_parts_does_not_make_a_legacy_contract_structured()
    {
        // Presence of a field is not a routing signal. Under a legacy contract, an entry that is
        // missing what that contract requires stays a failure even though it happens to carry
        // sourceParts - otherwise the contract in force would be decided by the model.
        using var document = JsonDocument.Parse(
            """{"isHeading":true,"sourceParts":[{"sourceAlias":"L0000:S0","selectionMode":"WHOLE_ALIAS"}]}""");

        var legacy = SemanticCoordinateContract.DocxAliasSpan.Decode(document.RootElement);

        Assert.Empty(legacy.Proposals);
        Assert.Equal("ALIAS_SCALAR_ENTRY_UNREADABLE", Assert.Single(legacy.Failures).Code);
    }

    [Fact]
    public void A_two_part_claim_survives_decoding_whole()
    {
        // S0616's shape, deterministically, whether or not the captured run proposed it: a heading
        // that wraps across two atoms. Collapsing it to its first part would migrate forward the
        // exact truncation the structured coordinate system exists to remove.
        using var document = JsonDocument.Parse("""
            {"isHeading":true,"semanticRole":"section-heading","sourceParts":[
              {"sourceAlias":"L0359:S0","selectionMode":"VERBATIM_TEXT","verbatimText":"2. A Survey Based Approach"},
              {"sourceAlias":"L0360:S0","selectionMode":"WHOLE_ALIAS"}]}
            """);

        var decoded = SemanticCoordinateContract.PdfStructuredSourceParts.Decode(document.RootElement);
        var proposal = Assert.Single(decoded.Proposals);

        Assert.Equal(2, proposal.SourceParts!.Count);
        Assert.Equal("L0359:S0", proposal.SourceParts[0].SourceAlias);
        Assert.Equal("VERBATIM_TEXT", proposal.SourceParts[0].SelectionMode);
        Assert.Equal("2. A Survey Based Approach", proposal.SourceParts[0].VerbatimText);
        Assert.Equal("L0360:S0", proposal.SourceParts[1].SourceAlias);
        Assert.Equal("WHOLE_ALIAS", proposal.SourceParts[1].SelectionMode);
        Assert.Equal(["L0359:S0", "L0360:S0"], proposal.SourceAliases);
    }

    // ---- offline rescore from the captured responses ---------------------------------------------

    [Fact]
    public void Rescore_the_structured_baseline_from_the_captured_responses()
    {
        Assert.Equal(RawResponsesSha256, CanonicalArtifactHash.OfTextFile(TestRepository.Path(RawResponses)));

        var plan = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Doc0252Pdf));
        var goldIdentities = GoldIdentities();
        Assert.Equal(41, goldIdentities.Count);

        var doc0252 = CapturedResponses("DOC-0252");
        Assert.Equal(Repeats * Doc0252CallsPerRepeat, doc0252.Count);

        var repeats = new List<object>();
        var microTp = 0;
        var microFp = 0;
        var microFn = 0;

        for (var repeat = 0; repeat < Repeats; repeat++)
        {
            var validated = 0;
            var headingProposals = 0;
            var nonHeadingProposals = 0;
            var materializationFailures = 0;
            var rejections = new Dictionary<string, int>(StringComparer.Ordinal);
            var predicted = new HashSet<string>(StringComparer.Ordinal);

            for (var pack = 0; pack < Doc0252CallsPerRepeat; pack++)
            {
                var owned = plan.Packs[pack].OwnedAliases.ToHashSet(StringComparer.Ordinal);
                using var document = JsonDocument.Parse(doc0252[(repeat * Doc0252CallsPerRepeat) + pack]);

                Assert.Empty(SemanticCoordinateContract.PdfStructuredSourceParts.Validate(document.RootElement));
                validated++;

                foreach (var element in document.RootElement.GetProperty("headings").EnumerateArray())
                {
                    var decoded = SemanticCoordinateContract.PdfStructuredSourceParts.Decode(element);
                    materializationFailures += decoded.Failures.Count;

                    foreach (var proposal in decoded.Proposals)
                    {
                        if (!proposal.IsHeading) { nonHeadingProposals++; continue; }
                        headingProposals++;

                        // The same ownership rule the engine applies: a segment may read its
                        // neighbours but may only claim what it owns.
                        if (!owned.Contains(proposal.SourceAlias)) { Count(rejections, "OutOfOwnedSegment"); continue; }

                        var binding = SemanticSourcePartBinder.Bind(
                            plan.Atoms, new SemanticSourcePartsProposal(proposal.SourceParts!));
                        if (!binding.IsBound) { Count(rejections, binding.Status.ToString()); continue; }
                        predicted.Add(binding.Identity);
                    }
                }
            }

            Assert.Equal(Doc0252CallsPerRepeat, validated);
            Assert.Equal(0, materializationFailures);

            var truePositive = predicted.Count(identity => goldIdentities.Contains(identity));
            var falsePositive = predicted.Count - truePositive;
            var falseNegative = goldIdentities.Count - truePositive;
            microTp += truePositive;
            microFp += falsePositive;
            microFn += falseNegative;

            repeats.Add(new
            {
                repeat = repeat + 1,
                rawResponses = Doc0252CallsPerRepeat,
                validatedResponses = validated,
                headingProposals,
                nonHeadingProposals,
                materializationFailures,
                bound = predicted.Count,
                rejected = rejections.Values.Sum(),
                rejectionReasons = rejections.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                truePositive,
                falsePositive,
                falseNegative,
                precision = Round(Ratio(truePositive, truePositive + falsePositive)),
                recall = Round(Ratio(truePositive, truePositive + falseNegative)),
                f1 = Round(F1(Ratio(truePositive, truePositive + falsePositive),
                    Ratio(truePositive, truePositive + falseNegative))),
            });
        }

        var precision = Ratio(microTp, microTp + microFp);
        var recall = Ratio(microTp, microTp + microFn);

        FreezeArtifact.AssertJson(RunRoot, "offline-score.v1.json", new
        {
            artifactKind = "a99_occurrence_baseline_score",
            schemaVersion = "a99-occurrence-baseline-score-v1",
            experimentId = "A99-S2P-STRUCTURED-BASELINE-V1",
            scoredFrom = "persisted provider responses (raw-responses.v1.json)",
            providerCalls = 0,
            modelCalls = 0,
            evaluatorId = EvaluatorId,
            authorityProfile = "STRUCTURED_SOURCE_PARTS",

            correction = new
            {
                predecessorScoreStatus = "INVALIDATED_BY_STRUCTURED_PROPOSAL_MATERIALIZATION_DEFECT",
                rootCause = "STRUCTURED_CONTRACT_EMITS_SOURCEPARTS_BUT_LEGACY_PARSER_REQUIRED_TOP_LEVEL_SOURCEALIAS",
                note = "The run recorded 0 proposals for DOC-0252 in all three repeats. The model had "
                    + "answered in the shape the structured contract asked for; the engine decoded every "
                    + "reply with the alias-scalar contract's parser, which requires a top-level "
                    + "sourceAlias, and dropped each entry as unreadable. Decoding is now owned by the "
                    + "contract that issued the schema, and these are the same replies read correctly.",
                rawProviderResponsesChanged = false,
                providerCallsAdded = 0,
                providerRunReused = true,
                rawResponsesSha256 = RawResponsesSha256,
            },

            doc0252 = new
            {
                goldSha256 = Doc0252GoldSha256,
                goldClaims = goldIdentities.Count,
                sourceAliasUniverseSha256 = plan.SourceUniverseSha256,
                repeats,
                micro = new
                {
                    truePositive = microTp,
                    falsePositive = microFp,
                    falseNegative = microFn,
                    precision = Round(precision),
                    recall = Round(recall),
                    f1 = Round(F1(precision, recall)),
                },
            },

            doc0001 = Doc0001Captured(),
        });
    }

    /// <summary>
    /// DOC-0001 is DOCX and its contract did not move. Re-read here only to prove that: the
    /// captured replies decode exactly as they did, through the alias-scalar decoder, and whatever
    /// this run produced is reported rather than rounded up to the count its Gold records.
    /// </summary>
    private static object Doc0001Captured()
    {
        var goldAliases = GoldAliases("DOC-0001");
        var responses = CapturedResponses("DOC-0001");
        Assert.Equal(Repeats * Doc0001CallsPerRepeat, responses.Count);

        var repeats = new List<object>();
        for (var repeat = 0; repeat < Repeats; repeat++)
        {
            using var document = JsonDocument.Parse(responses[repeat]);
            var headings = 0;
            var failures = 0;
            var matched = new HashSet<string>(StringComparer.Ordinal);

            foreach (var element in document.RootElement.GetProperty("headings").EnumerateArray())
            {
                var decoded = SemanticCoordinateContract.DocxAliasSpan.Decode(element);
                failures += decoded.Failures.Count;
                foreach (var proposal in decoded.Proposals.Where(item => item.IsHeading))
                {
                    headings++;
                    if (goldAliases.Contains(proposal.SourceAlias)) matched.Add(proposal.SourceAlias);
                }
            }

            repeats.Add(new
            {
                repeat = repeat + 1,
                headingProposals = headings,
                materializationFailures = failures,
                goldAliasesMatched = matched.Count,
                goldClaims = goldAliases.Count,
            });
        }

        return new { goldClaims = goldAliases.Count, repeats };
    }

    [Fact]
    public async Task The_engine_now_materializes_the_captured_replies_it_previously_dropped()
    {
        // The repaired boundary, exercised through the real engine rather than the scorer: the same
        // captured replies, the real structured contract, a classifier that replays them and never
        // transports. Before the repair this produced zero proposals and reported one
        // UNREADABLE_PROPOSAL per heading; the replies themselves have not changed.
        var plan = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Doc0252Pdf));
        var replies = CapturedResponses("DOC-0252").Take(Doc0252CallsPerRepeat).ToArray();
        using var replay = new FrozenReplyClassifier(replies);
        var model = new CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel(
            replay, SemanticCoordinateContract.PdfStructuredSourceParts);

        var result = await model.InferAsync(
            plan.CreateProductionInput("DOC-0252"), new SemanticContextPacket([], [], []), "offline-replay");

        Assert.Equal(0, replay.CallsBeyondRecording);
        Assert.NotEmpty(result.ParsedProposals!);
        Assert.All(result.ParsedProposals!, proposal => Assert.NotNull(proposal.SourceParts));
        Assert.DoesNotContain(result.ContractIssues,
            issue => issue.Code.StartsWith("STRUCTURED_", StringComparison.Ordinal));
        Assert.DoesNotContain(result.ContractIssues, issue => issue.Code == "ALIAS_SCALAR_ENTRY_UNREADABLE");
    }

    [Fact]
    public async Task Structured_claims_reach_canonical_structure_through_the_production_route()
    {
        // The boundary this used to stop at. Decoding was repaired first and proposals reached the
        // pipeline intact, where a validator and binder written for one alias and one span refused
        // every one of them - the same defect as the decoder's, one stage later. Now the contract
        // that issued the schema also owns the binding, and the captured replies materialize.
        var replies = CapturedResponses("DOC-0252").Take(Doc0252CallsPerRepeat).ToArray();
        using var replay = new FrozenReplyClassifier(replies);

        var authority = await CanonicalSemanticPdfAuthorityAdapter.RunAsync(
            TestRepository.Path(Doc0252Pdf), replay, CancellationToken.None,
            profile: PdfSemanticAuthorityProfile.StructuredSourceParts);

        // Six discovery calls are replayed. Anything past them is the placement pass, which only
        // runs when headings remain unplaced - so before this repair it could not run at all, and
        // its appearance here is itself evidence that claims now reach structural resolution.
        Assert.Equal(Doc0252CallsPerRepeat, replies.Length);
        Assert.NotEmpty(authority.Structure.Elements);

        // The text is the claim's own projection, not an atom's raw text: a heading the parser
        // split across rows reads as one heading here.
        Assert.Contains(authority.Structure.Elements,
            element => element.Text == "MINUTES OF THE INTERNATIONAL COMPARISON PROGRAM");
    }

    [Fact]
    public async Task Production_binding_matches_the_offline_binder_claim_for_claim()
    {
        // One binder, two callers - proven rather than assumed. If the live pipeline ever grew its
        // own interpretation of a structured claim, the baseline score computed offline would stop
        // describing what production does, and nothing else would say so.
        var plan = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Doc0252Pdf));

        for (var repeat = 0; repeat < Repeats; repeat++)
        {
            var replies = CapturedResponses("DOC-0252")
                .Skip(repeat * Doc0252CallsPerRepeat).Take(Doc0252CallsPerRepeat).ToArray();
            using var replay = new FrozenReplyClassifier(replies);
            var model = new CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel(
                replay, SemanticCoordinateContract.PdfStructuredSourceParts);

            var result = await CanonicalSemanticProductionEntryPoint.RunAsync(
                plan.CreateProductionInput("DOC-0252"), model, requestId: $"parity-r{repeat + 1}");

            var production = result.TextPipeline.BoundHeadings
                .Select(heading => string.Join("|", heading.Parts.Select(part => $"{part.Alias}:{part.Start}-{part.End}")))
                .ToHashSet(StringComparer.Ordinal);
            var offline = OfflineBoundIdentities(plan, repeat);

            Assert.Equal(offline, production);
        }
    }

    [Fact]
    public async Task A_two_atom_heading_materializes_through_the_whole_production_route()
    {
        // S0616 end to end, on the real document: the heading whose legacy occurrence was truncated,
        // claimed as the two atoms it actually occupies. Every stage is the production one - decode,
        // validate, bind, resolve, materialize - and the element that comes out carries both parts'
        // text, which is the entire point of the coordinate migration.
        var plan = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Doc0252Pdf));

        // S0616's two atoms, asserted to still be what this document holds rather than trusted:
        // the heading runs to the end of one visual row and finishes on the next.
        var first = plan.Atoms.Single(atom => atom.Alias == "L0359:S0");
        var second = plan.Atoms.Single(atom => atom.Alias == "L0360:S0");
        Assert.StartsWith("2. A Survey Based Approach", first.Text, StringComparison.Ordinal);
        Assert.Equal("Comparisons", second.Text.Trim());

        var pack = plan.Packs.Single(item => item.OwnedAliases.Contains(first.Alias));
        Assert.Contains(second.Alias, pack.OwnedAliases);

        var claim = $$"""
            {"headings":[{"isHeading":true,"semanticRole":"section-heading","relationHints":[],
              "sourceParts":[{"sourceAlias":"{{first.Alias}}","selectionMode":"WHOLE_ALIAS"},
                             {"sourceAlias":"{{second.Alias}}","selectionMode":"WHOLE_ALIAS"}]}]}
            """;
        var replies = plan.Packs
            .Select(item => item.Index == pack.Index ? claim : """{"headings":[]}""")
            .ToArray();

        using var replay = new FrozenReplyClassifier(replies);
        var authority = await CanonicalSemanticPdfAuthorityAdapter.RunAsync(
            TestRepository.Path(Doc0252Pdf), replay, CancellationToken.None,
            profile: PdfSemanticAuthorityProfile.StructuredSourceParts);

        var element = Assert.Single(authority.Structure.Elements);
        Assert.Contains(first.Text.Trim(), element.Text, StringComparison.Ordinal);
        Assert.Contains(second.Text.Trim(), element.Text, StringComparison.Ordinal);

        // Not the first atom alone - the collapse this coordinate system exists to prevent.
        Assert.NotEqual(first.Text, element.Text);
    }

    [Fact]
    public void A_structured_claim_that_cannot_bind_is_refused_by_name_not_dropped()
    {
        // Decodes cleanly, names real atoms, and still cannot be bound: the parts are named against
        // source order (distance alone is no longer a refusal - GENERIC_MULTIPART_BINDER_V2). The
        // refusal keeps the binder's own reason rather than becoming an absence.
        var plan = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Doc0252Pdf));
        var proposal = new CanonicalSemanticProposal(
            "L0300:S0", true, null, SourceParts:
            [
                new SemanticSourcePart("L0300:S0", CanonicalSemanticSelectionMode.WholeAlias),
                new SemanticSourcePart("L0000:S0", CanonicalSemanticSelectionMode.WholeAlias),
            ]);

        var outcome = SemanticCoordinateContract.PdfStructuredSourceParts.BindProposals(
            new SemanticCoordinateBindingRequest([proposal], plan.Aliases, null, plan.Atoms));

        var refusal = SemanticSourcePartBinder.Bind(
            plan.Atoms, new SemanticSourcePartsProposal(proposal.SourceParts!));
        Assert.Equal(SemanticSourcePartsStatus.OutOfSourceOrder, refusal.Status);

        Assert.Empty(outcome.Bound);
        var observation = Assert.Single(outcome.Observations);
        // The binder's own verdict, carried through under its own name rather than flattened.
        Assert.Equal(refusal.Status.ToString(), observation.Reason);
        Assert.NotEqual(CanonicalSemanticBindingStatus.NonHeadingIgnored, observation.Status);
    }

    [Fact]
    public void A_model_saying_not_a_heading_is_distinguishable_from_a_claim_that_would_not_bind()
    {
        var plan = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Doc0252Pdf));
        var declined = new CanonicalSemanticProposal(
            "L0000:S0", false, null, SourceParts:
            [new SemanticSourcePart("L0000:S0", CanonicalSemanticSelectionMode.WholeAlias)]);

        var outcome = SemanticCoordinateContract.PdfStructuredSourceParts.BindProposals(
            new SemanticCoordinateBindingRequest([declined], plan.Aliases, null, plan.Atoms));

        Assert.Empty(outcome.Bound);
        Assert.Equal(CanonicalSemanticBindingStatus.NonHeadingIgnored, Assert.Single(outcome.Observations).Status);
    }

    [Fact]
    public void The_legacy_binding_is_the_one_the_alias_span_contracts_still_use()
    {
        Assert.Equal("ALIAS_SPAN", SemanticCoordinateContract.DocxAliasSpan.Binding.BindingId);
        Assert.Equal("ALIAS_SPAN", SemanticCoordinateContract.PdfAliasSelection.Binding.BindingId);
        Assert.Equal("STRUCTURED_SOURCE_PARTS", SemanticCoordinateContract.PdfStructuredSourceParts.Binding.BindingId);

        // Schema, validator, decoder and binder are one selection. A contract cannot be assembled
        // from one coordinate system's schema and another's binder without saying so here.
        Assert.Same(SemanticCoordinateBinding.AliasSpan, SemanticCoordinateContract.DocxAliasSpan.Binding);
        Assert.Same(SemanticCoordinateBinding.SourceParts, SemanticCoordinateContract.PdfStructuredSourceParts.Binding);
    }

    /// <summary>
    /// The identities the offline scorer resolves for one repeat, through the same binder the live
    /// pipeline reaches - the comparison basis for parity, computed the way the baseline was.
    /// </summary>
    private static HashSet<string> OfflineBoundIdentities(PdfStructuredSourceAuthority plan, int repeat)
    {
        var responses = CapturedResponses("DOC-0252");
        var identities = new HashSet<string>(StringComparer.Ordinal);

        for (var pack = 0; pack < Doc0252CallsPerRepeat; pack++)
        {
            var owned = plan.Packs[pack].OwnedAliases.ToHashSet(StringComparer.Ordinal);
            using var document = JsonDocument.Parse(responses[(repeat * Doc0252CallsPerRepeat) + pack]);
            foreach (var element in document.RootElement.GetProperty("headings").EnumerateArray())
            {
                foreach (var proposal in SemanticCoordinateContract.PdfStructuredSourceParts.Decode(element).Proposals)
                {
                    if (!proposal.IsHeading || !owned.Contains(proposal.SourceAlias)) continue;
                    var binding = SemanticSourcePartBinder.Bind(
                        plan.Atoms, new SemanticSourcePartsProposal(proposal.SourceParts!));
                    if (binding.IsBound) identities.Add(binding.Identity);
                }
            }
        }

        return identities;
    }

    // ---- reading the captured evidence ------------------------------------------------------------

    private static IReadOnlyList<string> CapturedResponses(string documentId)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(RawResponses)));
        return document.RootElement.EnumerateArray()
            .Where(call => call.GetProperty("DocumentId").GetString() == documentId)
            .OrderBy(call => call.GetProperty("Ordinal").GetInt32())
            .Select(call => call.GetProperty("RawResponse").GetString()!)
            .ToArray();
    }

    private static JsonElement FirstCapturedStructuredHeading()
    {
        using var document = JsonDocument.Parse(CapturedResponses("DOC-0252")[0]);
        return document.RootElement.GetProperty("headings").EnumerateArray().First().Clone();
    }

    private static HashSet<string> GoldIdentities()
    {
        using var gold = CanonicalGoldRegistry.ResolveAt(PredecessorGoldPath, Doc0252GoldSha256);
        Assert.Equal(Doc0252GoldSha256, CanonicalGoldRegistry.EntryAt(PredecessorGoldPath, Doc0252GoldSha256).GoldSha256);
        return gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
            .Select(claim => claim.GetProperty("identity").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> GoldAliases(string documentId)
    {
        using var gold = CanonicalGoldRegistry.Resolve(documentId);
        return gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
            .Select(claim => claim.GetProperty("sourceAlias").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static void Count(Dictionary<string, int> counts, string key) =>
        counts[key] = counts.GetValueOrDefault(key) + 1;

    private static double Ratio(int numerator, int denominator) =>
        denominator == 0 ? 0 : (double)numerator / denominator;

    private static double F1(double precision, double recall) =>
        precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);

    private static double Round(double value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);
}
