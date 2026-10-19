using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.DocumentProcessing.Source.Scan;
using DocxHeaderExtractor.HeadingAgent;
using DocxHeaderExtractor.HeadingAgent.Scan;
using DocxHeaderExtractor.V6Qualification;
using static DocxHeaderExtractor.HeadingAgent.AgentProtocol;

namespace DocxHeaderExtractor.Tests;

public sealed class SourceComparativeCanariesTests
{
    internal static ScanAgentSource Source()
    {
        var pages = new List<OcrPage>();
        foreach (var page in new[] { 8, 11, 26 })
        {
            var count = page == 8 ? 27 : page == 11 ? 12 : 12;
            var lines = Enumerable.Range(1, count).Select(n =>
            {
                var text = (page, n) switch
                {
                    (8, 13) => "Chương II", (8, 14) => "CHẾ ĐỘ CHỨNG THỰC", (8, 15) => "CHỮ KÝ",
                    (8, 16) => "1. CẤP BẢN SAO", (8, 17) => "TỪ SỔ GỐC", (8, 18) => "Điều 16. Cá nhân, tổ chức có quyền yêu cầu",
                    (8, 24) => "Điều 17. Thẩm quyền cấp bản sao",
                    (11, 1) => "3. CHỨNG THỰC CHỮ KÝ", (11, 2) => "", (11, 3) => "Điều 23. Trách nhiệm của người yêu cầu",
                    (11, 4) => "chứng thực chữ ký", (26, 3) => "4. Lời chứng chứng thực hợp đồng, giao dịch",
                    (26, 4) => "a) Lời chứng chứng thực hợp đồng, giao dịch", _ => $"context {page}-{n}"
                };
                return new OcrLine($"p{page}-line-{n:000}", text, new PixelBox(30, n * 25, 600, 20), 95, 95, false, []);
            }).ToArray();
            pages.Add(new(page, new string('a', 64), 900, 1200, new string('b', 64), lines));
        }
        var view = new OcrView(new("ocr-original", "none", 1, "fixture", "1", "vie", 1), EvidenceOrigin.OcrDerived, pages);
        var evidence = new ScanOcrEvidence(ScanOcrEvidence.CurrentVersion, SourceComparativeCanaries.ExpectedSourceSha256,
            "ScannedPdf", 1, new string('d', 64), [view], []);
        return new(evidence);
    }

    [Fact]
    public void Boundary_cohort_is_predeclared_and_preserves_both_alternatives_and_trajectory()
    {
        var source = Source();
        var cases = SourceComparativeCanaries.BoundaryCases;
        Assert.Equal(new[] { "P026-OSCILLATION", "V17-CONTROL", "V24-CONTROL" }, cases.Select(c => c.Id));
        foreach (var item in cases)
        {
            var packet = SourceComparativeCanaries.BoundaryPacket(source, item);
            Assert.Equal(new[] { "A", "B", "A" }, packet["trajectory"]!.Deserialize<string[]>());
            Assert.Equal(item.ContextAliases, packet["sourceEvidence"]!.AsArray().Select(x => x!["alias"]!.GetValue<string>()));
            Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(item.PartitionA), packet["alternatives"]!["A"]));
            Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(item.PartitionB), packet["alternatives"]!["B"]));
            Assert.Contains("qualificationOnly", packet.ToJsonString());
        }
    }

    [Theory]
    [InlineData("A", "P026-L003", "A")]
    [InlineData("B", "P026-L004", "B")]
    [InlineData("INDETERMINATE", null, "INDETERMINATE")]
    public void Boundary_judgment_is_fresh_source_comparison_and_only_returns_alias_observations(string decision, string? evidenceAlias, string expected)
    {
        var source = Source();
        var item = SourceComparativeCanaries.BoundaryCases[0];
        var privateCapture = "";
        AgentUnit.Model model = (unit, round, request) =>
        {
            Assert.Equal("SOURCE-BOUNDARY-P026-OSCILLATION", unit);
            Assert.Equal(1, round);
            var body = JsonNode.Parse(request.Body)!;
            var messages = body["messages"]!.AsArray();
            Assert.Equal(2, messages.Count);
            var packet = JsonNode.Parse(messages[1]! ["content"]!.Text())!;
            Assert.Equal(SourceComparativeCanaries.BoundaryProtocol, packet["protocol"]!.GetValue<string>());
            Assert.DoesNotContain("oldReason", request.Body);
            return new(JsonSerializer.Serialize(new { protocol = SourceComparativeCanaries.BoundaryProtocol,
                caseId = item.Id, decision, evidenceRefs = evidenceAlias is null ? Array.Empty<string>() : [evidenceAlias] }), [], "stop");
        };
        var result = SourceComparativeCanaries.JudgeBoundary(source, item, model, new(3, 0.5), default,
            (_, text) => privateCapture = text);
        Assert.Equal(expected, result.Decision);
        Assert.Equal(evidenceAlias is null ? [] : [evidenceAlias], result.EvidenceRefs);
        Assert.Null(result.ProtocolError);
        Assert.Contains("request", privateCapture);
        Assert.DoesNotContain("oldReason", JsonSerializer.Serialize(result));
    }

    [Fact]
    public void Boundary_response_cannot_reference_alias_outside_frozen_source_scope()
    {
        var item = SourceComparativeCanaries.BoundaryCases[0];
        AgentUnit.Model model = (_, _, _) => new(JsonSerializer.Serialize(new { protocol = SourceComparativeCanaries.BoundaryProtocol,
            caseId = item.Id, decision = "A", evidenceRefs = new[] { "P001-L001" } }), [], "stop");
        var result = SourceComparativeCanaries.JudgeBoundary(Source(), item, model, new(1, 0.5), default);
        Assert.Equal("INDETERMINATE", result.Decision);
        Assert.Equal("RESPONSE_PROTOCOL_INVALID", result.ProtocolError);
        Assert.Empty(result.EvidenceRefs);
    }

    [Fact]
    public void Hierarchy_comparison_requires_both_targets_and_consistent_declared_note()
    {
        var packet = SourceComparativeCanaries.HierarchyPacket(Source());
        Assert.Equal(new[] { "P008-L018", "P008-L024" }, packet["graph"]!["children"]!.AsArray().Select(x => x!["alias"]!.GetValue<string>()));
        var correctLocalReview = """{"protocol":"QUALIFICATION_SOURCE_HIERARCHY_COMPARISON_V1","decisions":[{"childAlias":"P008-L018","parentAlias":"P008-L016","evidenceRefs":["P008-L016","P008-L018"]},{"childAlias":"P008-L024","parentAlias":"P008-L016","evidenceRefs":["P008-L016","P008-L024"]}],"consistencyNote":"SAME_PARENT"}""";
        var parsed = SourceComparativeCanaries.ParseHierarchy(correctLocalReview);
        Assert.Null(parsed.Error);
        Assert.Equal("SAME_PARENT", parsed.Consistency);
        Assert.All(parsed.Decisions, d => Assert.Equal("P008-L016", d.ParentAlias));
        var inconsistent = correctLocalReview.Replace("SAME_PARENT", "DIFFERENT_PARENT", StringComparison.Ordinal);
        Assert.Equal("RESPONSE_PROTOCOL_INVALID", SourceComparativeCanaries.ParseHierarchy(inconsistent).Error);
        var wrongParent = JsonNode.Parse(correctLocalReview)!;
        wrongParent["decisions"]![0]!["parentAlias"] = "P000-L999";
        Assert.Equal("RESPONSE_PROTOCOL_INVALID", SourceComparativeCanaries.ParseHierarchy(wrongParent.ToJsonString()).Error);
    }

    [Fact]
    public void Commands_require_explicit_mode_before_reading_manifest()
    {
        Assert.Equal(2, SourceComparativeCanaries.Run(["boundary-oscillation-canary", "missing", "unused"], hierarchy: false));
        Assert.Equal(2, SourceComparativeCanaries.Run(["hierarchy-comparative-canary", "missing", "unused"], hierarchy: true));
    }
}
