using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The five false positives that survive once packing variance and Gold boundary debt are gone,
/// read against the source and against the Gold headings nearest to them.
/// <para>
/// These are the first errors in this whole line of work that are actually the model's. Everything
/// earlier turned out to be the harness: a request that mixed three regions, a decoder reading the
/// wrong contract's shape, a binder that could not express a tuple, a locator that dropped a
/// closing parenthesis. What is left is semantic policy, and policy is argued from contrasts -
/// which Gold headings look like these and are kept, and what separates them.
/// </para>
/// <para>
/// No prompt is edited here. The output is three discriminators and what each would cost.
/// </para>
/// </summary>
public sealed class ResidualSemanticMechanismAuditTests
{
    private const string AuditRoot = "eval/a99-closed-loop/residual-semantic-mechanism-audit-v1";
    private const string Doc0252Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private const string GoldSha256 = "e0001e940bc71c78d0dc2c8df44434f49421ff97679f1f968b192e98a05dd66e";
    private const string SuccessorScore =
        "eval/a99-closed-loop/structured-context-packing-experiment-v2/DOC-0252/targeted-packing-rerun-v2-successor-score.v1.json";

    [Fact]
    public void Characterize_the_five_residual_semantic_false_positives()
    {
        Assert.Equal(GoldSha256, CanonicalGoldRegistry.Entry("DOC-0252").GoldSha256);
        var plan = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Doc0252Pdf));
        var atomByAlias = plan.Atoms.ToDictionary(atom => atom.Alias, StringComparer.Ordinal);
        var scopeByAlias = plan.Evidence.ToDictionary(
            item => item.SourceAlias, item => item.StructuralScope, StringComparer.Ordinal);

        var gold = GoldClaims();
        Assert.Equal(41, gold.Count);

        using var score = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(SuccessorScore)));
        var residual = score.RootElement.GetProperty("residualFalsePositives");
        Assert.Equal(5, residual.GetProperty("stableAcrossAllThree").GetInt32());
        var falsePositives = residual.GetProperty("identities").EnumerateArray()
            .Select(item => item.GetProperty("identity").GetString()!).ToArray();
        Assert.Equal(5, falsePositives.Length);

        // Every repeat's per-cell figures, re-read rather than restated.
        var perRepeat = score.RootElement.GetProperty("perRepeat").EnumerateArray()
            .Select(item => new
            {
                repeat = item.GetProperty("repeat").GetInt32(),
                truePositive = item.GetProperty("truePositive").GetInt32(),
                falsePositive = item.GetProperty("falsePositive").GetInt32(),
                falseNegative = item.GetProperty("falseNegative").GetInt32(),
            }).ToArray();
        Assert.All(perRepeat, item => Assert.Equal(18, item.truePositive));
        Assert.All(perRepeat, item => Assert.Equal(5, item.falsePositive));
        Assert.All(perRepeat, item => Assert.Equal(0, item.falseNegative));

        // ---- what the model said about each claim, from the captured replies --------------------
        var claimed = ClaimedByModel();

        object Describe(string identity)
        {
            var aliases = identity.Split('|').Select(part => part[..part.LastIndexOf(':')]).ToArray();
            var first = atomByAlias[aliases[0]];
            var index = plan.Atoms.ToList().FindIndex(atom => atom.Alias == aliases[0]);
            return new
            {
                identity,
                aliases,
                text = string.Join(" ", aliases.Select(alias => atomByAlias[alias].Text)),
                page = first.Page,
                atomOrdinal = index,
                scopes = aliases.Select(alias => scopeByAlias.GetValueOrDefault(alias, "?")).ToArray(),
                modelSemanticRole = claimed.GetValueOrDefault(string.Join("|", aliases))?.Role ?? "(none)",
                modelRelationHints = claimed.GetValueOrDefault(string.Join("|", aliases))?.Hints ?? [],
                precedingSource = Enumerable.Range(Math.Max(0, index - 2), Math.Min(2, index))
                    .Select(at => $"{plan.Atoms[at].Alias}: {plan.Atoms[at].Text}").ToArray(),
                followingSource = Enumerable.Range(index + aliases.Length, 2)
                    .Where(at => at < plan.Atoms.Count)
                    .Select(at => $"{plan.Atoms[at].Alias}: {plan.Atoms[at].Text}").ToArray(),
            };
        }

        // ---- Gold-risk scans, run over all 41 rather than argued -------------------------------
        var goldStartingWithClockTime = gold
            .Where(claim => System.Text.RegularExpressions.Regex.IsMatch(
                claim.Value.Text.TrimStart(), @"^\d{1,2}:\d{2}"))
            .Select(claim => claim.Key).ToArray();
        var goldContainingADate = gold
            .Where(claim => System.Text.RegularExpressions.Regex.IsMatch(
                claim.Value.Text, @"\b(January|February|March|April|May|June|July|August|September|October|November|December)\b\s+\d"))
            .Select(claim => claim.Value.Text).ToArray();
        var goldEndingInSentencePunctuation = gold
            .Where(claim => claim.Value.Text.TrimEnd().EndsWith('.') && !System.Text.RegularExpressions.Regex.IsMatch(
                claim.Value.Text.TrimEnd(), @"^\d+\.$"))
            .Select(claim => claim.Value.Text).ToArray();
        var goldStartingLowercase = gold
            .Where(claim => claim.Value.Text.Length > 0 && char.IsLower(claim.Value.Text[0]))
            .Select(claim => claim.Value.Text).ToArray();
        var goldThatIsEventMetadata = gold
            .Where(claim => System.Text.RegularExpressions.Regex.IsMatch(
                claim.Value.Text, @"^\s*(\d{1,2}:\d{2}|[A-Z][a-z]+ \d{1,2}[-–]\d{1,2},|\d{3,5} \w+ (Ave|Street|Road))"))
            .Select(claim => claim.Value.Text).ToArray();

        FreezeArtifact.AssertJson(AuditRoot, "residual-semantic-mechanisms.v1.json", new
        {
            artifactKind = "a99_residual_semantic_mechanism_audit",
            schemaVersion = "a99-residual-semantic-mechanism-audit-v1",
            authorityId = "DOC-0252",
            providerCalls = 0,
            modelCalls = 0,
            promptChanged = false,
            goldSha256 = GoldSha256,
            goldClaims = gold.Count,
            scoredResult = new { perRepeat, precision = 0.7826, recall = 1.0, f1 = 0.8780 },

            framing = "These five are the model's own errors. Packing variance is gone, Gold's boundary "
                + "debt is corrected, and recall is 1.0 - nothing here is a measurement artifact.",

            // ---- the finding that reframes all of this -----------------------------------------
            promptPolicyObservation = new
            {
                finding = "Four of the five carry relationHints parent-node:NONE, and the model reached that "
                    + "honestly: the prompt tells it to. NONE is defined as 'a heading but holds NO position "
                    + "in the section tree', to be used for 'the document's own title and subtitle, running "
                    + "headers and footers, table and figure labels', with the explicit instruction that "
                    + "'they are real headings and you should still report them'.",
                consequence = "The model is following the instruction and Gold is scoring it wrong. Gold "
                    + "keeps exactly one such claim for this document - its own title, L0000:S0 - and keeps "
                    + "no repeated masthead, no date, no venue, no address, no meeting-mode line.",
                noneClaimsInThisRun = claimed.Values.Count(item => item.Hints.Contains("parent-node:NONE")),
                noneClaimsThatAreGold = 1,
                noneClaimsThatAreFalsePositives = 4,
                implication = "The intervention is not 'teach the model to find fewer headings'. It is to "
                    + "narrow what the NONE category admits, which is a policy the prompt states and can "
                    + "restate without touching how sections are found.",
            },

            mechanisms = new object[]
            {
                new
                {
                    id = "A_BODY_PROPOSITION",
                    falsePositives = new[] { falsePositives.Single(item => item.StartsWith("L0396", StringComparison.Ordinal)) },
                    evidence = Describe(falsePositives.Single(item => item.StartsWith("L0396", StringComparison.Ordinal))),
                    whatItIs = "The tail of a wrapped prose sentence. The atom before it ends mid-clause "
                        + "('scanner data lacks details on item') and this one completes it "
                        + "('definitions and discounts, and requires traditional price collection to continue "
                        + "in parallel.'). It opens lowercase, carries a finite verb, and closes the sentence.",
                    contrastiveGold = new[]
                    {
                        Contrast(gold, "L0400:S0", "the next heading in the same region: an ordinal and a noun phrase, no predicate, no sentence period"),
                        Contrast(gold, "L0470:S0", "same shape, same region"),
                        Contrast(gold, "L0507:S0", "a session heading in the same pack"),
                    },
                    discriminator = "A heading names a division of the document; it does not assert something "
                        + "about the subject. Text that continues or completes a sentence, or that states a "
                        + "proposition with a finite verb and sentence-final punctuation, is body text however "
                        + "it is set.",
                    surfaceSignalsDeliberatelyNotUsed = "font size, boldness, or being alone on a line - the "
                        + "region is uniformly set and none of those separate it from its neighbours.",
                    classification = "PROMPT_SEMANTIC_POLICY",
                },
                new
                {
                    id = "B_MASTHEAD_AND_EVENT_METADATA",
                    falsePositives = falsePositives.Where(item =>
                        item.StartsWith("L0513", StringComparison.Ordinal) ||
                        item.StartsWith("L0515", StringComparison.Ordinal) ||
                        item.StartsWith("L0516", StringComparison.Ordinal)).ToArray(),
                    evidence = falsePositives.Where(item =>
                        item.StartsWith("L0513", StringComparison.Ordinal) ||
                        item.StartsWith("L0515", StringComparison.Ordinal) ||
                        item.StartsWith("L0516", StringComparison.Ordinal)).Select(Describe).ToArray(),
                    whatItIs = "One local system, not three findings: the annex opens by restating the "
                        + "programme and group that met, then how it met, then when and where. Immediately "
                        + "after them the annex's actual structure begins - 'Agenda', then 'DAY 1: ...', then "
                        + "'SESSION I: ...' - and Gold keeps all three of those.",
                    contrastiveGold = new[]
                    {
                        Contrast(gold, "L0519:S0", "the annex's own section heading, three atoms later - kept by Gold and by the model"),
                        Contrast(gold, "L0520:S0", "kept, and it contains a date - so 'mentions a date' cannot be the rule"),
                        Contrast(gold, "L0556:S0", "the other annex's heading, same function"),
                        Contrast(gold, "L0000:S0", "the document's own title, kept once - the category this must not destroy"),
                    },
                    discriminator = "A masthead identifies the document or the event it records; a heading "
                        + "divides the document. Report the document's own title once. A later block that "
                        + "restates the organisation, event, date, venue, address or meeting mode is "
                        + "identifying the same event again, not opening a new division, and nothing is filed "
                        + "under it.",
                    whyNotSurfaceBased = "Every one of these is visually prominent and centred, and so is "
                        + "'Agenda' directly beneath them. Prominence separates none of them.",
                    classification = "PROMPT_SEMANTIC_POLICY",
                },
                new
                {
                    id = "C_SCHEDULED_AGENDA_ENTRY",
                    falsePositives = new[] { falsePositives.Single(item => item.StartsWith("L0550", StringComparison.Ordinal)) },
                    evidence = Describe(falsePositives.Single(item => item.StartsWith("L0550", StringComparison.Ordinal))),
                    whatItIs = "A scheduled row of the agenda: a clock range and the activity that fills it. "
                        + "The model rejected every other such row in the same request - eighteen of them - "
                        + "and kept this one. What is different about it is not its function but its form: it "
                        + "carries no presenter attribution in brackets, it sits alone in one atom rather than "
                        + "split across a time atom and a title atom, and its wording is identical to a real "
                        + "heading it had already accepted earlier in the document (L0470:S0, "
                        + "'6. Improving Reliability of Price Comparisons').",
                    contrastiveGold = new[]
                    {
                        Contrast(gold, "L0470:S0", "the same words as a genuine agenda-item heading in the minutes body"),
                        Contrast(gold, "L0520:S0", "a kept agenda heading that contains a date and a chair"),
                        Contrast(gold, "L0540:S0", "a kept session heading inside the same agenda annex"),
                    },
                    rejectedSiblings = "L0522, L0524, L0527, L0528, L0529, L0532, L0534, L0535, L0537, L0541, "
                        + "L0543, L0545, L0546, L0547, L0548, L0551, L0552, L0554 - all scheduled rows, all "
                        + "correctly omitted in every repeat.",
                    discriminator = "A scheduled entry says when an activity happens; a heading says which part "
                        + "of the document follows. In a schedule, the day and session rows divide it and the "
                        + "timed activity rows fill it. The same wording can be a heading elsewhere in the "
                        + "document, where it heads the account of that activity.",
                    whyNotRegex = "'Contains a time' would be wrong twice over: two kept Gold headings carry a "
                        + "date and a chair, and a genuine heading may legitimately contain one.",
                    classification = "PROMPT_SEMANTIC_POLICY",
                },
            },

            goldRiskScan = new
            {
                note = "Each candidate rule run over all 41 Gold claims, not argued from the five.",
                ruleA_wouldRejectSentencesAndFragments = new
                {
                    goldEndingInSentencePunctuation,
                    goldStartingLowercase,
                    goldClaimsPotentiallyHit = goldEndingInSentencePunctuation.Length + goldStartingLowercase.Length,
                },
                ruleB_wouldRejectEventMetadata = new
                {
                    goldThatIsEventMetadata,
                    goldContainingADate,
                    goldClaimsPotentiallyHit = goldThatIsEventMetadata.Length,
                    caution = "Two Gold headings contain a date (DAY 1 / DAY 2 rows). A rule phrased as 'a line "
                        + "containing a date is metadata' would reject both. A rule phrased by function - does "
                        + "it open a division that content is filed under - keeps them.",
                    mustNotReject = "L0000:S0, the document's own title.",
                },
                ruleC_wouldRejectScheduledEntries = new
                {
                    goldStartingWithClockTime,
                    goldClaimsPotentiallyHit = goldStartingWithClockTime.Length,
                    caution = "No Gold heading opens with a clock time today, so a surface rule would appear "
                        + "free. It is still the wrong rule: it encodes this document's layout rather than the "
                        + "distinction, and a schedule whose sessions are timed would break it.",
                },
            },

            proposedPromptChanges = new object[]
            {
                new
                {
                    rule = "B_MASTHEAD_AND_EVENT_METADATA",
                    smallestChange = "Narrow what the NONE category admits. Today it reads 'Use NONE for the "
                        + "document's own title and subtitle, running headers and footers, table and figure "
                        + "labels, form labels and signature labels.' Add that a block restating the "
                        + "organisation, event, date, venue, address or meeting mode is identifying the "
                        + "document or event rather than heading a part of it, and is not returned - while the "
                        + "document's own title still is, once.",
                    generality = "Stated over document/event identity, not over this document's words.",
                    addressesFalsePositives = 3,
                },
                new
                {
                    rule = "C_SCHEDULED_AGENDA_ENTRY",
                    smallestChange = "Add that in a schedule or programme, the rows that divide it - a day, a "
                        + "session - are headings, and the rows that fill it with timed activities are entries "
                        + "within it, even when an entry's wording matches a heading found elsewhere.",
                    generality = "Stated over schedule structure, with no mention of time formats.",
                    addressesFalsePositives = 1,
                },
                new
                {
                    rule = "A_BODY_PROPOSITION",
                    smallestChange = "Add that text which continues or completes a sentence, or which asserts a "
                        + "proposition rather than naming a part of the document, is body text however it is set.",
                    generality = "Stated over grammatical function, with no mention of layout.",
                    addressesFalsePositives = 1,
                },
            },

            experiments = new object[]
            {
                new
                {
                    id = "EXPERIMENT_A_MASTHEAD",
                    rule = "B_MASTHEAD_AND_EVENT_METADATA",
                    falsePositivesTargeted = 3,
                    targetPacks = new[] { "PACK_005", "PACK_006" },
                    reason = "the masthead spans the boundary: its first claim is owned by pack 5, the rest by pack 6",
                    repeats = 3,
                    semanticCalls = 6,
                    placementCalls = 0,
                    proposedHardCap = 9,
                    goldAtRisk = "L0000:S0 only, and only if the rule is phrased by surface prominence rather than by function",
                },
                new
                {
                    id = "EXPERIMENT_B_SCHEDULE",
                    rule = "C_SCHEDULED_AGENDA_ENTRY",
                    falsePositivesTargeted = 1,
                    targetPacks = new[] { "PACK_006" },
                    repeats = 3,
                    semanticCalls = 3,
                    placementCalls = 0,
                    proposedHardCap = 6,
                    goldAtRisk = "the DAY and SESSION rows of the same annex - 9 Gold claims sit in this pack",
                },
                new
                {
                    id = "EXPERIMENT_C_PROPOSITION",
                    rule = "A_BODY_PROPOSITION",
                    falsePositivesTargeted = 1,
                    targetPacks = new[] { "PACK_005" },
                    repeats = 3,
                    semanticCalls = 3,
                    placementCalls = 0,
                    proposedHardCap = 6,
                    goldAtRisk = "none identified: no Gold claim in this document is a sentence",
                },
            },

            recommendedOrder = new
            {
                first = "EXPERIMENT_A_MASTHEAD",
                why = "three of the five false positives, one coherent mechanism, and the clearest contrast - "
                    + "the annex's real heading sits three atoms below the masthead and is kept by both Gold "
                    + "and the model, so the distinction is visible inside one request.",
                second = "EXPERIMENT_C_PROPOSITION",
                whySecond = "no Gold claim in this document is a sentence, so it carries the least regression "
                    + "risk, and it is independent of the annex entirely.",
                third = "EXPERIMENT_B_SCHEDULE",
                whyThird = "highest Gold risk - the rule has to separate rows that divide a schedule from rows "
                    + "that fill it, and nine kept headings sit in the same request as the one error.",
                doNotCombine = "Three rules in one prompt change would make an improved score unattributable, "
                    + "which is the mistake this whole sequence has been avoiding.",
            },

            candidateHints = new
            {
                hintedAtoms = plan.Evidence.Count(item => item.CandidateAttention.HeuristicMatch),
                totalAtoms = plan.Atoms.Count,
                inScope = false,
                note = "Still 650/650 and still non-discriminating, and still not causal for these five: the "
                    + "model rejected 18 scheduled rows that carry the same hint as the one it kept.",
            },
        });
    }

    private static object Contrast(IReadOnlyDictionary<string, GoldClaim> gold, string alias, string why)
    {
        var claim = gold.First(item => item.Value.Aliases[0] == alias);
        return new { alias, text = claim.Value.Text, role = claim.Value.Role, why };
    }

    private static Dictionary<string, ModelClaim> ClaimedByModel()
    {
        var directory = TestRepository.Path(
            "eval/a99-closed-loop/structured-context-packing-experiment-v2/DOC-0252/r1");
        var capture = Directory.GetFiles(directory, "*transport-capture.v1.json").Single();
        using var document = JsonDocument.Parse(File.ReadAllText(capture));
        var claims = new Dictionary<string, ModelClaim>(StringComparer.Ordinal);

        foreach (var call in document.RootElement.GetProperty("calls").EnumerateArray())
        {
            var raw = System.Text.Encoding.UTF8.GetString(
                Convert.FromBase64String(call.GetProperty("rawResponseUtf8Base64").GetString()!));
            using var response = JsonDocument.Parse(raw);
            foreach (var heading in response.RootElement.GetProperty("headings").EnumerateArray())
            {
                var aliases = heading.GetProperty("sourceParts").EnumerateArray()
                    .Select(part => part.GetProperty("sourceAlias").GetString()!).ToArray();
                var hints = heading.TryGetProperty("relationHints", out var value) && value.ValueKind == JsonValueKind.Array
                    ? value.EnumerateArray().Select(item => item.GetString()!).ToArray()
                    : [];
                var role = heading.TryGetProperty("semanticRole", out var roleValue) && roleValue.ValueKind == JsonValueKind.String
                    ? roleValue.GetString()!
                    : "(none)";
                claims[string.Join("|", aliases)] = new ModelClaim(role, hints);
            }
        }

        return claims;
    }

    private static Dictionary<string, GoldClaim> GoldClaims()
    {
        using var gold = CanonicalGoldRegistry.Resolve("DOC-0252");
        return gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
            .ToDictionary(
                claim => claim.GetProperty("identity").GetString()!,
                claim => new GoldClaim(
                    claim.GetProperty("projectedText").GetString()!,
                    claim.GetProperty("semanticRole").GetString()!,
                    claim.GetProperty("sourceParts").EnumerateArray()
                        .Select(part => part.GetProperty("sourceAlias").GetString()!).ToArray()),
                StringComparer.Ordinal);
    }

    private sealed record GoldClaim(string Text, string Role, IReadOnlyList<string> Aliases);

    private sealed record ModelClaim(string Role, IReadOnlyList<string> Hints);
}
