using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// DOC0133_CANONICAL_OCCURRENCE_GOLD_V1: the 112 headings the user approved on 2026-09-25, written into
/// the authored Gold as structured source parts of the PDF's atom universe - each claim its exact atoms,
/// the binder's identity, source order and its OCCURRENCE_SEMANTIC_AXES_V2 description.
/// <para>
/// Membership is the structural audit (<see cref="Doc0133StructuralAuditTests"/>) with the user's
/// occurrence-by-occurrence decisions on its seven patterns: the cover's title lines are one claim and
/// its issuer, date and audit status are not; the statements' part title on page 31 is a heading and the
/// issuer and date beside it are not; the MD&amp;A title on its contents page is a heading; captions are
/// not, whatever their size. No model output enters it: the frozen qwen3.7-flash run is not in its
/// provenance. Write once with A99_DOC0133_MATERIALIZE=1; the check runs every time.
/// </para>
/// </summary>
public sealed class Doc0133CanonicalOccurrenceGoldTests
{
    private const string GoldPath = "eval/a99-closed-loop/gold/DOC-0133.gold.json";
    private const string Audit = "eval/a99-closed-loop/policy-audit/DOC-0133.structural-audit.v1.json";
    private const string Policy = "eval/a99-closed-loop/policy/financial-procurement-heading-policy.v1.json";
    private const string Addendum = "eval/a99-closed-loop/policy/financial-occurrence-distinctions.v1.json";
    private const string Ontology = "eval/a99-closed-loop/policy/occurrence-semantic-axes.v2.json";
    private const string Principles = "eval/a99-closed-loop/policy/occurrence-classification-principles.v1.json";
    private const int ApprovedTotal = 112;

    [Fact]
    public void Materialize()
    {
        if (Environment.GetEnvironmentVariable("A99_DOC0133_MATERIALIZE") != "1") return;

        var path = TestRepository.Path(GoldPath);
        var before = File.ReadAllText(path);
        var gold = JsonNode.Parse(before)!;
        Assert.Null(gold["occurrence"]); // one-shot
        Assert.Equal(123, gold["semanticHeadingTotal"]!.GetValue<int>());

        var claims = BuildClaims();
        Assert.Equal(ApprovedTotal, claims.Count);

        gold["semanticHeadingTotal"] = ApprovedTotal;
        gold["approval"]!["approvalBasis"] = "USER_ADJUDICATED_STRUCTURAL_AUDIT";
        gold["approval"]!["approvedAt"] = "2026-09-25";
        gold["approval"]!["semanticTruthReusedFrom"] = new JsonArray(Audit);
        gold["occurrence"] = new JsonObject
        {
            ["coordinateSystem"] = "STRUCTURED_SOURCE_PARTS",
            ["claims"] = new JsonArray(claims.Select(c => (JsonNode)c).ToArray()),
        };
        gold["occurrenceUnavailableReason"] = null;
        var provenance = gold["provenance"]!.AsArray();
        provenance.Add(new JsonObject
        {
            ["path"] = "gold-correction:DOC-0133:source-only-123-to-occurrence-112:2026-09-25",
            ["sha256"] = CanonicalArtifactHash.OfText(before),
            ["role"] = "GOLD_CORRECTION_PREDECESSOR",
        });
        foreach (var (item, role) in new[]
                 {
                     (Audit, "STRUCTURAL_AUDIT"), (Policy, "HEADING_POLICY"), (Addendum, "HEADING_POLICY_ADDENDUM"),
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
    public void The_gold_is_the_approved_audit_list_and_every_claim_binds()
    {
        using var gold = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(GoldPath)));
        var occurrence = gold.RootElement.GetProperty("occurrence");
        if (occurrence.ValueKind == JsonValueKind.Null) return; // not materialized yet

        Assert.Equal(ApprovedTotal, gold.RootElement.GetProperty("semanticHeadingTotal").GetInt32());
        var expected = BuildClaims().Select(c => c.ToJsonString()).ToArray();
        var actual = occurrence.GetProperty("claims").EnumerateArray().Select(c => JsonNode.Parse(c.GetRawText())!.ToJsonString()).ToArray();
        Assert.Equal(expected, actual);
    }

    /// <summary>The approved headings as authored claims, in source order, each bound by the production binder.</summary>
    private static List<JsonObject> BuildClaims()
    {
        var atoms = Doc0133StructuralAuditTests.ReadAtoms();
        var sourceAtoms = atoms.Select(a => a.Source).ToArray();
        var ordinal = atoms.Select((a, i) => (a.Alias, i)).ToDictionary(x => x.Alias, x => x.i, StringComparer.Ordinal);
        var candidates = Doc0133StructuralAuditTests.Candidates(atoms);
        var repeats = Doc0133StructuralAuditTests.RepeatStatuses(candidates);
        var headings = candidates.Where(c => c.Verdict == "HEADING")
            .OrderBy(c => ordinal[c.Aliases[0]]).ToArray();

        var claims = new List<JsonObject>();
        var last = -1;
        foreach (var c in headings)
        {
            Assert.True(ordinal[c.Aliases[0]] > last, "claims out of source order");
            last = ordinal[c.Aliases[^1]];
            var parts = c.Aliases.Select(a => new SemanticSourcePart(a, "WHOLE_ALIAS")).ToArray();
            var binding = SemanticSourcePartBinder.Bind(sourceAtoms, new SemanticSourcePartsProposal(parts));
            Assert.True(binding.IsBound, $"{c.Aliases[0]}: {binding.Reason}");
            claims.Add(new JsonObject
            {
                ["approvedWording"] = c.Text,
                ["sourceParts"] = new JsonArray(parts.Select(p => (JsonNode)new JsonObject
                {
                    ["sourceAlias"] = p.SourceAlias,
                    ["selectionMode"] = p.SelectionMode,
                    ["verbatimText"] = null,
                    ["occurrence"] = null,
                    ["leftExactContext"] = null,
                    ["rightExactContext"] = null,
                }).ToArray()),
                ["semanticRole"] = null,
                ["identity"] = binding.Identity,
                ["projectedText"] = string.Join(" ", binding.Parts.Select(p => p.Text)),
                ["semanticAxes"] = new JsonObject
                {
                    ["semanticFunctions"] = new JsonArray(c.SemanticFunctions.Select(f => (JsonNode)f).ToArray()),
                    ["primaryFunction"] = c.PrimaryFunction,
                    ["scope"] = c.Scope,
                    ["occurrenceRoles"] = new JsonArray(c.OccurrenceRoles.Select(r => (JsonNode)r).ToArray()),
                    ["titleRelation"] = c.TitleRelation,
                    ["titlePartAliases"] = new JsonArray(c.TitleRelation == "TITLE" ? c.Aliases.Skip(1).Select(a => (JsonNode)a).ToArray() : []),
                    ["repeatStatus"] = repeats[c],
                    ["informationType"] = c.InformationType,
                },
                ["pattern"] = c.Pattern,
                ["evidence"] = c.Reason,
            });
        }
        return claims;
    }
}
