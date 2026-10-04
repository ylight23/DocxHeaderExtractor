using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>Provider-free sanitized comparison of frozen P6T-A and P6T-C role ledgers.</summary>
public sealed class V5P6TCMatchedComparisonTests
{
    private const string APath = "artifacts/v5-p6t-total-occurrence-role/p6ta-two-pack-canary/result.v1.json";
    private const string CPath = "artifacts/v5-p6t-total-occurrence-role/p6tc-correspondence-evidence/result.v1.json";
    private const string BPath = "artifacts/v5-p6t-total-occurrence-role/p6tb-anchor-role-audit/anchor-role-audit.v1.json";

    [Fact]
    public void P6TC_comparison_freezes_only_role_counts_and_transitions()
    {
        using var a = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(APath)));
        using var c = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(CPath)));
        using var b = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(BPath)));
        Assert.Equal(2, a.RootElement.GetProperty("providerCalls").GetInt32());
        Assert.Equal(2, c.RootElement.GetProperty("providerCalls").GetInt32());
        Assert.False(a.RootElement.GetProperty("goldRead").GetBoolean());
        Assert.False(c.RootElement.GetProperty("goldRead").GetBoolean());
        var rows = new List<object>();
        foreach (var documentId in new[] { "SRC-089", "SRC-095" })
        {
            var ar = a.RootElement.GetProperty("rows").EnumerateArray().Single(value => value.GetProperty("documentId").GetString() == documentId);
            var cr = c.RootElement.GetProperty("rows").EnumerateArray().Single(value => value.GetProperty("documentId").GetString() == documentId);
            var ad = Decisions(ar.GetProperty("rawResponse")); var cd = Decisions(cr.GetProperty("rawResponse"));
            Assert.Equal(ad.Keys.OrderBy(value => value), cd.Keys.OrderBy(value => value));
            rows.Add(new { documentId, p6ta = Counts(ad), p6tc = Counts(cd), roleChanged = ad.Count(value => cd[value.Key] != value.Value), total = ad.Count });
        }
        var toc = b.RootElement.GetProperty("src095").GetProperty("rows").EnumerateArray().Select(value => value.GetProperty("occurrence").GetString()!).ToArray();
        var a095 = Decisions(a.RootElement.GetProperty("rows").EnumerateArray().Single(value => value.GetProperty("documentId").GetString() == "SRC-095").GetProperty("rawResponse"));
        var c095 = Decisions(c.RootElement.GetProperty("rows").EnumerateArray().Single(value => value.GetProperty("documentId").GetString() == "SRC-095").GetProperty("rawResponse"));
        var tocTransitions = toc.Select(id => new { id, p6ta = a095[id], p6tc = c095[id] }).ToArray();
        Assert.Equal(53, tocTransitions.Length);
        Assert.All(tocTransitions, value => Assert.Equal("HEADING_START", value.p6ta));
        Assert.All(tocTransitions, value => Assert.Equal("HEADING_START", value.p6tc));
        FreezeArtifact.AssertJson("artifacts/v5-p6t-total-occurrence-role/p6tc-correspondence-evidence", "matched-role-comparison.v1.json", new
        {
            schemaVersion = "v5-p6tc-matched-role-comparison-v1", providerCalls = 0, sourceCaptures = new { p6ta = APath, p6tc = CPath }, rawResponsesCopied = false,
            matchedVariable = "READ_ONLY_CORRESPONDENCE_EVIDENCE", roleOntology = new[] { "HEADING_START", "REPRESENTATION_START", "OTHER" }, rows,
            src095ReviewedToc = new { targets = 53, p6taHeadingStart = 53, p6tcHeadingStart = 53, p6tcRepresentationStart = 0, p6tcOther = 0, roleTransitions = 0 },
            conclusion = "CORRESPONDENCE_EVIDENCE_DID_NOT_CHANGE_53_TOC_ROLE_DECISIONS",
        });
    }

    private static Dictionary<string, string> Decisions(JsonElement raw)
    {
        using var json = JsonDocument.Parse(raw.GetString()!);
        return json.RootElement.GetProperty("decisions").EnumerateArray().ToDictionary(value => value.GetProperty("occurrence").GetString()!, value => value.GetProperty("role").GetString()!, StringComparer.Ordinal);
    }
    private static object Counts(IReadOnlyDictionary<string, string> values) => values.GroupBy(value => value.Value).OrderBy(value => value.Key).ToDictionary(value => value.Key, value => value.Count());
}
