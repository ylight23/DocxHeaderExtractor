using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// SRC041_CANONICAL_OCCURRENCE_GOLD_V1: the 280 headings the user decided on 2026-09-25 (5f95cb3), written
/// into the authored Gold as structured source parts of the original PDF's atom universe.
/// <para>
/// Membership and parts are the source-only review (<see cref="Src041SourceReviewTests"/>) as the user
/// decided it. Each claim is exactly the parts the review named, bound by the production binder, which adds
/// nothing between them. Axes are OCCURRENCE_SEMANTIC_AXES_V3: no repeatStatus - PRIMARY / REPEAT /
/// CONTINUATION is derived later, between resolved claims. No model output enters it: the engine's blind
/// proposals (10b3317) are not opened before this Gold is committed. The previous authority - a
/// count-only review of this same PDF, total 297, with no occurrence list - is lineage only.
/// Write once with A99_SRC041_MATERIALIZE=1; the checks run every time.
/// </para>
/// </summary>
public sealed class Src041CanonicalOccurrenceGoldTests
{
    private const string GoldPath = "eval/a99-closed-loop/gold/SRC-041.gold.json";
    private const string Review = "eval/a99-closed-loop/source-review-v1/SRC-041.source-review.v1.json";
    private const string ReviewItems = "eval/a99-closed-loop/source-review-v1/SRC-041/review-items.json";
    private const string LineageDir = "eval/a99-closed-loop/source-review-v1";
    private const string Lineage = LineageDir + "/SRC-041.authority-lineage.v1.json";
    private const string PreviousFreeze = "eval/a99-closed-loop/canonical-semantic-gold-vnext/semantic/SRC-041.semantic-freeze.v1.json";
    private const string Preregistration = "eval/a99-closed-loop/generic-audit-v1_1/SRC-041.preregistration.v1_1.json";
    private const string Policy = "eval/a99-closed-loop/policy/financial-procurement-heading-policy.v1.json";
    private const string Addendum = "eval/a99-closed-loop/policy/financial-occurrence-distinctions.v1.json";
    private const string Ontology = "eval/a99-closed-loop/policy/occurrence-semantic-axes.v3.json";
    private const string Principles = "eval/a99-closed-loop/policy/occurrence-classification-principles.v1.json";
    private const int ApprovedTotal = 280;

    [Fact]
    public void Freeze_the_authority_lineage()
    {
        using var previous = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(PreviousFreeze)));
        var old = previous.RootElement;
        // The previous authority described this very PDF: same path, same bytes.
        Assert.Equal(Src041SourceReviewTests.Pdf, old.GetProperty("sourcePath").GetString());
        Assert.Equal(CanonicalArtifactHash.OfBytes(TestRepository.Path(Src041SourceReviewTests.Pdf)), old.GetProperty("sourceSha256").GetString());
        Assert.False(old.GetProperty("exactOccurrenceFreeze").GetBoolean());

        FreezeArtifact.AssertJson(LineageDir, "SRC-041.authority-lineage.v1.json", new
        {
            artifactKind = "a99_gold_authority_lineage",
            authorityId = "SRC-041",
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
            // Why 280 is the authority: not because it is newer, but because it can be audited occurrence by
            // occurrence and was made independently of the engine and of the old count (user, 2026-09-25).
            supersededBecause = "the current authority names every occurrence and binds each one exactly to the source, independently of the engine and of the previous count; the previous authority is a total without occurrence identities",
            historicalAuthorityDelta = new
            {
                HISTORICAL_AUTHORITY_DELTA = ApprovedTotal - old.GetProperty("semanticHeadingTotal").GetInt32(),
                CAUSE = "UNRESOLVED",
                meaning = "a difference between two authorities over the same PDF; not evidence that the current Gold misses any occurrence",
                followUp = "SRC041_HISTORICAL_297_DELTA_AUDIT_V1, only after the Gold and the raw blind score are committed: source-level evidence only, never engine predictions; if the previous review left no occurrence notes, the exact delta is not recoverable",
                goldRevisionRule = "a Gold error found later becomes a new Gold revision with its own provenance; the blind score stays scored against the Gold frozen at the reveal",
            },
            currentCanonicalAuthority = new
            {
                source = "original PDF",
                sourcePath = Src041SourceReviewTests.Pdf,
                sourceSha256 = CanonicalArtifactHash.OfBytes(TestRepository.Path(Src041SourceReviewTests.Pdf)),
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
                    decidedAt = "2026-09-25",
                    review = new { path = Review, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Review)) },
                    reviewItems = new { path = ReviewItems, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(ReviewItems)) },
                },
                engineProposalsRead = false,
                status = "CURRENT_OCCURRENCE_LEVEL_AUTHORITY",
            },
            erratum = new
            {
                affected = new[]
                {
                    new { path = Preregistration, field = "sourceNote", commit = "35a2040" },
                    new { path = "tests/DocxHeaderExtractor.Tests/Src041SourceReviewTests.cs", field = "summary comment", commit = "614c8fc" },
                },
                statedThen = "SRC-041's Gold file holds only a count-only total recorded on a retired pdf2docx DOCX",
                correction = "the count-only total (297) was a user-approved source-only review of this same original PDF (2026-09-12) with no occurrence list; no DOCX was involved",
                effect = "none on the study: the total was neither engine input nor target, and neither the review nor the decisions read it. The pre-registration is not rewritten; this lineage records the correction",
            },
        });
    }

    [Fact]
    public void Materialize()
    {
        if (Environment.GetEnvironmentVariable("A99_SRC041_MATERIALIZE") != "1") return;

        var path = TestRepository.Path(GoldPath);
        var before = File.ReadAllText(path);
        var gold = JsonNode.Parse(before)!;
        Assert.Null(gold["occurrence"]); // one-shot
        Assert.Equal(297, gold["semanticHeadingTotal"]!.GetValue<int>());

        var claims = BuildClaims();
        Assert.Equal(ApprovedTotal, claims.Count);

        gold["semanticHeadingTotal"] = ApprovedTotal;
        gold["approval"]!["approvalBasis"] = "USER_ADJUDICATED_STRUCTURAL_AUDIT";
        gold["approval"]!["approvedAt"] = "2026-09-25";
        gold["approval"]!["semanticTruthReusedFrom"] = new JsonArray(Review);
        gold["occurrence"] = new JsonObject
        {
            ["coordinateSystem"] = "STRUCTURED_SOURCE_PARTS",
            ["claims"] = new JsonArray(claims.Select(c => (JsonNode)c).ToArray()),
        };
        gold["occurrenceUnavailableReason"] = null;
        var provenance = gold["provenance"]!.AsArray();
        // The 297 freeze stays, as what it now is: the previous authority, lineage only.
        var previous = Assert.Single(provenance, item => item!["path"]!.GetValue<string>() == PreviousFreeze)!;
        previous["role"] = "PREVIOUS_SEMANTIC_AUTHORITY";
        provenance.Add(new JsonObject
        {
            ["path"] = "gold-correction:SRC-041:pdf-count-297-to-pdf-occurrence-280:2026-09-25",
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
        Assert.Equal(Src041SourceReviewTests.Pdf, source.GetProperty("sourcePath").GetString());
        Assert.Equal(CanonicalArtifactHash.OfBytes(TestRepository.Path(Src041SourceReviewTests.Pdf)), source.GetProperty("sourceSha256").GetString());
        Assert.Equal(ApprovedTotal, root.GetProperty("semanticHeadingTotal").GetInt32());
        Assert.False(root.GetProperty("declaredCapabilities").GetProperty("hierarchyEvaluable").GetBoolean());

        var expected = BuildClaims().Select(c => c.ToJsonString()).ToArray();
        var actual = occurrence.GetProperty("claims").EnumerateArray().Select(c => JsonNode.Parse(c.GetRawText())!.ToJsonString()).ToArray();
        Assert.Equal(expected, actual);

        var atoms = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Src041SourceReviewTests.Pdf)).Atoms
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

        // A2: five display-title occurrences of a continuing statement or report.
        Claim("L3476:S0");
        Claim("L3585:S0", "L3586:S0");
        Claim("L3644:S0", "L3645:S0");
        Claim("L3703:S0", "L3704:S0");
        Claim("L3185:S0");
        // A3: this occurrence is a heading; the same text atop p4 and p5 is a running header in no claim.
        Claim("L0006:S0");
        Assert.DoesNotContain("L0057:S0", inAnyClaim);
        Assert.DoesNotContain("L0105:S0", inAnyClaim);
        // A4: the appendix list title is in no claim.
        Assert.DoesNotContain("L3139:S0", inAnyClaim);
        // A5: the contents sub-group opener is a heading; its two entries are not.
        Claim("L3169:S0");
        Assert.DoesNotContain("L3170:S0", inAnyClaim);
        Assert.DoesNotContain("L3171:S0", inAnyClaim);
        // Cover: the title is one claim of three lines; issuer and date are in none.
        Claim("L0002:S0", "L0003:S0", "L0004:S0");
        foreach (var metadata in new[] { "L0000:S0", "L0001:S0", "L0005:S0", "L3158:S0", "L3160:S0", "L3187:S0", "L3189:S0" })
            Assert.DoesNotContain(metadata, inAnyClaim);
    }

    /// <summary>The decided headings as authored claims, in source order, each bound by the production binder.</summary>
    private static List<JsonObject> BuildClaims()
    {
        var atoms = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Src041SourceReviewTests.Pdf)).Atoms;
        var byAlias = atoms.ToDictionary(a => a.Alias, StringComparer.Ordinal);
        var items = Src041SourceReviewTests.Items();
        Assert.DoesNotContain(items, i => i.Verdict == "AMBIGUOUS");

        var bindings = items.Select(i => (Item: i, Binding: Src041SourceReviewTests.Bind(atoms, i))).ToArray();
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
