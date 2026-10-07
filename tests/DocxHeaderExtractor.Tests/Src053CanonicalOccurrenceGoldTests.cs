using System.Text;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// SRC053_CANONICAL_OCCURRENCE_GOLD_V1: the 271 headings the user decided on 2026-09-26 (797b22c), written into the
/// authored Gold as structured source parts of the original PDF's atom universe.
/// <para>
/// Membership and parts are the source-only review (<see cref="Src053SourceReviewTests"/>) as the user decided it:
/// four ambiguous patterns decided by the user (S053_Q1-Q4), everything else by precedent. Each claim is exactly the
/// parts the review named, bound by the production binder, which adds nothing between them. Axes are
/// OCCURRENCE_SEMANTIC_AXES_V3: no repeatStatus. No model output enters it: the engine's blind proposals (d3152ce)
/// are not opened before this Gold is committed. The previous authority - a count-only review of this same PDF,
/// total 277, with no occurrence list - is lineage only.
/// Write once with A99_SRC053_MATERIALIZE=1; the checks run every time.
/// </para>
/// </summary>
public sealed class Src053CanonicalOccurrenceGoldTests
{
    private const string GoldPath = "eval/a99-closed-loop/gold/SRC-053.gold.json";
    private const string Review = "eval/a99-closed-loop/source-review-v1/SRC-053.source-review.v1.json";
    private const string ReviewItems = "eval/a99-closed-loop/source-review-v1/SRC-053/review-items.json";
    private const string LineageDir = "eval/a99-closed-loop/source-review-v1";
    private const string Lineage = LineageDir + "/SRC-053.authority-lineage.v1.json";
    private const string PreviousFreeze = "eval/a99-closed-loop/canonical-semantic-gold-vnext/semantic/SRC-053.semantic-freeze.v1.json";
    private const string Policy = "eval/a99-closed-loop/policy/financial-procurement-heading-policy.v1.json";
    private const string Addendum = "eval/a99-closed-loop/policy/financial-occurrence-distinctions.v1.json";
    private const string Ontology = "eval/a99-closed-loop/policy/occurrence-semantic-axes.v3.json";
    private const string Principles = "eval/a99-closed-loop/policy/occurrence-classification-principles.v1.json";
    private const int ApprovedTotal = 271;

    [Fact]
    public void Freeze_the_authority_lineage()
    {
        using var previous = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(PreviousFreeze)));
        var old = previous.RootElement;
        // The previous authority described this very PDF: same path, same bytes.
        Assert.Equal(Src053SourceReviewTests.Pdf, old.GetProperty("sourcePath").GetString());
        Assert.Equal(CanonicalArtifactHash.OfBytes(TestRepository.Path(Src053SourceReviewTests.Pdf)), old.GetProperty("sourceSha256").GetString());
        Assert.False(old.GetProperty("exactOccurrenceFreeze").GetBoolean());

        FreezeArtifact.AssertJson(LineageDir, "SRC-053.authority-lineage.v1.json", new
        {
            artifactKind = "a99_gold_authority_lineage",
            authorityId = "SRC-053",
            modelProviderVlmCalls = 0,
            previousSemanticAuthority = new
            {
                source = "original PDF (the same bytes as the current authority)",
                sourcePath = old.GetProperty("sourcePath").GetString(),
                sourceSha256 = old.GetProperty("sourceSha256").GetString(),
                total = old.GetProperty("semanticHeadingTotal").GetInt32(),
                finalAuthority = old.GetProperty("finalAuthority").GetString(),
                exactOccurrenceFreeze = old.GetProperty("exactOccurrenceFreeze").GetBoolean(),
                occurrenceList = false,
                occurrenceIdentities = "UNAVAILABLE",
                exactSpans = "UNAVAILABLE",
                status = "SUPERSEDED",
                role = "provenance_only",
                artifact = new { path = PreviousFreeze, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(PreviousFreeze)) },
            },
            // Why 271 is the authority: not because it is newer, but because it can be audited occurrence by
            // occurrence and was made independently of the engine and of the old count (user, 2026-09-25, on SRC-042; applied here unchanged).
            supersededBecause = "the current authority names every occurrence and binds each one exactly to the source, independently of the engine and of the previous count; the previous authority is a total without occurrence identities",
            historicalAuthorityDelta = new
            {
                HISTORICAL_AUTHORITY_DELTA = ApprovedTotal - old.GetProperty("semanticHeadingTotal").GetInt32(),
                CAUSE = "UNRESOLVED",
                meaning = "a difference between two authorities over the same PDF; not evidence that the current Gold misses any occurrence",
                followUp = "a historical delta audit only after the Gold and the raw blind score are committed: source-level evidence only, never engine predictions; the previous review's own record names the 6 occurrences its re-audit added (all repeated '(Continued)' statement titles, which the current Gold contains)",
                goldRevisionRule = "a Gold error found later becomes a new Gold revision with its own provenance; the blind score stays scored against the Gold frozen at the reveal",
            },
            currentCanonicalAuthority = new
            {
                source = "original PDF",
                sourcePath = Src053SourceReviewTests.Pdf,
                sourceSha256 = CanonicalArtifactHash.OfBytes(TestRepository.Path(Src053SourceReviewTests.Pdf)),
                total = ApprovedTotal,
                coordinateSystem = "STRUCTURED_SOURCE_PARTS",
                ontology = "OCCURRENCE_SEMANTIC_AXES_V3",
                semanticClaimsEvaluable = true,
                occurrenceEvaluable = true,
                characterSpanEvaluable = true,
                hierarchyEvaluable = false,
                membershipDecision = new
                {
                    decidedBy = "USER",
                    decidedAt = "2026-09-26",
                    review = new { path = Review, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Review)) },
                    reviewItems = new { path = ReviewItems, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(ReviewItems)) },
                },
                engineProposalsRead = false,
                status = "CURRENT_OCCURRENCE_LEVEL_AUTHORITY",
            },
        });
    }

    [Fact]
    public void Materialize()
    {
        if (Environment.GetEnvironmentVariable("A99_SRC053_MATERIALIZE") != "1") return;

        var path = TestRepository.Path(GoldPath);
        var before = File.ReadAllText(path);
        var gold = JsonNode.Parse(before)!;
        Assert.Null(gold["occurrence"]); // one-shot
        Assert.Equal(277, gold["semanticHeadingTotal"]!.GetValue<int>());

        var claims = BuildClaims();
        Assert.Equal(ApprovedTotal, claims.Count);

        gold["semanticHeadingTotal"] = ApprovedTotal;
        gold["approval"]!["approvalBasis"] = "USER_ADJUDICATED_STRUCTURAL_AUDIT";
        gold["approval"]!["approvedAt"] = "2026-09-26";
        gold["approval"]!["semanticTruthReusedFrom"] = new JsonArray(Review);
        gold["occurrence"] = new JsonObject
        {
            ["coordinateSystem"] = "STRUCTURED_SOURCE_PARTS",
            ["claims"] = new JsonArray(claims.Select(c => (JsonNode)c).ToArray()),
        };
        gold["occurrenceUnavailableReason"] = null;
        var provenance = gold["provenance"]!.AsArray();
        // The 277 freeze stays, as what it now is: the previous authority, lineage only.
        var previous = Assert.Single(provenance, item => item!["path"]!.GetValue<string>() == PreviousFreeze)!;
        previous["role"] = "PREVIOUS_SEMANTIC_AUTHORITY";
        provenance.Add(new JsonObject
        {
            ["path"] = "gold-correction:SRC-053:pdf-count-277-to-pdf-occurrence-271:2026-09-26",
            ["sha256"] = CanonicalArtifactHash.OfText(before),
            ["role"] = "GOLD_CORRECTION_PREDECESSOR",
        });
        foreach (var (item, role) in new[]
                 {
                     (Review, "SEMANTIC_AUTHORITY"), (ReviewItems, "SOURCE_REVIEW_ITEMS"), (Lineage, "AUTHORITY_LINEAGE"),
                     (Policy, "HEADING_POLICY"), (Addendum, "HEADING_POLICY_ADDENDUM"),
                     (Ontology, "OCCURRENCE_ONTOLOGY"), (Principles, "CLASSIFICATION_PRINCIPLES"),
                 })
            provenance.Add(new JsonObject
            {
                ["path"] = item,
                ["sha256"] = CanonicalArtifactHash.OfTextFile(TestRepository.Path(item)),
                ["role"] = role,
            });
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(gold.ToJsonString(FreezeArtifact.Json).ReplaceLineEndings("\n")));
    }

    [Fact]
    public void The_gold_is_the_decided_review_and_every_claim_is_exact()
    {
        using var gold = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(GoldPath)));
        var root = gold.RootElement;
        var occurrence = root.GetProperty("occurrence");
        if (occurrence.ValueKind == JsonValueKind.Null) return; // not materialized yet

        var source = root.GetProperty("source");
        Assert.Equal(Src053SourceReviewTests.Pdf, source.GetProperty("sourcePath").GetString());
        Assert.Equal(CanonicalArtifactHash.OfBytes(TestRepository.Path(Src053SourceReviewTests.Pdf)), source.GetProperty("sourceSha256").GetString());
        Assert.Equal(ApprovedTotal, root.GetProperty("semanticHeadingTotal").GetInt32());
        Assert.False(root.GetProperty("declaredCapabilities").GetProperty("hierarchyEvaluable").GetBoolean());

        var expected = BuildClaims().Select(c => c.ToJsonString()).ToArray();
        var actual = occurrence.GetProperty("claims").EnumerateArray().Select(c => JsonNode.Parse(c.GetRawText())!.ToJsonString()).ToArray();
        Assert.Equal(expected, actual);

        var atoms = PdfSourceAdapter.Build(TestRepository.Path(Src053SourceReviewTests.Pdf)).Atoms
            .ToDictionary(a => a.Alias, StringComparer.Ordinal);
        var taken = new Dictionary<string, List<(int Start, int End)>>(StringComparer.Ordinal);
        foreach (var claim in occurrence.GetProperty("claims").EnumerateArray())
        {
            Assert.False(claim.GetProperty("semanticAxes").TryGetProperty("repeatStatus", out _));
            var named = claim.GetProperty("sourceParts").EnumerateArray().ToArray();
            var bound = claim.GetProperty("boundParts").EnumerateArray().ToArray();
            Assert.Equal(named.Length, bound.Length);
            for (var i = 0; i < named.Length; i++)
            {
                var alias = bound[i].GetProperty("sourceAlias").GetString()!;
                Assert.Equal(named[i].GetProperty("sourceAlias").GetString(), alias);
                var span = bound[i].GetProperty("utf16Span");
                var (start, end) = (span.GetProperty("start").GetInt32(), span.GetProperty("end").GetInt32());
                var text = atoms[alias].Text;
                Assert.Equal(text[start..end], bound[i].GetProperty("text").GetString());
                Assert.Equal("WHOLE_ALIAS", named[i].GetProperty("selectionMode").GetString());
                Assert.Equal((0, text.Length), (start, end));
                var spans = taken.TryGetValue(alias, out var list) ? list : taken[alias] = [];
                Assert.DoesNotContain(spans, s => start < s.End && s.Start < end);
                spans.Add((start, end));
            }
        }
    }

    [Fact]
    public void The_user_decisions_are_in_the_gold_as_decided()
    {
        using var gold = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(GoldPath)));
        var occurrence = gold.RootElement.GetProperty("occurrence");
        if (occurrence.ValueKind == JsonValueKind.Null) return;
        var claims = occurrence.GetProperty("claims").EnumerateArray()
            .Select(c => c.GetProperty("sourceParts").EnumerateArray().Select(p => p.GetProperty("sourceAlias").GetString()!).ToArray())
            .ToArray();
        var inAnyClaim = claims.SelectMany(c => c).ToHashSet(StringComparer.Ordinal);
        void Claim(params string[] aliases) => Assert.Single(claims, c => c.SequenceEqual(aliases));

        // S053_Q1 (user, TRUE): the back-cover title; the issuer under it is in no claim; the contents opener is.
        Claim("L5185:S0");
        Assert.DoesNotContain("L5186:S0", inAnyClaim);
        Claim("L5220:S0");
        // S053_Q2 (user, TRUE x3): italic standalone labels over their own prose.
        foreach (var italic in new[] { "L0572:S0", "L2128:S0", "L2188:S0" })
            Claim(italic);
        // S053_Q3 (user, title only): the cover title is one line; the larger issuer below it is in no claim.
        Claim("L0000:S0");
        Assert.DoesNotContain("L0001:S0", inAnyClaim);
        // S053_Q4 (user, FALSE): the bulleted list item over its sub-list.
        Assert.DoesNotContain("L0828:S0", inAnyClaim);
        // A2 by precedent: the repeated statement titles.
        foreach (var repeated in new[] { new[] { "L2985:S0" }, ["L3224:S0"], ["L3277:S0"], ["L3366:S0", "L3367:S0"], ["L3414:S0", "L3415:S0"], ["L3461:S0", "L3462:S0"], ["L3509:S0", "L3510:S0"] })
            Claim(repeated);
        // S042_A1 (revised) by precedent: the section heading is present; Box 2's title is in no claim.
        Claim("L1076:S0");
        Assert.DoesNotContain("L1080:S0", inAnyClaim);
        // A5 by precedent; the page notice, the part issuer and dates are in no claim.
        Claim("L2709:S0");
        Claim("L2697:S0", "L2698:S0");
        foreach (var other in new[] { "L2694:S0", "L2696:S0", "L2699:S0", "L0049:S0" })
            Assert.DoesNotContain(other, inAnyClaim);
    }

    /// <summary>The decided headings as authored claims, in source order, each bound by the production binder.</summary>
    private static List<JsonObject> BuildClaims()
    {
        var atoms = PdfSourceAdapter.Build(TestRepository.Path(Src053SourceReviewTests.Pdf)).Atoms;
        var byAlias = atoms.ToDictionary(a => a.Alias, StringComparer.Ordinal);
        var items = Src053SourceReviewTests.Items();
        Assert.DoesNotContain(items, i => i.Verdict == "AMBIGUOUS");

        var bindings = items.Select(i => (Item: i, Binding: Src053SourceReviewTests.Bind(atoms, i))).ToArray();
        Assert.All(bindings, b => Assert.True(b.Binding.IsBound, $"{b.Item.Text}: {b.Binding.Reason}"));
        var ordered = bindings.OrderBy(b => b.Binding.Parts[0].Ordinal).ThenBy(b => b.Binding.Parts[0].Start).ToArray();

        var claims = new List<JsonObject>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        (int Ordinal, int Start) last = (-1, -1);
        foreach (var (item, binding) in ordered.Where(b => b.Item.Verdict == "HEADING"))
        {
            var first = binding.Parts[0];
            Assert.True((first.Ordinal, first.Start).CompareTo(last) > 0, $"{item.Text}: claims out of source order");
            last = (first.Ordinal, first.Start);
            Assert.True(identities.Add(binding.Identity), $"{item.Text}: duplicate claim identity");

            var axes = item.Axes;
            var titleRelation = axes.GetProperty("titleRelation").GetString();
            claims.Add(new JsonObject
            {
                ["approvedWording"] = item.Text,
                ["sourceParts"] = new JsonArray(item.Parts.Select(p => (JsonNode)new JsonObject
                {
                    ["sourceAlias"] = p.SourceAlias,
                    ["selectionMode"] = p.SelectionMode,
                    ["verbatimText"] = p.VerbatimText,
                    ["occurrence"] = p.Occurrence,
                    ["leftExactContext"] = null,
                    ["rightExactContext"] = null,
                }).ToArray()),
                ["semanticRole"] = null,
                ["identity"] = binding.Identity,
                ["projectedText"] = string.Join(" ", binding.Parts.Select(p => p.Text)),
                ["boundParts"] = new JsonArray(binding.Parts.Select((p, index) => (JsonNode)new JsonObject
                {
                    ["sourceAlias"] = p.Alias,
                    ["page"] = byAlias[p.Alias].Page,
                    ["row"] = byAlias[p.Alias].Row,
                    ["utf16Span"] = new JsonObject { ["start"] = p.Start, ["end"] = p.End },
                    ["text"] = p.Text,
                    ["localityFromPrevious"] = index == 0 ? null : p.LocalityFromPrevious.ToString(),
                }).ToArray()),
                ["semanticAxes"] = new JsonObject
                {
                    ["semanticFunctions"] = new JsonArray(axes.GetProperty("semanticFunctions").EnumerateArray().Select(f => (JsonNode)f.GetString()!).ToArray()),
                    ["primaryFunction"] = axes.GetProperty("primaryFunction").GetString(),
                    ["scope"] = axes.GetProperty("scope").GetString(),
                    ["occurrenceRoles"] = new JsonArray(axes.GetProperty("occurrenceRoles").EnumerateArray().Select(r => (JsonNode)r.GetString()!).ToArray()),
                    ["titleRelation"] = titleRelation,
                    ["titlePartAliases"] = new JsonArray(titleRelation == "TITLE"
                        ? item.Parts.Select(p => p.SourceAlias).Distinct().Where(a => a != item.Parts[0].SourceAlias).Select(a => (JsonNode)a).ToArray()
                        : []),
                    ["informationType"] = axes.GetProperty("informationType").ValueKind == JsonValueKind.Null
                        ? null : axes.GetProperty("informationType").GetString(),
                },
                ["pattern"] = item.Pattern,
                ["evidence"] = item.Reason,
            });
        }
        return claims;
    }
}
