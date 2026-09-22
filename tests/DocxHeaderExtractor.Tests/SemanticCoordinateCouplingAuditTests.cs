using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Why a clause about what counts as a heading disturbed where headings were placed and how they
/// were written down.
/// <para>
/// EXP_MASTHEAD_METADATA changed one paragraph of semantic policy. The memberships it targeted did
/// not move; the relations and the part shapes did. Four approved headings were named correctly and
/// then refused, because the reply wrote WHOLE_ALIAS and also quoted the text - a combination the
/// schema permits, the validator permits, the decoder permits, and the binder refuses.
/// </para>
/// <para>
/// This audit asks who should be deciding what. It changes nothing.
/// </para>
/// </summary>
public sealed class SemanticCoordinateCouplingAuditTests
{
    private const string AuditRoot = "eval/a99-closed-loop/semantic-coordinate-coupling-audit-v1";
    private const string Doc0252Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private const string GoldSha256 = "e0001e940bc71c78d0dc2c8df44434f49421ff97679f1f968b192e98a05dd66e";
    private const string ExperimentRoot = "eval/a99-closed-loop/exp-masthead-metadata-experiment-v1/DOC-0252";
    private const string BaselineRoot = "eval/a99-closed-loop/structured-context-packing-experiment-v2/DOC-0252";

    private static readonly string[] RefusedInRepeat1 =
        ["L0400:S0", "L0420:S0", "L0470:S0", "L0507:S0"];

    [Fact]
    public void Characterize_the_semantic_relation_and_coordinate_coupling()
    {
        Assert.Equal(GoldSha256, CanonicalGoldRegistry.Entry("DOC-0252").GoldSha256);
        var plan = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Doc0252Pdf));
        var atomByAlias = plan.Atoms.ToDictionary(atom => atom.Alias, StringComparer.Ordinal);

        // ---- §6 the malformed shape, traced layer by layer ---------------------------------------
        var malformed = JsonDocument.Parse("""
            {"headings":[{"isHeading":true,"semanticRole":"heading","relationHints":["parent-node:NONE"],
              "sourceParts":[{"sourceAlias":"L0400:S0","selectionMode":"WHOLE_ALIAS",
                              "verbatimText":"4. Treatment of Negative Expenditures in ICP"}]}]}
            """);
        var entry = malformed.RootElement.GetProperty("headings")[0];

        var schemaPermits = SchemaPermitsVerbatimTextWithWholeAlias();
        var validatorIssues = SemanticCoordinateContract.PdfStructuredSourceParts
            .Validate(malformed.RootElement);
        var decoded = SemanticCoordinateContract.PdfStructuredSourceParts.Decode(entry);
        var proposalIssues = SemanticCoordinateContract.PdfStructuredSourceParts.Binding
            .ValidateProposal(decoded.Proposals[0],
                plan.Aliases.ToDictionary(alias => alias.Alias, StringComparer.Ordinal), null);
        var binding = SemanticSourcePartBinder.Bind(
            plan.Atoms, new SemanticSourcePartsProposal(decoded.Proposals[0].SourceParts!));

        Assert.True(schemaPermits);
        Assert.Empty(validatorIssues);
        Assert.Empty(decoded.Failures);
        Assert.Empty(proposalIssues);
        Assert.False(binding.IsBound);
        Assert.Equal(SemanticSourcePartsStatus.UnexpectedVerbatimText, binding.Status);

        // ---- §4 is selectionMode derivable from the selected content? ----------------------------
        var derivability = AssessSelectionModeDerivability(plan, atomByAlias);

        // ---- §5 were the four refusals lossless redundancy? --------------------------------------
        var refusals = RefusedInRepeat1.Select(alias =>
        {
            var raw = CapturedReply(ExperimentRoot, 1, "PACK_005");
            using var response = JsonDocument.Parse(raw);
            var heading = response.RootElement.GetProperty("headings").EnumerateArray()
                .Single(item => item.GetProperty("sourceParts")[0].GetProperty("sourceAlias").GetString() == alias);
            var part = heading.GetProperty("sourceParts")[0];
            var quoted = part.GetProperty("verbatimText").GetString()!;
            var atom = atomByAlias[alias];
            var identical = string.Equals(quoted, atom.Text, StringComparison.Ordinal);
            return new
            {
                alias,
                selectionMode = part.GetProperty("selectionMode").GetString(),
                quotedText = quoted,
                atomText = atom.Text,
                quotedEqualsWholeAtom = identical,
                classification = identical
                    ? "SEMANTICALLY_UNAMBIGUOUS_REDUNDANCY"
                    : "ACTUALLY_AMBIGUOUS_COORDINATE_REQUEST",
                canonicalizationWouldBeLossless = identical,
                resolvedCoordinatesIfCanonicalized = identical ? $"{alias}:0-{atom.Text.Length}" : "(undetermined)",
            };
        }).ToArray();

        // ---- §7 what actually moved between the arms ---------------------------------------------
        var perturbation = RefusedInRepeat1.Select(alias => new
        {
            alias,
            baseline = ClaimShape(BaselineRoot, 1, "PACK_005", alias),
            experiment = ClaimShape(ExperimentRoot, 1, "PACK_005", alias),
        }).ToArray();

        FreezeArtifact.AssertJson(AuditRoot, "semantic-coordinate-coupling-audit.v1.json", new
        {
            artifactKind = "a99_semantic_coordinate_coupling_audit",
            schemaVersion = "a99-semantic-coordinate-coupling-audit-v1",
            providerCalls = 0,
            modelCalls = 0,
            mutations = "none - prompt, Gold, contract, binder and packing are all unchanged",

            frozenExperiment = new
            {
                experimentId = "EXP_MASTHEAD_METADATA",
                result = "MIXED_INSUFFICIENT_AND_REGRESSIVE",
                mastheadTargetsStillEmitted = new
                {
                    programmeAndGroup = "L0513:S0|L0514:S0 - 2 of 3 repeats",
                    meetingMode = "L0515:S0 - 3 of 3 repeats",
                    dateVenueAddress = "L0516:S0|L0517:S0|L0518:S0 - 3 of 3 repeats",
                },
                repeat1Pack5 = new
                {
                    semanticallyProposed = 4,
                    bound = 0,
                    refused = 4,
                    refusal = "UnexpectedVerbatimText",
                },
                notReinterpreted = true,
            },

            // ---- §2 what each part of the prompt can reach ----------------------------------------
            promptResponsibilitySections = new object[]
            {
                new { section = "A_semantic_eligibility", influences = new[] { "isHeading", "claim existence" },
                      location = "opening paragraphs of the shared core" },
                new { section = "B_role_ontology", influences = new[] { "semanticRole" },
                      location = "implicit - the core names no closed role vocabulary, which is why replies "
                        + "carry 'heading', 'section', 'section-heading' and 'document-title' interchangeably" },
                new { section = "C_relation_rules", influences = new[] { "relationHints", "parent-node:ROOT/NONE/<alias>" },
                      location = "the REQUIRED block, immediately before where the experiment clause was appended" },
                new { section = "D_coordinate_rules", influences = new[] { "sourceParts", "selectionMode", "verbatimText", "part order" },
                      location = "the coordinate contract's own PromptClause, appended after the core" },
                new { section = "E_response_format", influences = new[] { "overall JSON shape" },
                      location = "the schema postfix and the provider's json_object mode" },
            },
            experimentClausePlacement = new
            {
                appendedTo = "the end of the shared core, after section C and before section D",
                adjacentInstructions = "the parent-node rules, including the sentence naming NONE for titles, "
                    + "subtitles, running headers and table labels",
                observedEffect = "the clause discussed a semantic category and the fields that moved were C's "
                    + "and D's",
            },

            // ---- §3 who should own each field -----------------------------------------------------
            fieldOwnership = new object[]
            {
                new { field = "isHeading / claim existence", authority = "LLM_REQUIRED",
                      reason = "whether text names a structural unit is meaning, which the source cannot settle" },
                new { field = "semanticRole", authority = "LLM_REQUIRED",
                      reason = "meaning, though currently unconstrained by any vocabulary" },
                new { field = "relationHints / parent-node", authority = "LLM_REQUIRED",
                      reason = "placement is a reading of the document, not a property of the atoms" },
                new { field = "sourceAlias", authority = "LLM_REQUIRED",
                      reason = "which occurrence is being named is the model's claim" },
                new { field = "verbatimText", authority = "LLM_REQUIRED",
                      reason = "the exact words claimed - this is how the model says how much of an atom it means" },
                new { field = "selectionMode", authority = "HARNESS_DERIVABLE",
                      reason = "given the alias and the quoted words, whether that is the whole atom or part of "
                        + "it is a fact about the source, and the harness holds the source" },
                new { field = "part order", authority = "LLM_REQUIRED",
                      reason = "the reading order of a wrapped heading is the claim's own shape" },
                new { field = "occurrence / leftExactContext / rightExactContext", authority = "LLM_REQUIRED",
                      reason = "disambiguation between repeats of the same text is a claim, not a lookup" },
                new { field = "numeric offsets", authority = "HARNESS_REQUIRED",
                      reason = "already the harness's, and the reason this contract exists" },
            },

            // ---- §4 ---------------------------------------------------------------------------------
            selectionModeDerivability = derivability,

            // ---- §5 ---------------------------------------------------------------------------------
            refusedShapes = new
            {
                cases = refusals,
                allLossless = refusals.All(item => item.canonicalizationWouldBeLossless),
                reading = "Each refusal quoted exactly the atom it named. Under the rule the binder applies, "
                    + "the quote is forbidden; under the source, the quote and the mode say the same thing. "
                    + "The claim was never ambiguous - it was over-specified.",
                cautionAgainstSilentAcceptance = "Accepting WHOLE_ALIAS with any verbatimText would also accept "
                    + "a quote that does NOT match the atom, which is a real contract violation and would then "
                    + "bind to the whole atom regardless of what the model said it meant. Canonicalization has "
                    + "to compare the quote with the source, not ignore it.",
            },

            // ---- §6 ---------------------------------------------------------------------------------
            contractLayerConsistency = new
            {
                contractLayerInconsistency = true,
                layers = new object[]
                {
                    new { layer = "JSON schema shown to the model", verdict = "PERMITS",
                          detail = "verbatimText is an unconditional property beside selectionMode; there is no "
                            + "if/then or oneOf tying them together, so the reply was schema-valid" },
                    new { layer = "CanonicalSemanticContractValidator.ValidateJson", verdict = "PERMITS",
                          detail = "checks only for fabricated numeric coordinate fields" },
                    new { layer = "SemanticProposalDecoder.DecodeSourceParts", verdict = "PERMITS",
                          detail = "requires sourceAlias and selectionMode; carries verbatimText through" },
                    new { layer = "SemanticCoordinateBinding.ValidateStructuredProposal", verdict = "PERMITS",
                          detail = "checks alias existence, segment ownership and that the mode is one of the two" },
                    new { layer = "SemanticSourcePartBinder.Resolve", verdict = "REFUSES",
                          detail = "UnexpectedVerbatimText - and the refusal is terminal: the claim is dropped "
                            + "and the evaluator sees a false negative" },
                },
                consequence = "One rule is stated in exactly one place, and it is the last place. Four layers "
                    + "tell the model its reply is acceptable and the fifth discards it. Whatever is decided "
                    + "about ownership, this rule should be expressible where the reply is shaped - either the "
                    + "schema forbids the combination, or the harness canonicalizes it.",
            },

            // ---- §7 ---------------------------------------------------------------------------------
            relationPerturbation = new
            {
                claims = perturbation,
                membershipPerturbations = 0,
                relationOnlyPerturbations = 4,
                reading = "All four were proposed in both arms and all four name the same atom. What changed is "
                    + "the relation - ROOT under the baseline, NONE under the clause - and the part shape. The "
                    + "clause did not alter what the model considered a heading; it altered how it answered two "
                    + "other questions.",
            },

            // ---- §8 ---------------------------------------------------------------------------------
            noneTokenCoupling = new
            {
                carriesSemanticAndRelationalMeaning = true,
                semanticSense = "an accepted heading that sits outside the section tree - a title, a running "
                    + "header, a table label",
                relationalSense = "this claim has no parent",
                evidence = "The prompt introduces NONE inside the relation block as one of three parent-node "
                    + "values, then immediately defines it by semantic category. A claim therefore reaches NONE "
                    + "either by being a certain kind of thing or by having nowhere to attach, and the reply "
                    + "cannot distinguish the two.",
                observedConsequence = "A clause arguing about which kinds of thing belong in the category moved "
                    + "genuine sections into it. Under one reading that is obedience, not error: if NONE means "
                    + "'no parent', and the clause made the model less certain about what the annex's structure "
                    + "was, then NONE is where an uncertain placement goes.",
                unresolved = "Whether this is the cause cannot be established from one arm. It is a coupling "
                    + "that exists in the wording, and it is consistent with what was observed.",
            },

            // ---- §9 / §10 ----------------------------------------------------------------------------
            architectureOptions = new object[]
            {
                new
                {
                    option = "A_PROMPT_ONLY_REPAIR",
                    change = "reword the masthead clause and move it out of the relation block",
                    correctnessGain = "possible for the three masthead claims; nothing for the shape instability, "
                        + "which the prompt does not govern",
                    compatibilityCost = "none - contract, binder and Gold untouched",
                    migrationCost = "one prompt hash, one experiment",
                    providerRebaseline = "the arm only; baselines keep their prompt",
                    historicalReplay = "unaffected",
                    risk = "the same class of failure can recur, because the reply can still be schema-valid and "
                        + "binder-invalid",
                },
                new
                {
                    option = "B_COORDINATE_CANONICALIZATION",
                    change = "the model names an alias and quotes the words it means; the harness derives "
                        + "WHOLE_ALIAS or VERBATIM_TEXT by comparing the quote with the atom, and refuses only "
                        + "when the quote does not occur",
                    correctnessGain = "removes an entire refusal class without accepting a wrong quote; the four "
                        + "lost headings would have bound",
                    compatibilityCost = "selectionMode becomes derived rather than required - the schema and the "
                        + "coordinate prompt clause both change, so every structured request's bytes change",
                    migrationCost = "contract hash, prompt hash, request bytes, provider-input plan",
                    providerRebaseline = "required for the structured lane; captured replies remain decodable "
                        + "because a derived mode can be computed from what they already carry",
                    historicalReplay = "preserved - existing captures carry alias and quote, which is what "
                        + "derivation needs",
                    risk = "a quote that matches the atom in more than one place must still be disambiguated, "
                        + "which is what occurrence and the context fields already exist for",
                },
                new
                {
                    option = "C_SEPARATE_MEMBERSHIP_FROM_PLACEMENT",
                    change = "membership and placement stop sharing a field and a vocabulary; NONE stops meaning "
                        + "both 'outside the tree by kind' and 'no parent'",
                    correctnessGain = "a clause about categories could no longer move a section's placement, "
                        + "which is the perturbation actually observed",
                    compatibilityCost = "relation vocabulary changes, so every reply shape and every frozen "
                        + "prompt/request hash changes; the hierarchy resolver reads these hints",
                    migrationCost = "highest - prompt, schema, resolver, and the Gold that records roles",
                    providerRebaseline = "required for both lanes",
                    historicalReplay = "captured replies stay readable, but their relation values would need a "
                        + "documented mapping into the new vocabulary",
                    risk = "largest surface changed on the evidence of one arm",
                },
            },

            recommendation = new
            {
                next = "B_COORDINATE_CANONICALIZATION",
                why = "It is the only option whose defect is already proven rather than hypothesised. Four "
                    + "approved headings were lost to it in this run, the same class cost nothing to detect and "
                    + "everything to diagnose, and the fix restores the architecture's own rule - the model says "
                    + "what it means, the harness decides coordinates. It is also the option that makes the next "
                    + "semantic experiment interpretable, because a semantic clause could no longer fail through "
                    + "a coordinate encoding.",
                notYetC = "The relation coupling is real in the wording but its causal role rests on one repeat. "
                    + "Establishing it deserves its own evidence, not a redesign carried in alongside B.",
                notA = "Rewording alone leaves the reply able to be valid everywhere except where it is finally "
                    + "read.",
                experimentsOnHold = new[]
                {
                    "EXP_MASTHEAD_METADATA rerun - not recommended",
                    "EXP_BODY_PROPOSITION - not recommended yet",
                    "EXP_SCHEDULE_ITEM - not recommended yet",
                },
                reasonForHold = "Each would be measured through the same coordinate encoding that has now been "
                    + "shown able to discard a correct answer.",
            },

            providerAccounting = new
            {
                remainingFromExperimentCap = 3,
                status = "UNUSED and UNAUTHORIZED - not transferable",
                callsThisTask = 0,
            },
        });
    }

    /// <summary>
    /// Every part in Gold and in every captured reply, asked whether the mode it declares could be
    /// derived from the words it claims.
    /// </summary>
    private static object AssessSelectionModeDerivability(
        PdfStructuredSourceAuthority plan, IReadOnlyDictionary<string, SemanticSourceAtom> atomByAlias)
    {
        var wholeAlias = 0;
        var verbatim = 0;
        var multiPart = 0;
        var derivable = 0;
        var counterexamples = new List<object>();

        using var gold = CanonicalGoldRegistry.Resolve("DOC-0252");
        foreach (var claim in gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray())
        {
            var parts = claim.GetProperty("sourceParts").EnumerateArray().ToArray();
            if (parts.Length > 1) multiPart++;
            foreach (var part in parts)
            {
                var alias = part.GetProperty("sourceAlias").GetString()!;
                var mode = part.GetProperty("selectionMode").GetString()!;
                var quoted = part.TryGetProperty("verbatimText", out var text) && text.ValueKind == JsonValueKind.String
                    ? text.GetString()
                    : null;
                if (mode == CanonicalSemanticSelectionMode.WholeAlias) wholeAlias++; else verbatim++;

                var atom = atomByAlias[alias];
                var claimed = mode == CanonicalSemanticSelectionMode.WholeAlias ? atom.Text : quoted ?? string.Empty;
                var derivedMode = string.Equals(claimed, atom.Text, StringComparison.Ordinal)
                    ? CanonicalSemanticSelectionMode.WholeAlias
                    : CanonicalSemanticSelectionMode.VerbatimText;

                if (derivedMode == mode) derivable++;
                else
                {
                    counterexamples.Add(new { source = "gold", alias, declaredMode = mode, derivedMode, quoted });
                }
            }
        }

        return new
        {
            scope = "all 41 approved DOC-0252 claims",
            wholeAliasParts = wholeAlias,
            verbatimTextParts = verbatim,
            multiPartClaims = multiPart,
            partsWhereModeIsDerivableFromTheQuote = derivable,
            counterexamples,
            rule = "quote equals the atom exactly -> WHOLE_ALIAS; quote is a proper substring -> VERBATIM_TEXT",
            caveat = "A quote occurring more than once inside its atom still needs occurrence or exact context "
                + "to place it. Derivation settles the mode, not the position, and the fields that settle "
                + "position already exist.",
        };
    }

    private static bool SchemaPermitsVerbatimTextWithWholeAlias()
    {
        var schema = JsonSerializer.Serialize(SemanticSourcePartsContract.Schema());
        using var document = JsonDocument.Parse(schema);
        var part = document.RootElement.GetProperty("properties").GetProperty("headings")
            .GetProperty("items").GetProperty("properties").GetProperty("sourceParts")
            .GetProperty("items");
        var required = part.GetProperty("required").EnumerateArray().Select(item => item.GetString()).ToArray();
        // verbatimText is an unconditional sibling of selectionMode and is not required, so the
        // combination is expressible and nothing in the schema forbids it.
        return part.GetProperty("properties").TryGetProperty("verbatimText", out _) &&
            !required.Contains("verbatimText") &&
            !schema.Contains("\"if\"", StringComparison.Ordinal) &&
            !schema.Contains("oneOf", StringComparison.Ordinal);
    }

    private static string CapturedReply(string root, int repeat, string pack)
    {
        var directory = TestRepository.Path($"{root}/r{repeat}");
        var path = Directory.GetFiles(directory, "*transport-capture.v1.json").Single();
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var call = document.RootElement.GetProperty("calls").EnumerateArray()
            .Single(item => item.GetProperty("packId").GetString()!.EndsWith(pack, StringComparison.Ordinal));
        return Encoding.UTF8.GetString(Convert.FromBase64String(call.GetProperty("rawResponseUtf8Base64").GetString()!));
    }

    private static object ClaimShape(string root, int repeat, string pack, string alias)
    {
        using var response = JsonDocument.Parse(CapturedReply(root, repeat, pack));
        var heading = response.RootElement.GetProperty("headings").EnumerateArray()
            .FirstOrDefault(item => item.GetProperty("sourceParts")[0].GetProperty("sourceAlias").GetString() == alias);
        if (heading.ValueKind != JsonValueKind.Object) return new { proposed = false };

        var part = heading.GetProperty("sourceParts")[0];
        return new
        {
            proposed = true,
            semanticRole = heading.TryGetProperty("semanticRole", out var role) ? role.GetString() : null,
            relationHints = heading.TryGetProperty("relationHints", out var hints)
                ? hints.EnumerateArray().Select(item => item.GetString()).ToArray()
                : [],
            selectionMode = part.GetProperty("selectionMode").GetString(),
            carriesVerbatimText = part.TryGetProperty("verbatimText", out var text) && text.ValueKind == JsonValueKind.String,
        };
    }
}
