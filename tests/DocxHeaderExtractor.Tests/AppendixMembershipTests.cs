using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Contracts.Routing;
using DocxHeaderExtractor.DocumentProcessing.Routing;
using DocxHeaderExtractor.Sources.Routing;
using DocxHeaderExtractor.Contracts.Scan;
using DocxHeaderExtractor.Sources.Scan;
using DocxHeaderExtractor.HeadingAgent;
using DocxHeaderExtractor.HeadingAgent.Scan;
using static DocxHeaderExtractor.HeadingAgent.AgentProtocol;

namespace DocxHeaderExtractor.Tests;

public sealed class AppendixMembershipTests
{
    internal static string A(int n) => $"P023-L{n:000}";
    internal static readonly int[] HeadingLines = [1, 2, 4, 6, 8, 9, 11, 12, 14];

    // Synthetic alias distribution, based on the inspected page-23 image and the user's appendix
    // skeleton. This is a protocol regression fixture, not a Gold or a claim about a live model run.
    internal static (ScanAgentSource Source, HeadingSelection Selection, List<string[]> ReviewTargets) RunFixture(string mode)
    {
        string[] texts = ["Phụ lục", "I. LỜI CHỨNG", "Mẫu lời chứng được đánh máy hoặc khắc trên mẫu dấu, bao gồm:",
            "1. Lời chứng chứng thực bản sao từ bản chính", "Nội dung biểu mẫu", "2. Lời chứng chứng thực chữ ký",
            "Nội dung biểu mẫu", "II. MẪU SỔ CHỨNG THỰC", "1. Bìa sổ", "Nội dung biểu mẫu", "2. Nội dung sổ",
            "a) Đề mục cấp thấp đang ngoài cây", "Nội dung", "b) Đề mục cấp thấp khác đang ngoài cây"];
        var lines = texts.Select((text, i) => new OcrLine($"P023-ocr-original-L{i + 1:000}", text,
            new PixelBox(100, 100 + i * 50, 1000, 40), 95, 95, false, [])).ToArray();
        var evidence = new ScanOcrEvidence(ScanOcrEvidence.CurrentVersion, new string('a', 64), "ScannedPdf", 1, new string('b', 64),
            [new OcrView(new OcrViewSpec("ocr-original", "none", 1, "test", "1", "vie", 1), EvidenceOrigin.OcrDerived,
                [new OcrPage(23, new string('c', 64), 1600, 2200, new string('d', 64), lines)])], []);
        var source = new ScanAgentSource(evidence);
        var reviewTargets = new List<string[]>();
        AgentUnit.Model model = (unit, _, request) =>
        {
            var content = JsonNode.Parse(request.Body)!["messages"]![1]!["content"]!;
            var packet = JsonNode.Parse(content is JsonArray ? content[0]!["text"]!.GetValue<string>() : content.GetValue<string>())!;
            var stage = packet["stage"]!.GetValue<string>();
            if (stage == "GROUP_VERIFICATION")
            {
                var verdicts = new JsonArray();
                foreach (var c in packet["candidates"]!.AsArray())
                {
                    var aliases = c!["proposedGroups"]!.AsArray().SelectMany(g => g!.AsArray()).Select(a => a!.GetValue<string>()).ToArray();
                    var mixed = aliases.Contains(A(3));
                    var boundaryOnly = mode == "boundary-after-repair" && unit == "VERIFY-001";
                    var decision = !mixed ? "ACCEPT" : boundaryOnly ? "REGROUP" : mode == "unknown" ? "UNRESOLVED" : "MEMBERSHIP_DISPUTE";
                    if (unit.Contains("SURVIVORS", StringComparison.Ordinal))
                    {
                        Assert.DoesNotContain(A(3), aliases);
                        Assert.Contains(packet["context"]!.AsArray(), l => l!["alias"]!.GetValue<string>() == A(3));
                    }
                    verdicts.Add(new JsonObject
                    {
                        ["run"] = c["run"]!.DeepClone(), ["decision"] = decision,
                        ["reason"] = "L003 is an introductory sentence; membership and boundary claims are structured separately.",
                        ["evidenceRefs"] = JsonSerializer.SerializeToNode(aliases.Take(1)),
                        ["membership"] = new JsonArray(aliases.Select(alias => (JsonNode)new JsonObject
                        {
                            ["alias"] = alias,
                            ["status"] = alias == A(3) && !boundaryOnly ? mode == "unknown" ? "UNRESOLVED" : "NOT_HEADING" : "HEADING",
                            ["evidenceRefs"] = JsonSerializer.SerializeToNode(new[] { alias })
                        }).ToArray())
                    });
                }
                return new ModelTurn(GroundedVerifierFixture.Reply(new JsonObject { ["protocolVersion"] = AgentGroupVerification.Version, ["verdicts"] = verdicts }.ToJsonString(), packet), [], "stop");
            }
            if (stage == "GROUP")
            {
                var runs = packet["runs"]!.AsArray().Select(r =>
                {
                    var aliases = r!["lines"]!.AsArray().Select(l => l!["alias"]!.GetValue<string>()).ToArray();
                    return new { run = r["run"]!.GetValue<string>(), headings = unit == "GROUP-001" && aliases.Contains(A(3))
                        ? new[] { new[] { A(1) }, new[] { A(2), A(3) }, new[] { A(4) } } : aliases.Select(a => new[] { a }).ToArray() };
                });
                return new ModelTurn(JsonSerializer.Serialize(new { protocolVersion = AgentGrouping.GroupVersion, runs }), [], "stop");
            }
            var review = packet["selectionReview"];
            if (review is not null)
            {
                reviewTargets.Add(review["disputedAliases"]!.AsArray().Select(a => a!.GetValue<string>()).ToArray());
                if (mode == "review-fails") throw new ReplyFailure("TEST_REVIEW_FAILURE");
            }
            var selected = HeadingLines.Select(A).ToHashSet();
            if (review is null || mode == "reselected") selected.Add(A(3));
            return new ModelTurn(JsonSerializer.Serialize(new
            {
                protocolVersion = SelectScanVersion, status = "COMPLETE",
                decisions = packet["targets"]!.AsArray().Select(t =>
                {
                    var alias = t!["alias"]!.GetValue<string>();
                    return new { alias, decision = selected.Contains(alias) ? "SELECT" : "NOT_SELECT", evidenceRefs = new[] { alias }, reason = "Scripted membership decision." };
                })
            }), [], "stop");
        };
        var selection = HeadingAgentSelector.Select(source, model, new AgentBudget(100, 100), 1, CancellationToken.None, 0, () => 0);
        return (source, selection, reviewTargets);
    }

    [Theory]
    [InlineData("reject")]
    [InlineData("review-fails")]
    [InlineData("reselected")]
    [InlineData("unknown")]
    [InlineData("boundary-after-repair")]
    public void Mixed_run_is_bound_from_grouping_without_reopening_selection_for_review(string mode)
    {
        var (_, result, targets) = RunFixture(mode);
        Assert.Equal(HeadingLines.Append(3).Select(A).Order(), result.SelectedAliases.Order());
        Assert.Equal(result.SelectedAliases.Order(), result.Groups!.SelectMany(g => g).Order());
        Assert.Empty(targets);
        Assert.Equal(0, result.Grouping!.VerificationChecks);
        foreach (var alias in HeadingLines.Select(A))
        {
            Assert.Equal("READY_FOR_BINDING", result.AliasStates![alias].BindingStatus);
        }
        var bad = result.AliasStates![A(3)];
        Assert.Equal("SELECT", bad.FinalSelection);
        Assert.Equal("READY_FOR_BINDING", bad.BindingStatus);
        Assert.Contains(result.ModelAnswers, a => a.Contains("pre-binding-alias-state", StringComparison.Ordinal));
        Assert.Empty(result.UnitFailures);
        Assert.Equal(1, result.UnitsComplete);
        Assert.Equal(0, result.Grouping!.FallbackRuns);
        Assert.Equal(0, result.Grouping.BoundaryDisputes);
    }
}
