using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Source.Scan;
using DocxHeaderExtractor.HeadingAgent;
using DocxHeaderExtractor.HeadingAgent.Scan;
using DocxHeaderExtractor.V6Qualification;
using static DocxHeaderExtractor.HeadingAgent.AgentProtocol;

namespace DocxHeaderExtractor.Tests;

public sealed class BoundaryStaticCanaryTests
{
    static string A(int i) => $"P001-L{i:000}";
    const string Reason = "PRIVATE_FROZEN_SEED_REASON";
    internal static ScanAgentSource Source()
    {
        var lines = Enumerable.Range(1, 16).Select(i => new OcrLine($"P001-ocr-original-L{i:000}", "original " + i,
            new PixelBox(20, i * 30, 250, 20), 95, 95, false, [])).ToArray();
        var view = new OcrView(new("ocr-original", "none", 1, "test", "1", "vie", 1), EvidenceOrigin.OcrDerived,
            [new(1, new string('a', 64), 400, 600, new string('b', 64), lines)]);
        var transcription = new TranscriptionSet(TranscriptionSet.CurrentVersion, "fixture", "fixture", 2,
            [new(lines[0].Id, "prefix Heading tail", ScanTranscription.Accepted)], [], 0, 0);
        return new(new(ScanOcrEvidence.CurrentVersion, new string('c', 64), "ScannedPdf", 1, new string('d', 64), [view], []),
            transcriptions: transcription);
    }

    static string Verdict(JsonObject packet, string decision) => JsonSerializer.Serialize(new
    {
        protocolVersion = AgentGroupVerification.Version,
        verdicts = packet["candidates"]!.AsArray().Select(c => new
        {
            run = c!["run"]!.GetValue<string>(), decision, reason = Reason,
            evidenceRefs = new[] { A(1) }, membership = c["proposedGroups"]!.AsArray().SelectMany(g => g!.AsArray()).Select(a =>
                new { alias = a!.GetValue<string>(), status = "HEADING", evidenceRefs = new[] { a.GetValue<string>() } })
        })
    });

    [Theory]
    [InlineData(false, true, "ACCEPT")]
    [InlineData(false, true, "REGROUP")]
    [InlineData(false, false, "ACCEPT")]
    [InlineData(true, true, "ACCEPT")]
    public void Paired_canary_freezes_feedback_mirrors_selected_representation_and_only_verifies_changed_proposals(bool aChanged, bool bChanged, string independent)
    {
        JsonObject? seedPacket = null;
        var seedCalls = 0; var verifyCalls = 0; var armOrder = new List<string>();
        var feedbacks = new List<string>();
        var privateSnapshots = new Dictionary<string, string>();
        AgentUnit.Model model = (unit, _, request) =>
        {
            var packet = BoundaryStaticCanary.Packet(request);
            if (packet["stage"]!.GetValue<string>() == "GROUP_VERIFICATION")
            {
                if (unit.EndsWith("-C0", StringComparison.Ordinal)) { seedCalls++; seedPacket = packet; }
                else
                {
                    verifyCalls++;
                    Assert.DoesNotContain(Reason, request.Body); // Fresh verifier sees no seed verdict/repair reasoning.
                }
                return new(Verdict(packet, unit.EndsWith("-C0", StringComparison.Ordinal) ? "REGROUP" : independent), [], "stop");
            }
            var name = unit.Contains("-A-", StringComparison.Ordinal) ? "A" : "B";
            armOrder.Add(name);
            Assert.Null(JsonNode.Parse(request.Body)!["tools"]);
            var feedback = packet["verificationFeedback"]!.DeepClone().AsObject();
            Assert.Equal(Reason, feedback["verifier"]!["Reason"]!.GetValue<string>());
            if (name == "B")
            {
                var extra = feedback["verifierStaticEvidence"]!;
                Assert.Equal(seedPacket!["candidates"]![0]!["targetFacts"]!.ToJsonString(), extra["targetFacts"]!.ToJsonString());
                Assert.Equal(seedPacket["context"]!.ToJsonString(), extra["context"]!.ToJsonString());
                Assert.Equal(seedPacket["documentStyles"]!.ToJsonString(), extra["documentStyles"]!.ToJsonString());
                Assert.Equal("vlm-transcribed", extra["lines"]![0]!["readingViewId"]!.GetValue<string>());
                Assert.Equal("Heading", extra["lines"]![0]!["selectedText"]!.GetValue<string>());
                Assert.NotEqual(extra["lines"]![0]!["selectedText"]!.GetValue<string>(), packet["runs"]![0]!["lines"]![0]!["text"]!.GetValue<string>());
                Assert.Null(extra["evidenceTools"]);
                Assert.Null(extra["scanCropTool"]);
                feedback.Remove("verifierStaticEvidence");
            }
            else Assert.Null(feedback["verifierStaticEvidence"]);
            feedbacks.Add(feedback.ToJsonString());
            var changed = name == "A" ? aChanged : bChanged;
            return new(JsonSerializer.Serialize(new { protocolVersion = AgentGrouping.GroupVersion,
                runs = packet["runs"]!.AsArray().Select(r => new { run = r!["run"]!.GetValue<string>(),
                    headings = changed ? new[] { new[] { A(1) }, new[] { A(2) } } : new[] { new[] { A(1), A(2) } } }) }), [], "stop");
        };
        var result = BoundaryStaticCanary.Run("case", Source(), [[A(1), A(2)]],
            new Dictionary<string, TextOffsetSpan> { [A(1)] = new(7, 14) }, new Dictionary<string, string> { [A(1)] = "vlm-transcribed" },
            model, new AgentBudget(50, 100), 2, CancellationToken.None, (n, s) => privateSnapshots[n] = s);
        Assert.Equal(1, seedCalls);
        Assert.Equal(new[] { "A", "B", "B", "A" }, armOrder);
        Assert.Single(feedbacks.Distinct());
        Assert.Single(result.Arms.Select(a => a.FeedbackSha256).Distinct());
        Assert.Single(result.Arms.Select(a => a.BasePacketSha256).Distinct());
        Assert.Equal(2 * ((aChanged ? 1 : 0) + (bChanged ? 1 : 0)), verifyCalls);
        Assert.Equal("PAIRED", result.Status);
        Assert.All(result.Arms, arm =>
        {
            var changed = arm.Name == "A" ? aChanged : bChanged;
            Assert.Equal(changed ? "CHANGED" : "NO_PROGRESS", arm.Result);
            Assert.Equal(changed, arm.Changed);
            Assert.Equal(changed ? independent : null, arm.IndependentVerdict);
        });
        Assert.DoesNotContain(Reason, JsonSerializer.Serialize(result));
        Assert.Contains(privateSnapshots.Values, s => s.Contains(Reason, StringComparison.Ordinal));
    }

    [Fact]
    public void Accepted_seed_is_not_resampled_or_forced_into_regroup()
    {
        var calls = 0;
        AgentUnit.Model model = (_, _, request) => { calls++; return new(Verdict(BoundaryStaticCanary.Packet(request), "ACCEPT"), [], "stop"); };
        var result = BoundaryStaticCanary.Run("case", Source(), [[A(1), A(2)]], new Dictionary<string, TextOffsetSpan>(),
            new Dictionary<string, string>(), model, new AgentBudget(20, 100), 2, CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.Equal("SEED_ACCEPT", result.Status);
        Assert.Empty(result.Arms);
    }

    [Fact]
    public void Tool_informed_seed_is_excluded_from_the_static_evidence_experiment()
    {
        var calls = 0;
        AgentUnit.Model model = (_, _, request) => ++calls == 1
            ? new(JsonSerializer.Serialize(new { protocolVersion = AgentGroupVerification.Version,
                requests = new[] { new { name = "get_source_context", arguments = new { alias = A(1), before = 0, after = 1 } } } }), [], "stop")
            : new(Verdict(BoundaryStaticCanary.Packet(request), "REGROUP"), [], "stop");
        var result = BoundaryStaticCanary.Run("case", Source(), [[A(1), A(2)]], new Dictionary<string, TextOffsetSpan>(),
            new Dictionary<string, string>(), model, new AgentBudget(20, 100), 2, CancellationToken.None);
        Assert.Equal(2, calls);
        Assert.Equal("SEED_REQUESTED_DYNAMIC_EVIDENCE", result.Status);
        Assert.Empty(result.Arms);
    }

    [Fact]
    public void Command_requires_explicit_live_flag_before_reading_manifest_or_starting_calls()
    {
        Assert.Equal(2, BoundaryStaticCanaryCommand.Run(["boundary-static-ab", "missing-manifest", "unused-output"]));
        Assert.Equal(2, BoundaryStaticCanaryCommand.Run(["boundary-static-ab", "missing-manifest", "unused-output", "--dry"]));
    }
}
