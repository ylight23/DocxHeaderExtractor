using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// DOC0123_CANONICAL_OCCURRENCE_GOLD_V1: the 359 headings the user approved on 2026-09-24, written into
/// the authored Gold as source-alias occurrences - every claim its exact source parts, UTF-16 spans,
/// source order and the OCCURRENCE_SEMANTIC_AXES_V2 description.
/// <para>
/// The membership comes from the structural audit (<see cref="Doc0123StructuralAuditTests"/>) and the
/// user's adjudication of its 23 open items; no model output enters it. The frozen qwen3.7-flash run
/// stays where it is, comparison evidence for a later scored run, and is not in this Gold's
/// provenance. Write once with A99_DOC0123_MATERIALIZE=1 (refuses an already itemised Gold); the
/// check below runs every time and keeps the Gold and the audit in step.
/// </para>
/// </summary>
public sealed class Doc0123CanonicalOccurrenceGoldTests
{
    private const string GoldPath = "eval/a99-closed-loop/gold/DOC-0123.gold.json";
    private const string Audit = "eval/a99-closed-loop/policy-audit/DOC-0123.structural-audit.v1.json";
    private const string Policy = "eval/a99-closed-loop/policy/financial-procurement-heading-policy.v1.json";
    private const string Ontology = "eval/a99-closed-loop/policy/occurrence-semantic-axes.v2.json";
    private const int ApprovedTotal = 359;

    [Fact]
    public void Materialize()
    {
        if (Environment.GetEnvironmentVariable("A99_DOC0123_MATERIALIZE") != "1") return;

        var path = TestRepository.Path(GoldPath);
        var before = File.ReadAllText(path);
        var gold = JsonNode.Parse(before)!;
        Assert.Null(gold["occurrence"]); // one-shot
        Assert.Equal(362, gold["semanticHeadingTotal"]!.GetValue<int>());

        var claims = BuildClaims();
        Assert.Equal(ApprovedTotal, claims.Count);

        gold["semanticHeadingTotal"] = ApprovedTotal;
        gold["approval"]!["approvalBasis"] = "USER_ADJUDICATED_STRUCTURAL_AUDIT";
        gold["approval"]!["approvedAt"] = "2026-09-24";
        gold["approval"]!["semanticTruthReusedFrom"] = new JsonArray(Audit);
        gold["occurrence"] = new JsonObject
        {
            ["coordinateSystem"] = "SOURCE_ALIAS",
            ["claims"] = new JsonArray(claims.Select(c => (JsonNode)c).ToArray()),
        };
        gold["occurrenceUnavailableReason"] = null;
        var provenance = gold["provenance"]!.AsArray();
        provenance.Add(new JsonObject
        {
            ["path"] = "gold-correction:DOC-0123:source-only-362-to-occurrence-359:2026-09-24",
            ["sha256"] = CanonicalArtifactHash.OfText(before),
            ["role"] = "GOLD_CORRECTION_PREDECESSOR",
        });
        foreach (var (item, role) in new[] { (Audit, "STRUCTURAL_AUDIT"), (Policy, "HEADING_POLICY"), (Ontology, "OCCURRENCE_ONTOLOGY") })
            provenance.Add(new JsonObject
            {
                ["path"] = item,
                ["sha256"] = CanonicalArtifactHash.OfTextFile(TestRepository.Path(item)),
                ["role"] = role,
            });
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(gold.ToJsonString(FreezeArtifact.Json).ReplaceLineEndings("\n")));
    }

    [Fact]
    public void The_gold_is_the_approved_audit_list_and_every_part_slices_to_its_text()
    {
        using var gold = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(GoldPath)));
        var occurrence = gold.RootElement.GetProperty("occurrence");
        if (occurrence.ValueKind == JsonValueKind.Null) return; // not materialized yet

        Assert.Equal(ApprovedTotal, gold.RootElement.GetProperty("semanticHeadingTotal").GetInt32());
        var expected = BuildClaims().Select(c => c.ToJsonString()).ToArray();
        var actual = occurrence.GetProperty("claims").EnumerateArray().Select(c => JsonNode.Parse(c.GetRawText())!.ToJsonString()).ToArray();
        Assert.Equal(expected, actual);

        var build = Doc0123StructuralAuditTests.BuildItems();
        var byAlias = build.Aliases.ToDictionary(a => a.Alias, StringComparer.Ordinal);
        var lastOrdinal = -1;
        foreach (var claim in occurrence.GetProperty("claims").EnumerateArray())
        {
            var parts = claim.GetProperty("parts").EnumerateArray().ToArray();
            var sliced = parts.Select(part =>
            {
                var alias = byAlias[part.GetProperty("sourceAlias").GetString()!];
                Assert.Equal(alias.SourceId, part.GetProperty("sourceId").GetString());
                var span = part.GetProperty("utf16Span");
                return alias.Text[span.GetProperty("start").GetInt32()..span.GetProperty("end").GetInt32()];
            }).ToArray();
            Assert.Equal(claim.GetProperty("exactText").GetString(), string.Join(" ", sliced));
            var ordinal = build.Index[parts[0].GetProperty("sourceAlias").GetString()!];
            Assert.True(ordinal > lastOrdinal, "claims out of source order");
            lastOrdinal = build.Index[parts[^1].GetProperty("sourceAlias").GetString()!];
        }
    }

    /// <summary>The approved headings as authored claims, in source order.</summary>
    private static List<JsonObject> BuildClaims()
    {
        var build = Doc0123StructuralAuditTests.BuildItems();
        var described = Doc0123StructuralAuditTests.Describe(build.Items, build.Index);
        var keep = build.Items.Where(i => i.Verdict == Doc0123StructuralAuditTests.Verdict.Keep)
            .OrderBy(i => build.Index[i.Aliases[0]]).ToArray();

        return keep.Select((item, ordinal) =>
        {
            var aliases = item.Aliases.Select(a => build.Aliases[build.Index[a]]).ToArray();
            var sourceText = string.Join(" ", aliases.Select(a => a.Text.Trim()));
            // A bold lead-in before an editorial instruction ("Other Proposers [INSTRUCTIONS: ...]")
            // is selected as the lead-in text; every other heading is its whole paragraph(s).
            var prefixOnly = Compact(sourceText) != Compact(item.Text);
            if (prefixOnly) Assert.Single(aliases);

            var parts = new JsonArray();
            foreach (var alias in aliases)
            {
                var text = prefixOnly ? item.Text : alias.Text.Trim();
                var at = alias.Text.IndexOf(text, StringComparison.Ordinal);
                Assert.True(at >= 0, $"'{text}' is not in alias {alias.Alias}");
                parts.Add(new JsonObject
                {
                    ["sourceAlias"] = alias.Alias,
                    ["sourceId"] = alias.SourceId,
                    ["utf16Span"] = new JsonObject { ["start"] = at, ["end"] = at + text.Length },
                });
            }
            var (axes, titleParts) = described[item];
            var first = parts[0]!;
            return new JsonObject
            {
                ["headingOrdinal"] = ordinal,
                ["sourceAlias"] = first["sourceAlias"]!.GetValue<string>(),
                ["sourceId"] = first["sourceId"]!.GetValue<string>(),
                ["exactText"] = prefixOnly ? item.Text : sourceText,
                ["selectionMode"] = prefixOnly ? CanonicalSemanticSelectionMode.VerbatimText : CanonicalSemanticSelectionMode.WholeAlias,
                ["verbatimText"] = prefixOnly ? item.Text : null,
                ["semanticRole"] = null,
                ["utf16Span"] = aliases.Length == 1 ? first["utf16Span"]!.DeepClone() : null,
                ["parts"] = parts,
                ["semanticAxes"] = new JsonObject
                {
                    ["semanticFunctions"] = new JsonArray(axes.SemanticFunctions.Select(f => (JsonNode)f).ToArray()),
                    ["primaryFunction"] = axes.PrimaryFunction,
                    ["scope"] = axes.Scope,
                    ["occurrenceRoles"] = new JsonArray(axes.OccurrenceRoles.Select(r => (JsonNode)r).ToArray()),
                    ["titleRelation"] = axes.TitleRelation,
                    ["titlePartAliases"] = new JsonArray(titleParts.Select(a => (JsonNode)a).ToArray()),
                    ["repeatStatus"] = axes.RepeatStatus,
                    ["informationType"] = axes.InformationType,
                },
                ["section"] = item.Section,
                ["decision"] = $"{item.Rule}: {item.Reason}",
            };
        }).ToList();
    }

    /// <summary>Text with all whitespace removed: tab/field spacing in the catalog is not a different heading.</summary>
    private static string Compact(string value) => Regex.Replace(value, @"\s+", "");
}
