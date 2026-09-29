using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// SRC089_CANONICAL_OCCURRENCE_GOLD_V1: the 36 headings the user decided on 2026-09-27 (e01ca71), written into a new
/// authored Gold over the original PDF's atom universe - this source had no Gold or occurrence list before.
/// <para>
/// Membership and parts are the source-only review (<see cref="Src089SourceReviewTests"/>) as the user decided it (S089_Q1,
/// S089_Q2, S089_Q3; the articles by function). Each claim is exactly the parts the review named, bound by the production
/// binder. Axes are OCCURRENCE_SEMANTIC_AXES_V3. No model output enters it: the engine's blind proposals (78df425) are not
/// opened before this Gold is committed. Written once with A99_SRC089_CREATE=1; the checks run every time.
/// </para>
/// </summary>
public sealed class Src089CanonicalOccurrenceGoldTests
{
    private const string GoldPath = "eval/a99-closed-loop/gold/SRC-089.gold.json";

    private const string Review = "eval/a99-closed-loop/source-review-v1/SRC-089.source-review.v1.json";
    private const string ReviewItems = "eval/a99-closed-loop/source-review-v1/SRC-089/review-items.json";
    private const string LineageDir = "eval/a99-closed-loop/source-review-v1";
    private const string Lineage = LineageDir + "/SRC-089.authority-lineage.v1.json";
    private const string Policy = "eval/a99-closed-loop/policy/financial-procurement-heading-policy.v1.json";
    private const string Addendum = "eval/a99-closed-loop/policy/financial-occurrence-distinctions.v1.json";
    private const string Ontology = "eval/a99-closed-loop/policy/occurrence-semantic-axes.v3.json";
    private const string Principles = "eval/a99-closed-loop/policy/occurrence-classification-principles.v1.json";
    private const int ApprovedTotal = 36;

    [Fact]
    public void Freeze_the_authority_lineage()
    {
        FreezeArtifact.AssertJson(LineageDir, "SRC-089.authority-lineage.v1.json", new
        {
            artifactKind = "a99_gold_authority_lineage",
            authorityId = "SRC-089",
            modelProviderVlmCalls = 0,
            previousSemanticAuthority = (object?)null,
            previousAuthorityNote = "none: no Gold, count or occurrence list of this source existed before this review",
            currentCanonicalAuthority = new
            {
                source = "original PDF",
                sourcePath = Src089SourceReviewTests.Pdf,
                sourceSha256 = CanonicalArtifactHash.OfBytes(TestRepository.Path(Src089SourceReviewTests.Pdf)),
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
                    decidedAt = "2026-09-27",
                    review = new { path = Review, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Review)) },
                    reviewItems = new { path = ReviewItems, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(ReviewItems)) },
                },
                engineProposalsRead = false,
                status = "CURRENT_OCCURRENCE_LEVEL_AUTHORITY",
            },
        });
    }

    [Fact]
    public void Create()
    {
        if (Environment.GetEnvironmentVariable("A99_SRC089_CREATE") != "1") return;
        var path = TestRepository.Path(GoldPath);
        Assert.False(File.Exists(path)); // one-shot: this source had no Gold
        var claims = BuildClaims();
        Assert.Equal(ApprovedTotal, claims.Count);
        var pdf = Src089SourceReviewTests.Pdf;
        var gold = new JsonObject
        {
            ["schemaVersion"] = "a99-gold-authored-v1",
            ["authorityId"] = "SRC-089",
            ["source"] = new JsonObject
            {
                ["fileName"] = Path.GetFileName(pdf),
                ["mediaType"] = "PDF",
                ["sourcePath"] = pdf,
                ["sourceSha256"] = CanonicalArtifactHash.OfBytes(TestRepository.Path(pdf)),
                ["sourceLineageStatus"] = "VERIFIED",
            },
            ["approval"] = new JsonObject
            {
                ["authority"] = "USER",
                ["userFinalApproval"] = true,
                ["approvalBasis"] = "USER_ADJUDICATED_STRUCTURAL_AUDIT",
                ["approvedAt"] = "2026-09-27",
                ["truthDefinition"] = "ALL_TRUE_HEADING_OCCURRENCES",
                ["semanticTruthReusedFrom"] = new JsonArray(Review),
                ["reReviewedDuringConsolidation"] = false,
            },
            ["semanticHeadingTotal"] = ApprovedTotal,
            ["declaredCapabilities"] = new JsonObject { ["visualBindingEvaluable"] = false, ["hierarchyEvaluable"] = false },
            ["occurrence"] = new JsonObject
            {
                ["coordinateSystem"] = "STRUCTURED_SOURCE_PARTS",
                ["claims"] = new JsonArray(claims.Select(c => (JsonNode)c).ToArray()),
            },
            ["occurrenceUnavailableReason"] = null,
            ["provenance"] = new JsonArray(new[]
            {
                (Review, "SEMANTIC_AUTHORITY"), (ReviewItems, "SOURCE_REVIEW_ITEMS"), (Lineage, "AUTHORITY_LINEAGE"),
                (Policy, "HEADING_POLICY"), (Addendum, "HEADING_POLICY_ADDENDUM"), (Ontology, "OCCURRENCE_ONTOLOGY"), (Principles, "CLASSIFICATION_PRINCIPLES"),
            }.Select(x => (JsonNode)new JsonObject
            {
                ["path"] = x.Item1,
                ["sha256"] = CanonicalArtifactHash.OfTextFile(TestRepository.Path(x.Item1)),
                ["role"] = x.Item2,
            }).ToArray()),
        };
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(gold.ToJsonString(FreezeArtifact.Json).ReplaceLineEndings("\n")));
    }

    [Fact]
    public void The_gold_is_the_decided_review_and_every_claim_is_exact()
    {
        if (!File.Exists(TestRepository.Path(GoldPath))) return;
        using var gold = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(GoldPath)));
        var root = gold.RootElement;
        var occurrence = root.GetProperty("occurrence");
        if (occurrence.ValueKind == JsonValueKind.Null) return; // not materialized yet

        var source = root.GetProperty("source");
        Assert.Equal(Src089SourceReviewTests.Pdf, source.GetProperty("sourcePath").GetString());
        Assert.Equal(CanonicalArtifactHash.OfBytes(TestRepository.Path(Src089SourceReviewTests.Pdf)), source.GetProperty("sourceSha256").GetString());
        Assert.Equal(ApprovedTotal, root.GetProperty("semanticHeadingTotal").GetInt32());
        Assert.False(root.GetProperty("declaredCapabilities").GetProperty("hierarchyEvaluable").GetBoolean());

        var expected = BuildClaims().Select(c => c.ToJsonString()).ToArray();
        var actual = occurrence.GetProperty("claims").EnumerateArray().Select(c => JsonNode.Parse(c.GetRawText())!.ToJsonString()).ToArray();
        Assert.Equal(expected, actual);

        var atoms = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Src089SourceReviewTests.Pdf)).Atoms
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
                if (named[i].GetProperty("selectionMode").GetString() == "WHOLE_ALIAS")
                    Assert.Equal((0, text.Length), (start, end));
                else
                {
                    Assert.Equal("VERBATIM_TEXT", named[i].GetProperty("selectionMode").GetString());
                    Assert.Equal(named[i].GetProperty("verbatimText").GetString(), text[start..end]);
                }
                var spans = taken.TryGetValue(alias, out var list) ? list : taken[alias] = [];
                Assert.DoesNotContain(spans, s => start < s.End && s.Start < end);
                spans.Add((start, end));
            }
        }
    }

    [Fact]
    public void The_user_decisions_are_in_the_gold_as_decided()
    {
        if (!File.Exists(TestRepository.Path(GoldPath))) return;
        using var gold = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(GoldPath)));
        var claims = gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
            .Select(c => c.GetProperty("sourceParts").EnumerateArray().Select(p => p.GetProperty("sourceAlias").GetString()!).ToArray())
            .ToArray();
        var inAnyClaim = claims.SelectMany(c => c).ToHashSet(StringComparer.Ordinal);
        void Claim(params string[] aliases) => Assert.Single(claims, c => c.SequenceEqual(aliases));

        // S089_Q1 (user): one three-line title, the footnote mark '(*)' excluded.
        var title = Assert.Single(gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray(),
            c => c.GetProperty("pattern").GetString() == "S089_Q1_DECREE_TITLE_BLOCK");
        Assert.Equal(["L0006:S0", "L0007:S0", "L0008:S0"], title.GetProperty("sourceParts").EnumerateArray().Select(p => p.GetProperty("sourceAlias").GetString()!));
        Assert.Equal("THE PUBLICATION LAW", title.GetProperty("sourceParts")[2].GetProperty("verbatimText").GetString());
        // S089_Q2 (user): a chapter label and its title line are one claim of two parts.
        Claim("L0014:S0", "L0015:S0");
        Claim("L0535:S0", "L0536:S0");
        // S089_Q3 (user, TRUE x4): the colon-ended clause labels.
        foreach (var clause in new[] { "L0109:S0", "L0117:S0", "L0246:S0", "L0249:S0" })
            Claim(clause);
        // An article title wrapped onto lower-case lines is one claim; a complete one-line title stays alone.
        Claim("L0106:S0", "L0107:S0", "L0108:S0");
        Claim("L0350:S0");
        // The number/date line, the signature block and the translation notice are in no claim.
        foreach (var other in new[] { "L0005:S0", "L0576:S0", "L0577:S0", "L0578:S0", "L0000:S0", "L0001:S0" })
            Assert.DoesNotContain(other, inAnyClaim);
    }

    /// <summary>The decided headings as authored claims, in source order, each bound by the production binder.</summary>
    private static List<JsonObject> BuildClaims()
    {
        var atoms = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Src089SourceReviewTests.Pdf)).Atoms;
        var byAlias = atoms.ToDictionary(a => a.Alias, StringComparer.Ordinal);
        var items = Src089SourceReviewTests.Items();
        Assert.DoesNotContain(items, i => i.Verdict == "AMBIGUOUS");

        var bindings = items.Select(i => (Item: i, Binding: Src089SourceReviewTests.Bind(atoms, i))).ToArray();
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
