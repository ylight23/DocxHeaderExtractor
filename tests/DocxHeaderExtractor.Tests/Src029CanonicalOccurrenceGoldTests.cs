using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// SRC029_CANONICAL_OCCURRENCE_GOLD_V1: the 374 headings the user decided on 2026-09-25 (aa9f4ae), written
/// into the authored Gold as structured source parts of the original PDF's atom universe.
/// <para>
/// Membership and parts are the source-only review (<see cref="Src029SourceReviewTests"/>) as the user
/// decided it: A1 is applicability metadata and not a heading; A2, both A3 titles and the three A4 labels
/// are headings; the CON-4 declaration's claim is its title line only. Each claim is exactly the parts the
/// review named - the binder validates them (GENERIC_MULTIPART_BINDER_V2) and adds nothing between them,
/// so the five titles interleaved with the other column or continued on the next page keep their full
/// multipart identity. No model output enters it: the engine's frozen proposals (dc98144) are not read
/// before this Gold is committed. The retired pdf2docx DOCX and its total 356 are lineage only.
/// Write once with A99_SRC029_MATERIALIZE=1; the checks run every time.
/// </para>
/// </summary>
public sealed class Src029CanonicalOccurrenceGoldTests
{
    private const string GoldPath = "eval/a99-closed-loop/gold/SRC-029.gold.json";
    private const string Review = "eval/a99-closed-loop/source-review-v1/SRC-029.source-review.v1.json";
    private const string ReviewItems = Src029SourceReviewTests.Dir + "/review-items.json";
    private const string LineageDir = "eval/a99-closed-loop/source-review-v1";
    private const string Lineage = LineageDir + "/SRC-029.authority-lineage.v1.json";
    private const string PreviousFreeze = "eval/a99-closed-loop/canonical-semantic-gold-vnext/semantic/SRC-029.semantic-freeze.v1.json";
    private const string Regeneration = "eval/a99-closed-loop/generated-docx-v2/SRC-029.conversion-manifest.v1.json";
    private const string Policy = "eval/a99-closed-loop/policy/financial-procurement-heading-policy.v1.json";
    private const string Addendum = "eval/a99-closed-loop/policy/financial-occurrence-distinctions.v1.json";
    private const string Ontology = "eval/a99-closed-loop/policy/occurrence-semantic-axes.v2.json";
    private const string Principles = "eval/a99-closed-loop/policy/occurrence-classification-principles.v1.json";
    private const int ApprovedTotal = 374;

    [Fact]
    public void Freeze_the_authority_lineage()
    {
        using var previous = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(PreviousFreeze)));
        using var regeneration = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Regeneration)));
        var old = previous.RootElement;
        var removed = regeneration.RootElement.GetProperty("removed");
        // The freeze's DOCX is the one the regeneration removed: one retired source, not two.
        Assert.Equal(removed.GetProperty("sha256").GetString(), old.GetProperty("sourceSha256").GetString());
        Assert.Equal(removed.GetProperty("path").GetString(), old.GetProperty("sourcePath").GetString());

        FreezeArtifact.AssertJson(LineageDir, "SRC-029.authority-lineage.v1.json", new
        {
            artifactKind = "a99_gold_authority_lineage",
            authorityId = "SRC-029",
            modelProviderVlmCalls = 0,
            previousSemanticAuthority = new
            {
                source = $"retired {removed.GetProperty("producedBy").GetString()} DOCX",
                sourcePath = old.GetProperty("sourcePath").GetString(),
                sourceSha256 = old.GetProperty("sourceSha256").GetString(),
                total = old.GetProperty("semanticHeadingTotal").GetInt32(),
                exactOccurrenceFreeze = old.GetProperty("exactOccurrenceFreeze").GetBoolean(),
                role = "provenance_only",
                artifact = new { path = PreviousFreeze, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(PreviousFreeze)) },
                retiredBy = new { path = Regeneration, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Regeneration)) },
            },
            currentCanonicalAuthority = new
            {
                source = "original PDF",
                sourcePath = Src029SourceReviewTests.Pdf,
                sourceSha256 = CanonicalArtifactHash.OfBytes(TestRepository.Path(Src029SourceReviewTests.Pdf)),
                total = ApprovedTotal,
                coordinateSystem = "STRUCTURED_SOURCE_PARTS",
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
            },
        });
    }

    [Fact]
    public void Materialize()
    {
        if (Environment.GetEnvironmentVariable("A99_SRC029_MATERIALIZE") != "1") return;

        var path = TestRepository.Path(GoldPath);
        var before = File.ReadAllText(path);
        var gold = JsonNode.Parse(before)!;
        Assert.Null(gold["occurrence"]); // one-shot
        Assert.Equal(356, gold["semanticHeadingTotal"]!.GetValue<int>());

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
        // The 356 freeze stays, as what it now is: the retired authority, lineage only.
        var previous = Assert.Single(provenance, item => item!["path"]!.GetValue<string>() == PreviousFreeze)!;
        previous["role"] = "PREVIOUS_SEMANTIC_AUTHORITY";
        provenance.Add(new JsonObject
        {
            ["path"] = "gold-correction:SRC-029:docx-count-356-to-pdf-occurrence-374:2026-09-25",
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

        // The authority is the original PDF, byte for byte.
        var source = root.GetProperty("source");
        Assert.Equal(Src029SourceReviewTests.Pdf, source.GetProperty("sourcePath").GetString());
        Assert.Equal(CanonicalArtifactHash.OfBytes(TestRepository.Path(Src029SourceReviewTests.Pdf)),
            source.GetProperty("sourceSha256").GetString());
        Assert.Equal(ApprovedTotal, root.GetProperty("semanticHeadingTotal").GetInt32());
        Assert.False(root.GetProperty("declaredCapabilities").GetProperty("hierarchyEvaluable").GetBoolean());

        // The claims are exactly the decided review, rebuilt from the source now.
        var expected = BuildClaims().Select(c => c.ToJsonString()).ToArray();
        var actual = occurrence.GetProperty("claims").EnumerateArray().Select(c => JsonNode.Parse(c.GetRawText())!.ToJsonString()).ToArray();
        Assert.Equal(expected, actual);

        // Independently of the rebuild: every recorded span is the atom's own text at that offset.
        var atoms = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Src029SourceReviewTests.Pdf)).Atoms
            .ToDictionary(a => a.Alias, StringComparer.Ordinal);
        var taken = new Dictionary<string, List<(int Start, int End)>>(StringComparer.Ordinal);
        foreach (var claim in occurrence.GetProperty("claims").EnumerateArray())
        {
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
                if (named[i].GetProperty("selectionMode").GetString() == "WHOLE_ALIAS")
                    Assert.Equal((0, text.Length), (start, end));
                else
                    Assert.Equal(named[i].GetProperty("verbatimText").GetString(), text[start..end]);

                // No two claims share a character.
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
            .Select(c => (Identity: c.GetProperty("identity").GetString()!,
                Aliases: c.GetProperty("sourceParts").EnumerateArray().Select(p => p.GetProperty("sourceAlias").GetString()!).ToArray(),
                Localities: c.GetProperty("boundParts").EnumerateArray().Select(p => p.GetProperty("localityFromPrevious").GetString()).ToArray(),
                Axes: c.GetProperty("semanticAxes")))
            .ToArray();
        var inAnyClaim = claims.SelectMany(c => c.Aliases).ToHashSet(StringComparer.Ordinal);
        string[] Claim(params string[] aliases) =>
            Assert.Single(claims, c => c.Aliases.SequenceEqual(aliases)).Localities;

        // A1: applicability metadata, absent from heading Gold.
        Assert.DoesNotContain("L0006:S0", inAnyClaim);
        Assert.DoesNotContain("L0007:S0", inAnyClaim);
        Assert.DoesNotContain("L0008:S0", inAnyClaim);
        // A2, A3a, A4 x3: present, each its own atom.
        Claim("L2930:S0");
        Claim("L3216:S0");
        Assert.Equal("REPEATED", Assert.Single(claims, c => c.Aliases.SequenceEqual(["L3216:S0"])).Axes.GetProperty("repeatStatus").GetString());
        Claim("L3766:S0");
        Claim("L3779:S0");
        Claim("L3796:S0");
        // A3b: the title line only; the "in accordance with ..." lines are in no claim.
        Claim("L3275:S0");
        Assert.DoesNotContain("L3276:S0", inAnyClaim);
        Assert.DoesNotContain("L3277:S0", inAnyClaim);

        // T1/T2: full multipart identity; nothing between the parts is pulled in.
        Assert.Equal([null, "SamePageNonAdjacent", "NextRowCompatible"], Claim("L4787:S0", "L4789:S0", "L4790:S0"));
        Assert.Equal([null, "CrossPage", "NextRowCompatible"], Claim("L5519:S0", "L5523:S0", "L5524:S0"));
        Assert.Equal([null, "SamePageNonAdjacent"], Claim("L5782:S0", "L5784:S0"));
        Assert.Equal([null, "SamePageNonAdjacent"], Claim("L5802:S0", "L5804:S0"));
        Assert.Equal([null, "CrossPage", "NextRowCompatible", "NextRowCompatible"], Claim("L5847:S0", "L5850:S0", "L5851:S0", "L5852:S0"));
        foreach (var between in new[] { "L4788:S0", "L5520:S0", "L5521:S0", "L5522:S0", "L5783:S0", "L5803:S0", "L5848:S0", "L5849:S0" })
            Assert.DoesNotContain(between, inAnyClaim);
    }

    /// <summary>The decided headings as authored claims, in source order, each bound by the production binder.</summary>
    private static List<JsonObject> BuildClaims()
    {
        var atoms = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Src029SourceReviewTests.Pdf)).Atoms;
        var byAlias = atoms.ToDictionary(a => a.Alias, StringComparer.Ordinal);
        var items = Src029SourceReviewTests.Items();
        Assert.DoesNotContain(items, i => i.Verdict == "AMBIGUOUS");

        var bindings = items.Select(i => (Item: i, Binding: Src029SourceReviewTests.Bind(atoms, i))).ToArray();
        Assert.All(bindings, b => Assert.True(b.Binding.IsBound, $"{b.Item.Text}: {b.Binding.Reason}"));
        var ordered = bindings
            .OrderBy(b => b.Binding.Parts[0].Ordinal).ThenBy(b => b.Binding.Parts[0].Start)
            .ToArray();

        // Repeat status as for DOC-0133: an occurrence whose squashed text was already seen, among
        // every reviewed occurrence, in source order. Independent of whether either is a heading.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var repeats = ordered.ToDictionary(b => b.Item, b => seen.Add(Squash(b.Item.Text)) ? "FIRST" : "REPEATED");

        var claims = new List<JsonObject>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        // Claims are ordered by where they begin. They may interleave - a margin title running down
        // three rows beside a sub-heading in the other column - so a claim need not end before the
        // next begins; the parts within a claim are in source order (the binder refuses otherwise)
        // and no two claims share a character (checked on the written Gold).
        (int Ordinal, int Start) last = (-1, -1);
        foreach (var (item, binding) in ordered.Where(b => b.Item.Verdict == "HEADING"))
        {
            var first = binding.Parts[0];
            Assert.True((first.Ordinal, first.Start).CompareTo(last) > 0, $"{item.Text}: claims out of source order");
            last = (first.Ordinal, first.Start);
            Assert.True(identities.Add(binding.Identity), $"{item.Text}: duplicate claim identity");

            var axes = item.Axes;
            var functions = axes.GetProperty("semanticFunctions").EnumerateArray().Select(f => (JsonNode)f.GetString()!).ToArray();
            var roles = axes.GetProperty("occurrenceRoles").EnumerateArray().Select(r => (JsonNode)r.GetString()!).ToArray();
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
                    ["semanticFunctions"] = new JsonArray(functions),
                    ["primaryFunction"] = axes.GetProperty("primaryFunction").GetString(),
                    ["scope"] = axes.GetProperty("scope").GetString(),
                    ["occurrenceRoles"] = new JsonArray(roles),
                    ["titleRelation"] = titleRelation,
                    ["titlePartAliases"] = new JsonArray(titleRelation == "TITLE"
                        ? item.Parts.Select(p => p.SourceAlias).Distinct().Where(a => a != item.Parts[0].SourceAlias).Select(a => (JsonNode)a).ToArray()
                        : []),
                    ["repeatStatus"] = repeats[item],
                    ["informationType"] = axes.GetProperty("informationType").ValueKind == JsonValueKind.Null
                        ? null : axes.GetProperty("informationType").GetString(),
                },
                ["pattern"] = item.Pattern,
                ["evidence"] = item.Reason,
            });
        }
        return claims;
    }

    private static string Squash(string value) => Regex.Replace(value, @"\s+", "").Replace('\'', '’');
}
