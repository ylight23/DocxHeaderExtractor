using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Routing;
using DocxHeaderExtractor.DocumentProcessing.Source.Scan;
using DocxHeaderExtractor.HeadingAgent;
using DocxHeaderExtractor.HeadingAgent.Scan;
using static DocxHeaderExtractor.HeadingAgent.AgentProtocol;

namespace DocxHeaderExtractor.Tests;

public sealed class HeadingGroupVerificationTests
{
    static string A(int n) => $"P032-L{n:000}";
    // Source functions copied from the inspected stored page 32, not a heading Gold or a production phrase filter.
    static ScanAgentSource Source(string? entry = null, string? imageSha = null)
    {
        string[] texts = ["Chương II", "QUY ĐỊNH CHUNG", "Điều 3. Tên điều", "tiếp nối tên điều",
            "nội dung", "nội dung", "nội dung", "nội dung", "c) Sổ Chứng thực chữ ký người dịch",
            "CỘNG HÒA XÃ HỘI CHỦ NGHĨA VIỆT NAM", "Độc lập - Tự do - Hạnh phúc", "SỔ CHỨNG THỰC CHỮ KÝ NGƯỜI DỊCH",
            "d) Sổ Chứng thực hợp đồng, giao dịch", "nội dung"];
        var lines = texts.Select((t, i) => new OcrLine($"P032-ocr-original-L{i + 1:000}", t,
            new PixelBox(30, 20 + i * 30, 340, 20), 95, 95, false, [])).ToArray();
        var view = new OcrView(new OcrViewSpec("ocr-original", "none", 1, "test", "1", "vie", 1), EvidenceOrigin.OcrDerived,
            [new OcrPage(32, imageSha ?? new string('a', 64), 420, 500, new string('b', 64), lines)]);
        return new ScanAgentSource(new ScanOcrEvidence(ScanOcrEvidence.CurrentVersion, new string('c', 64), "ScannedPdf", 1,
            new string('d', 64), [view], []), evidenceEntry: entry);
    }

    static JsonObject Payload(PreparedRequest request)
    {
        var content = JsonNode.Parse(request.Body)!["messages"]![1]!["content"]!;
        return JsonNode.Parse(content is JsonArray ? content[0]!["text"]!.GetValue<string>() : content.GetValue<string>())!.AsObject();
    }

    static string Verdicts(JsonObject packet, Func<JsonNode, string> decide, bool invalidRefs = false) => JsonSerializer.Serialize(new
    {
        protocolVersion = AgentGroupVerification.Version,
        verdicts = packet["candidates"]!.AsArray().Select(c => new
        {
            run = c!["run"]!.GetValue<string>(), decision = decide(c), reason = "Independent scripted source-based judgement.",
            evidenceRefs = new[] { invalidRefs ? "UNKNOWN" : c["proposedGroups"]![0]![0]!.GetValue<string>() }
        })
    });

    [Theory]
    [InlineData("boundary")]
    [InlineData("membership")]
    [InlineData("repeated-boundary")]
    public void Accepted_but_wrong_form_group_is_checked_without_a_grouping_dispute(string mode)
    {
        var source = Source();
        var groupCalls = 0; var selectionReviews = 0; var verificationCalls = 0;
        AgentUnit.Model model = (unit, turn, request) =>
        {
            var packet = Payload(request);
            var stage = packet["stage"]!.GetValue<string>();
            if (stage == "GROUP_VERIFICATION")
            {
                verificationCalls++;
                Assert.True(packet["candidates"]![0]!["targetFacts"]!.AsArray().Count > 0);
                var firstPass = !unit.Contains("REVERIFY", StringComparison.Ordinal) && !unit.StartsWith("REVIEW-", StringComparison.Ordinal);
                return new ModelTurn(Verdicts(packet, c =>
                    c["proposedGroups"]!.ToJsonString().Contains(A(10), StringComparison.Ordinal) &&
                    (firstPass || mode == "repeated-boundary")
                        ? mode == "membership" ? "MEMBERSHIP_DISPUTE" : "REGROUP" : "ACCEPT"), [], "stop");
            }
            if (stage == "GROUP")
            {
                groupCalls++;
                var runs = packet["runs"]!.AsArray().Select(r =>
                {
                    var aliases = r!["lines"]!.AsArray().Select(l => l!["alias"]!.GetValue<string>()).ToArray();
                    var headings = aliases.Contains(A(1)) ? new[] { new[] { A(1), A(2) }, new[] { A(3), A(4) } }
                        : unit == "GROUP-001" ? new[] { new[] { A(9) }, new[] { A(10), A(11), A(12) }, new[] { A(13) } }
                        : aliases.Select(a => new[] { a }).ToArray();
                    return new { run = r["run"]!.GetValue<string>(), headings };
                });
                return new ModelTurn(JsonSerializer.Serialize(new { protocolVersion = AgentGrouping.GroupVersion, runs }), [], "stop");
            }
            var reviewing = packet["selectionReview"] is not null;
            if (reviewing) selectionReviews++;
            var decisions = packet["targets"]!.AsArray().Select(t =>
            {
                var alias = t!["alias"]!.GetValue<string>();
                var select = new[] { A(1), A(2), A(3), A(4), A(9), A(13) }.Contains(alias) || !reviewing && new[] { A(10), A(11), A(12) }.Contains(alias);
                return new { alias, decision = select ? "SELECT" : "NOT_SELECT", evidenceRefs = new[] { alias }, reason = "Scripted membership judgement." };
            });
            return new ModelTurn(JsonSerializer.Serialize(new { protocolVersion = SelectScanVersion, status = "COMPLETE", decisions }), [], "stop");
        };
        var result = HeadingAgentSelector.Select(source, model, new AgentBudget(100, 100), 1, CancellationToken.None, 0, () => 0);
        Assert.Contains(result.Groups!, g => g.SequenceEqual(new[] { A(1), A(2) }));
        Assert.Contains(result.Groups!, g => g.SequenceEqual(new[] { A(3), A(4) }));
        Assert.DoesNotContain(result.Groups!, g => g.Contains(A(10)) && g.Contains(A(12)));
        Assert.True(verificationCalls >= 2);
        Assert.Equal(0, result.Grouping!.FallbackRuns);
        Assert.Equal(mode == "membership" ? 1 : 0, selectionReviews);
        if (mode == "membership") Assert.DoesNotContain(A(10), result.SelectedAliases);
        if (mode == "repeated-boundary")
        {
            Assert.Equal(5, result.Grouping.UnresolvedAliases);
            Assert.Contains(result.UnitFailures, f => f.Contains("UNRESOLVED", StringComparison.Ordinal));
            Assert.Equal(0, result.UnitsComplete);
        }
        else Assert.Empty(result.UnitFailures);
        Assert.Equal(mode == "membership" ? 1 : 2, groupCalls);
    }

    [Theory]
    [InlineData("unknown-reference")]
    [InlineData("missing-verdict")]
    [InlineData("unresolved")]
    [InlineData("budget")]
    public void Unverifiable_groups_are_withheld_and_never_claimed_as_layout_fallback(string mode)
    {
        var source = Source();
        var input = new AgentGrouping.Result([new[] { A(10), A(11), A(12) }], [], [], 0);
        var budget = new AgentBudget(20, 100);
        if (mode == "budget") budget.Halt("TEST");
        var calls = 0;
        AgentUnit.Model model = (_, _, request) =>
        {
            calls++;
            var response = mode == "missing-verdict" ? "{\"protocolVersion\":\"" + AgentGroupVerification.Version + "\",\"verdicts\":[]}"
                : Verdicts(Payload(request), _ => mode == "unresolved" ? "UNRESOLVED" : "ACCEPT", mode == "unknown-reference");
            return new ModelTurn(response, [], "stop");
        };
        var result = AgentGroupVerification.Verify(source, input, new Dictionary<string, TextOffsetSpan>(), new Dictionary<string, string>(),
            model, budget, 1, CancellationToken.None, 0, Instruction);
        Assert.Empty(result.Groups);
        Assert.Equal(new[] { A(10), A(11), A(12) }, result.UnresolvedAliases);
        Assert.NotEmpty(result.Failures);
        Assert.Equal(0, result.Observation.FallbackRuns);
        Assert.InRange(calls, 0, AgentGroupVerification.MaxPosts);
    }

    [Fact]
    public void Verifier_can_request_a_hashed_scan_crop_and_read_only_context_before_deciding()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dhx-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "pages"));
        try
        {
            var png = TestPng.WithBlack(420, 500, 255, (40, 300, 80, 10));
            File.WriteAllBytes(Path.Combine(directory, "pages", "page-032.png"), png);
            var source = Source(directory, Convert.ToHexStringLower(SHA256.HashData(png)));
            var calls = 0;
            AgentUnit.Model model = (_, _, request) =>
            {
                calls++;
                if (calls == 1) return new ModelTurn(JsonSerializer.Serialize(new { protocolVersion = AgentGroupVerification.Version,
                    requests = new object[] { new { name = "get_scan_crop", arguments = new { aliases = new[] { A(9), A(10), A(11), A(12), A(13) } } },
                        new { name = "get_source_context", arguments = new { alias = A(10), before = 2, after = 4 } } } }), [], "stop");
                var messages = JsonNode.Parse(request.Body)!["messages"]!.AsArray();
                var parts = messages.Last()!["content"]!.AsArray();
                Assert.Contains(parts, p => p!["type"]!.GetValue<string>() == "image_url");
                var url = parts.Single(p => p!["type"]!.GetValue<string>() == "image_url")!["image_url"]!["url"]!.GetValue<string>();
                var raster = PngRaster.TryDecode(Convert.FromBase64String(url[(url.IndexOf(',') + 1)..]))!;
                Assert.Contains((byte)0, raster.Grey);
                Assert.Contains((byte)255, raster.Grey);
                Assert.Contains("cropSha256", parts[0]!["text"]!.GetValue<string>());
                return new ModelTurn(Verdicts(Payload(request), _ => "UNRESOLVED"), [], "stop");
            };
            var result = AgentGroupVerification.Verify(source, new AgentGrouping.Result([new[] { A(10), A(11), A(12) }], [], [], 0),
                new Dictionary<string, TextOffsetSpan>(), new Dictionary<string, string>(), model, new AgentBudget(10, 100), 1, CancellationToken.None, 0, Instruction);
            Assert.Equal(2, calls);
            Assert.Contains(result.Evidence, e => e.Contains("cropSha256", StringComparison.Ordinal));
            Assert.DoesNotContain(result.Evidence, e => e.Contains("data:image", StringComparison.Ordinal));
            Assert.Empty(result.Groups);
        }
        finally { Directory.Delete(directory, true); }
    }
}
