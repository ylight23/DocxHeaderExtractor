using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Why one repeat of the structured baseline lost precision, measured from the frozen run.
/// <para>
/// Repeat 2 returned 111 heading proposals where repeats 1 and 3 returned 45 and 47, against
/// byte-identical requests. Nothing here changes a prompt, a hint, Gold or a binder: the question
/// is where those proposals came from, and the answer has to come from the captured evidence
/// before anything is adjusted in response to it.
/// </para>
/// </summary>
public sealed class StructuredBaselinePrecisionForensicsTests
{
    private const string RunRoot = "eval/a99-closed-loop/occurrence-baseline-structured-v1";
    private const string ForensicRoot = "eval/a99-closed-loop/structured-baseline-forensics-v1";
    private const string RawResponses = RunRoot + "/raw-responses.v1.json";
    private const string RunArtifact = RunRoot + "/run.v1.json";
    private const string RawResponsesSha256 =
        "c1122ec1361c7e2895e95261b7c83dd27e4c25933397987ecc4be490833b5df6";
    private const string Doc0252Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private const string Doc0252GoldSha256 =
        "870c06ac4585d89f50496b5ae004f8a06c8072584e163184f817634fe03b468e";

    private const int Repeats = 3;
    private const int Packs = 6;

    [Fact]
    public void Characterize_the_repeat_two_precision_collapse()
    {
        Assert.Equal(RawResponsesSha256, CanonicalArtifactHash.OfTextFile(TestRepository.Path(RawResponses)));
        Assert.Equal(Doc0252GoldSha256, CanonicalGoldRegistry.Entry("DOC-0252").GoldSha256);

        var plan = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Doc0252Pdf));
        var atomByAlias = plan.Atoms.ToDictionary(atom => atom.Alias, StringComparer.Ordinal);
        var hintedAliases = plan.Evidence
            .Where(item => item.CandidateAttention.HeuristicMatch)
            .Select(item => item.SourceAlias)
            .ToHashSet(StringComparer.Ordinal);
        var scopeByAlias = plan.Evidence.ToDictionary(
            item => item.SourceAlias, item => item.StructuralScope, StringComparer.Ordinal);

        var gold = GoldClaims();
        var goldIdentities = gold.Keys.ToHashSet(StringComparer.Ordinal);
        var goldAtomOrdinals = gold.Values
            .SelectMany(claim => claim.Aliases)
            .Where(atomByAlias.ContainsKey)
            .Select(alias => atomByAlias[alias].Ordinal)
            .ToHashSet();

        var responses = CapturedResponses("DOC-0252");
        Assert.Equal(Repeats * Packs, responses.Count);

        // ---- per pack, per repeat ---------------------------------------------------------------
        var cells = new List<PackCell>();
        var claimsByRepeat = new Dictionary<int, List<Claim>>();

        for (var repeat = 1; repeat <= Repeats; repeat++)
        {
            var claims = new List<Claim>();
            for (var pack = 1; pack <= Packs; pack++)
            {
                var owned = plan.Packs[pack - 1].OwnedAliases.ToHashSet(StringComparer.Ordinal);
                var raw = responses[((repeat - 1) * Packs) + (pack - 1)];
                using var document = JsonDocument.Parse(raw);

                var entries = document.RootElement.GetProperty("headings").EnumerateArray().ToArray();
                var headingTrue = 0;
                var headingFalse = 0;
                var decoded = 0;
                var bound = 0;
                var rejected = new List<string>();

                foreach (var entry in entries)
                {
                    var result = SemanticCoordinateContract.PdfStructuredSourceParts.Decode(entry);
                    foreach (var proposal in result.Proposals)
                    {
                        decoded++;
                        if (!proposal.IsHeading) { headingFalse++; continue; }
                        headingTrue++;
                        if (!owned.Contains(proposal.SourceAlias)) { rejected.Add("OutOfOwnedSegment"); continue; }

                        var binding = SemanticSourcePartBinder.Bind(
                            plan.Atoms, new SemanticSourcePartsProposal(proposal.SourceParts!));
                        if (!binding.IsBound) { rejected.Add(binding.Status.ToString()); continue; }

                        bound++;
                        claims.Add(new Claim(
                            binding.Identity,
                            pack,
                            proposal.SourceParts!.Select(part => part.SourceAlias).ToArray(),
                            SemanticSourceProjection.Render(binding.Parts),
                            proposal.SemanticRole ?? "(none)",
                            proposal.SourceParts!.Count,
                            proposal.SourceParts!.Select(part => part.SelectionMode ?? "(none)").Distinct(StringComparer.Ordinal).ToArray(),
                            proposal.RelationHints ?? []));
                    }
                }

                var packClaims = claims.Where(claim => claim.Pack == pack).ToArray();
                var truePositive = packClaims.Count(claim => goldIdentities.Contains(claim.Identity));
                cells.Add(new PackCell(pack, repeat, entries.Length, headingTrue, headingFalse,
                    decoded, bound, rejected.Count, rejected.ToArray(), truePositive,
                    packClaims.Length - truePositive, raw.Length));
            }
            claimsByRepeat[repeat] = claims;
        }

        // ---- cross-repeat identity sets ----------------------------------------------------------
        var boundByRepeat = claimsByRepeat.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Select(claim => claim.Identity).ToHashSet(StringComparer.Ordinal));
        var tpByRepeat = boundByRepeat.ToDictionary(
            pair => pair.Key, pair => pair.Value.Where(goldIdentities.Contains).ToHashSet(StringComparer.Ordinal));
        var fpByRepeat = boundByRepeat.ToDictionary(
            pair => pair.Key, pair => pair.Value.Where(identity => !goldIdentities.Contains(identity)).ToHashSet(StringComparer.Ordinal));

        var r2Fp = fpByRepeat[2];
        var sharedR1 = r2Fp.Intersect(fpByRepeat[1], StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var sharedR3 = r2Fp.Intersect(fpByRepeat[3], StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var sharedAll = sharedR1.Intersect(sharedR3, StringComparer.Ordinal).ToArray();
        var r2Only = r2Fp.Except(fpByRepeat[1], StringComparer.Ordinal)
            .Except(fpByRepeat[3], StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);

        var claimByIdentity = claimsByRepeat[2]
            .GroupBy(claim => claim.Identity, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        // ---- false positives, described from the source -------------------------------------------
        var falsePositives = r2Fp.Select(identity =>
        {
            var claim = claimByIdentity[identity];
            var ordinals = claim.Aliases.Where(atomByAlias.ContainsKey)
                .Select(alias => atomByAlias[alias].Ordinal).ToArray();
            var distance = ordinals.Length == 0 || goldAtomOrdinals.Count == 0
                ? int.MaxValue
                : ordinals.Min(ordinal => goldAtomOrdinals.Min(goldOrdinal => Math.Abs(ordinal - goldOrdinal)));
            var overlapsGoldAtom = ordinals.Any(goldAtomOrdinals.Contains);

            return new
            {
                identity,
                claim.Pack,
                aliases = claim.Aliases,
                text = claim.Text,
                claim.SemanticRole,
                claim.PartCount,
                selectionModes = claim.SelectionModes,
                scope = scopeByAlias.GetValueOrDefault(claim.Aliases[0], "(unknown)"),
                block = plan.LayoutBlockByAtom.GetValueOrDefault(
                    atomByAlias.TryGetValue(claim.Aliases[0], out var atom) ? atom.SourceId : string.Empty, "(none)"),
                candidateHinted = claim.Aliases.All(hintedAliases.Contains),
                anyPartHinted = claim.Aliases.Any(hintedAliases.Contains),
                sharedWithR1 = sharedR1.Contains(identity),
                sharedWithR3 = sharedR3.Contains(identity),
                repeat2Only = r2Only.Contains(identity),
                overlapsGoldAtom,
                atomDistanceToNearestGold = distance == int.MaxValue ? -1 : distance,
                endsWithSentencePunctuation = claim.Text.TrimEnd().EndsWith('.') || claim.Text.TrimEnd().EndsWith(','),
                wordCount = claim.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length,
                sourceFunction = SourceFunctionOf(claim.Text),
                goldRelation = overlapsGoldAtom
                    ? "C_BOUNDARY_ALTERNATIVE_OF_GOLD_HEADING"
                    : GoldRelationOf(claim.Text),
            };
        }).OrderBy(item => item.Pack).ThenBy(item => item.aliases[0], StringComparer.Ordinal).ToArray();

        // ---- TP/FN instability --------------------------------------------------------------------
        var tpAllThree = tpByRepeat[1].Intersect(tpByRepeat[2], StringComparer.Ordinal)
            .Intersect(tpByRepeat[3], StringComparer.Ordinal).ToArray();
        var tpR2Only = tpByRepeat[2].Except(tpByRepeat[1], StringComparer.Ordinal)
            .Except(tpByRepeat[3], StringComparer.Ordinal).ToArray();
        var fnR2Only = goldIdentities.Except(tpByRepeat[2], StringComparer.Ordinal)
            .Intersect(tpByRepeat[1], StringComparer.Ordinal)
            .Intersect(tpByRepeat[3], StringComparer.Ordinal).ToArray();
        var fnAllThree = goldIdentities
            .Except(tpByRepeat[1], StringComparer.Ordinal)
            .Except(tpByRepeat[2], StringComparer.Ordinal)
            .Except(tpByRepeat[3], StringComparer.Ordinal).ToArray();

        FreezeArtifact.AssertJson(ForensicRoot, "repeat2-precision-forensics.v1.json", new
        {
            artifactKind = "a99_structured_baseline_precision_forensics",
            schemaVersion = "a99-structured-baseline-precision-forensics-v1",
            experimentId = "A99-S2P-STRUCTURED-BASELINE-V1",
            question = "Where did repeat 2's 73 false positives come from, given byte-identical requests?",
            providerCalls = 0,
            modelCalls = 0,
            intervention = "NONE - diagnosis only; prompt, hints, contract, Gold, binder and evaluator are untouched.",

            frozen = new
            {
                rawResponsesSha256 = RawResponsesSha256,
                goldSha256 = Doc0252GoldSha256,
                sourceAliasUniverseSha256 = plan.SourceUniverseSha256,
                modelVisibleEvidenceSha256 = plan.ModelVisibleEvidenceHash,
                goldClaims = goldIdentities.Count,
            },

            inputEquivalence = InputEquivalence(),

            perPack = cells.OrderBy(cell => cell.Pack).ThenBy(cell => cell.Repeat).Select(cell => new
            {
                pack = cell.Pack,
                repeat = cell.Repeat,
                responseChars = cell.ResponseChars,
                entries = cell.Entries,
                headingTrue = cell.HeadingTrue,
                headingFalse = cell.HeadingFalse,
                bound = cell.Bound,
                rejected = cell.Rejected,
                rejectionReasons = cell.RejectionReasons,
                truePositive = cell.TruePositive,
                falsePositive = cell.FalsePositive,
                headingPositiveRate = cell.Entries == 0 ? 0 : Math.Round((double)cell.HeadingTrue / cell.Entries, 4),
            }).ToArray(),

            repeatTotals = Enumerable.Range(1, Repeats).Select(repeat => new
            {
                repeat,
                bound = boundByRepeat[repeat].Count,
                truePositive = tpByRepeat[repeat].Count,
                falsePositive = fpByRepeat[repeat].Count,
                falseNegative = goldIdentities.Count - tpByRepeat[repeat].Count,
            }).ToArray(),

            repeat2FalsePositiveConcentration = new
            {
                total = r2Fp.Count,
                byPack = falsePositives.GroupBy(item => item.Pack)
                    .OrderBy(group => group.Key)
                    .ToDictionary(group => $"pack{group.Key}", group => group.Count()),
                largestPackShare = falsePositives.Length == 0 ? 0 : Math.Round(
                    (double)falsePositives.GroupBy(item => item.Pack).Max(group => group.Count()) / falsePositives.Length, 4),
            },

            crossRepeatFalsePositives = new
            {
                repeat2Total = r2Fp.Count,
                repeat2Only = r2Only.Count,
                sharedWithRepeat1 = sharedR1.Count,
                sharedWithRepeat3 = sharedR3.Count,
                sharedWithAllThree = sharedAll.Length,
                repeat1Total = fpByRepeat[1].Count,
                repeat3Total = fpByRepeat[3].Count,
            },

            repeat2FalsePositives = falsePositives,

            correlations = new
            {
                candidateHint = new
                {
                    repeat2FalsePositivesAllPartsHinted = falsePositives.Count(item => item.candidateHinted),
                    repeat2FalsePositivesAnyPartHinted = falsePositives.Count(item => item.anyPartHinted),
                    repeat2FalsePositivesUnhinted = falsePositives.Count(item => !item.anyPartHinted),
                    repeat2TruePositivesHinted = tpByRepeat[2].Count(identity =>
                        claimByIdentity[identity].Aliases.Any(hintedAliases.Contains)),
                    hintedAtomsInDocument = hintedAliases.Count,
                    totalAtoms = plan.Atoms.Count,
                },
                proximity = new
                {
                    overlapsAGoldAtom = falsePositives.Count(item => item.overlapsGoldAtom),
                    withinOneAtom = falsePositives.Count(item => item.atomDistanceToNearestGold is >= 0 and <= 1),
                    withinFiveAtoms = falsePositives.Count(item => item.atomDistanceToNearestGold is >= 0 and <= 5),
                    fartherThanFive = falsePositives.Count(item => item.atomDistanceToNearestGold > 5),
                },
                shape = new
                {
                    multiPartClaims = falsePositives.Count(item => item.PartCount > 1),
                    endingInSentencePunctuation = falsePositives.Count(item => item.endsWithSentencePunctuation),
                    bySemanticRole = falsePositives.GroupBy(item => item.SemanticRole)
                        .OrderByDescending(group => group.Count())
                        .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
                    byScope = falsePositives.GroupBy(item => item.scope)
                        .OrderByDescending(group => group.Count())
                        .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
                },
            },

            taxonomy = new
            {
                bySourceFunction = falsePositives.GroupBy(item => item.sourceFunction)
                    .OrderByDescending(group => group.Count())
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
                byGoldRelation = falsePositives.GroupBy(item => item.goldRelation)
                    .OrderByDescending(group => group.Count())
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
                repeat2OnlyBySourceFunction = falsePositives.Where(item => item.repeat2Only)
                    .GroupBy(item => item.sourceFunction)
                    .OrderByDescending(group => group.Count())
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            },

            responseShape = ResponseShape(),

            providerParameters = new
            {
                model = "qwen/qwen3.7-flash",
                temperature = 0,
                reasoningEffort = "none",
                responseFormat = "json_object",
                topP = "NOT_SENT",
                topK = "NOT_SENT",
                seed = "NOT_SENT",
                providerRoute = "NOT_RECORDED",
                persistedInRunArtifact = false,
                note = "Read from the transport that issued the calls, which sends these values on every "
                    + "request, so all three repeats used them. The run artifact itself did not persist "
                    + "them. temperature=0 did not make the provider deterministic: four of six packs "
                    + "returned byte-identical replies across all three repeats and pack 5 did not.",
            },

            statelessness = new
            {
                requestBytesIdenticalAcrossRepeats = true,
                evidence = "Every pack's request hash is equal across all three repeats (inputEquivalence "
                    + "above). A packet is composed in CanonicalSemanticEngine.ComposeSegments from the "
                    + "source evidence window alone; replies are appended to RawResponses and never read "
                    + "back into a later packet, and the classifier is called once per segment with no "
                    + "conversation state. Had pack 5's reply reached pack 6's request, pack 6's bytes "
                    + "would differ in repeat 2, where that reply was four times longer. They do not.",
            },

            placementCalls = new
            {
                semanticExtractionCallsPerRepeat = 6,
                placementCallsTriggered = new { repeat1 = 1, repeat2 = 1, repeat3 = 1 },
                totalCallsIfReplayedWithProvider = new { repeat1 = 7, repeat2 = 7, repeat3 = 7 },
                measuredBy = "offline replay of the captured replies through the production adapter; "
                    + "FrozenReplyClassifier.CallsBeyondRecording counts what a provider would have been asked.",
                note = "Unreachable during the frozen run: DOC-0252 materialized no headings then, so the "
                    + "placement pass had nothing to place. The 21-call figure describes extraction only "
                    + "and is not a budget for a future run.",
            },

            goldStability = new
            {
                foundInAllThree = tpAllThree.Length,
                foundOnlyInRepeat2 = tpR2Only.Select(identity => new
                {
                    identity,
                    text = gold[identity].Text,
                }).ToArray(),
                missedOnlyInRepeat2 = fnR2Only.Select(identity => new
                {
                    identity,
                    text = gold[identity].Text,
                }).ToArray(),
                missedInAllThree = fnAllThree.Select(identity => new
                {
                    identity,
                    text = gold[identity].Text,
                }).ToArray(),
            },
        });

        // The baseline this analysis describes must be the baseline that was scored.
        Assert.Equal(35, tpByRepeat[1].Count);
        Assert.Equal(37, tpByRepeat[2].Count);
        Assert.Equal(36, tpByRepeat[3].Count);
        Assert.Equal(10, fpByRepeat[1].Count);
        Assert.Equal(73, fpByRepeat[2].Count);
        Assert.Equal(11, fpByRepeat[3].Count);
    }

    /// <summary>
    /// Request bytes per pack, per repeat, from the run's own call ledger. Equality here is what
    /// makes "the model answered differently" a statement about the model rather than about the
    /// harness - and it is checked per call, not through an aggregate plan hash.
    /// </summary>
    private static object InputEquivalence()
    {
        using var run = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(RunArtifact)));
        var ledger = run.RootElement.GetProperty("callLedger").EnumerateArray()
            .Where(call => call.GetProperty("DocumentId").GetString() == "DOC-0252")
            .Select(call => new
            {
                Repeat = call.GetProperty("Repeat").GetInt32(),
                Request = call.GetProperty("RequestSha256").GetString()!,
                Chars = call.GetProperty("RequestChars").GetInt32(),
                Ordinal = call.GetProperty("Ordinal").GetInt32(),
            })
            .OrderBy(call => call.Ordinal)
            .ToArray();

        var byRepeat = ledger.GroupBy(call => call.Repeat)
            .ToDictionary(group => group.Key, group => group.ToArray());

        var packs = Enumerable.Range(0, Packs).Select(index => new
        {
            pack = index + 1,
            repeat1 = byRepeat[1][index].Request,
            repeat2 = byRepeat[2][index].Request,
            repeat3 = byRepeat[3][index].Request,
            requestChars = byRepeat[1][index].Chars,
            identical = byRepeat[1][index].Request == byRepeat[2][index].Request &&
                byRepeat[2][index].Request == byRepeat[3][index].Request,
        }).ToArray();

        Assert.All(packs, pack => Assert.True(pack.identical, $"pack {pack.pack} requests differ across repeats"));
        return new { packs, identicalPacks = packs.Count(pack => pack.identical), totalPacks = Packs };
    }

    // ---- the frozen evidence --------------------------------------------------------------------

    private static IReadOnlyList<string> CapturedResponses(string documentId)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(RawResponses)));
        return document.RootElement.EnumerateArray()
            .Where(call => call.GetProperty("DocumentId").GetString() == documentId)
            .OrderBy(call => call.GetProperty("Ordinal").GetInt32())
            .Select(call => call.GetProperty("RawResponse").GetString()!)
            .ToArray();
    }

    private static Dictionary<string, GoldClaim> GoldClaims()
    {
        using var gold = CanonicalGoldRegistry.Resolve("DOC-0252");
        return gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
            .ToDictionary(
                claim => claim.GetProperty("identity").GetString()!,
                claim => new GoldClaim(
                    claim.GetProperty("identity").GetString()!,
                    claim.GetProperty("projectedText").GetString() ?? string.Empty,
                    claim.GetProperty("sourceParts").EnumerateArray()
                        .Select(part => part.GetProperty("sourceAlias").GetString()!).ToArray()),
                StringComparer.Ordinal);
    }

    /// <summary>
    /// What the source row is, read from the row itself. Derived from the cases actually present in
    /// this repeat rather than from a taxonomy decided in advance: the annex holds an agenda laid
    /// out as time slot, title and presenter, and a participant list laid out as bullet, name and
    /// affiliation, and those shapes are what the rules below recognise.
    /// </summary>
    private static string SourceFunctionOf(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length <= 2 && !char.IsLetterOrDigit(trimmed.FirstOrDefault())) return "BULLET_OR_GLYPH_ONLY";
        if (Regex.IsMatch(trimmed, @"^\d{1,2}:\d{2}\s*[\u2013\u2014-]\s*\d{1,2}:\d{2}$"))
            return "AGENDA_TIME_SLOT_ONLY";
        if (Regex.IsMatch(trimmed, @"^\d{1,2}:\d{2}\s*[\u2013\u2014-]\s*\d{1,2}:\d{2}\s+\S"))
            return "AGENDA_TIME_SLOT_WITH_TITLE";
        if (trimmed.EndsWith(']') && trimmed.Contains('[')) return "AGENDA_ITEM_TITLE_WITH_PRESENTER";
        if (Regex.IsMatch(trimmed, @"^[A-Z][a-z]+\s+\d{1,2}[-\u2013]\d{1,2},\s*\d{4}$"))
            return "DATE_LINE";
        if (Regex.IsMatch(trimmed, @"\d{3,5}\s+\w+\s+(Ave|Avenue|St|Street|Rd|Road)"))
            return "ADDRESS_LINE";
        if (trimmed.EndsWith('.') || trimmed.EndsWith(',')) return "BODY_SENTENCE_FRAGMENT";
        if (trimmed.Length <= 6 && trimmed.All(char.IsDigit)) return "PAGE_NUMBER_LIKE";
        if (Regex.IsMatch(trimmed, @"^[A-Z][\w.\u2019'-]+(\s+[A-Z][\w.\u2019'-]+){0,3},\s+\D"))
            return "PARTICIPANT_NAME_AND_AFFILIATION";
        return "OTHER_TITLE_LIKE_ROW";
    }

    /// <summary>
    /// How a false positive stands to Gold, forensically. Gold is not modified and nothing here
    /// proposes that it should be: the distinction exists because "the model invented a heading" and
    /// "the model named a row that reads as a heading in an annex Gold does not cover" are different
    /// findings, and reporting them as one number would hide the second.
    /// </summary>
    private static string GoldRelationOf(string text) => SourceFunctionOf(text) switch
    {
        "AGENDA_ITEM_TITLE_WITH_PRESENTER" or "AGENDA_TIME_SLOT_WITH_TITLE" or "OTHER_TITLE_LIKE_ROW" =>
            "B_PLAUSIBLE_HEADING_NOT_IN_APPROVED_GOLD",
        _ => "A_CLEAR_NON_HEADING_UNDER_CURRENT_GOLD_POLICY",
    };

    /// <summary>One row per captured reply: how much came back, and how many claims were in it.</summary>
    private static object ResponseShape()
    {
        var responses = CapturedResponses("DOC-0252");
        return Enumerable.Range(0, responses.Count).Select(index =>
        {
            using var document = JsonDocument.Parse(responses[index]);
            var entries = document.RootElement.GetProperty("headings").EnumerateArray().ToArray();
            return new
            {
                repeat = (index / Packs) + 1,
                pack = (index % Packs) + 1,
                responseChars = responses[index].Length,
                proposalObjects = entries.Length,
                headingTrue = entries.Count(entry =>
                    entry.TryGetProperty("isHeading", out var value) && value.ValueKind == JsonValueKind.True),
                headingFalse = entries.Count(entry =>
                    entry.TryGetProperty("isHeading", out var value) && value.ValueKind == JsonValueKind.False),
            };
        }).ToArray();
    }

    private sealed record GoldClaim(string Identity, string Text, IReadOnlyList<string> Aliases);

    private sealed record Claim(
        string Identity,
        int Pack,
        IReadOnlyList<string> Aliases,
        string Text,
        string SemanticRole,
        int PartCount,
        IReadOnlyList<string> SelectionModes,
        IReadOnlyList<string> RelationHints);

    private sealed record PackCell(
        int Pack,
        int Repeat,
        int Entries,
        int HeadingTrue,
        int HeadingFalse,
        int Decoded,
        int Bound,
        int Rejected,
        IReadOnlyList<string> RejectionReasons,
        int TruePositive,
        int FalsePositive,
        int ResponseChars);
}
