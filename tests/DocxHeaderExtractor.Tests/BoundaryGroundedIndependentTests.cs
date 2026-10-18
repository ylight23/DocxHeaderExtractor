using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.HeadingAgent;
using DocxHeaderExtractor.V6Qualification;
using static DocxHeaderExtractor.HeadingAgent.AgentProtocol;

namespace DocxHeaderExtractor.Tests;

public sealed class BoundaryGroundedIndependentTests
{
    static string A(int n) => $"P001-L{n:000}";
    static readonly string[][] Proposal = [[A(1)], [A(2), A(3)]];
    static readonly string[][] Counterproposal = [[A(1), A(2)], [A(3)]];

    [Theory]
    [InlineData("accept", "GROUNDED_ACCEPT_CURRENT", 1, true)]
    [InlineData("repair", "GROUNDED_ACCEPT_CURRENT", 2, true)]
    [InlineData("counterproposal", "GROUNDED_REGROUP_REJECT_CURRENT", 1, false)]
    [InlineData("persistent", "UNRESOLVED", 3, false)]
    [InlineData("unresolved", "UNRESOLVED", 1, false)]
    [InlineData("membership", "UNRESOLVED", 1, false)]
    [InlineData("length", "UNRESOLVED", 1, false)]
    public void Final_grounded_independent_check_never_applies_a_second_suggestion(string mode, string status, int expectedCalls, bool accept)
    {
        var seed = BoundaryGroundedCanaryTests.Seed();
        var packet = seed["packet"]!.DeepClone().AsObject();
        packet["candidates"]![0]!["proposedGroups"] = JsonSerializer.SerializeToNode(Proposal);
        int calls = 0;
        AgentUnit.Model model = (unit, round, request) =>
        {
            calls++;
            Assert.Equal("E-INDEPENDENT", unit); // Any recursive Ask or grouper would have a different unit.
            Assert.DoesNotContain("PRIVATE_COMPLAINT", request.Body);
            Assert.DoesNotContain("frozenComplaint", request.Body);
            Assert.DoesNotContain("D-INDEPENDENT", request.Body);
            var sent = BoundaryStaticCanary.Packet(request);
            Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(Proposal), sent["candidates"]![0]!["proposedGroups"]));
            var decision = mode switch
            {
                "counterproposal" or "persistent" => "REGROUP",
                "repair" when round == 1 => "REGROUP",
                "unresolved" => "UNRESOLVED", "membership" => "MEMBERSHIP_DISPUTE", _ => "ACCEPT"
            };
            var suggested = decision == "REGROUP" ? mode == "counterproposal" ? Counterproposal : Proposal : null;
            var content = JsonSerializer.Serialize(new
            {
                protocolVersion = BoundaryGroundedCanary.Version,
                verdicts = new[] { new
                {
                    run = "V1", decision, reason = "VALID_REASON", evidenceRefs = new[] { A(1) },
                    currentGroups = Proposal, suggestedGroups = suggested,
                    membership = Proposal.SelectMany(g => g).Select(a => new { alias = a,
                        status = mode == "membership" && a == A(3) ? "NOT_HEADING" : "HEADING", evidenceRefs = new[] { a } })
                } }
            });
            return new(content, [], mode == "length" ? "length" : "stop");
        };
        var result = BoundaryGroundedCanary.Evaluate(BoundaryStaticCanaryTests.Source(), packet,
            new Dictionary<string, DocxHeaderExtractor.DocumentProcessing.Authority.TextOffsetSpan>(), new Dictionary<string, string>(),
            model, new(6, 1), default, independentOnly: true);
        Assert.Equal(expectedCalls, calls);
        Assert.Equal(status, result.Status);
        Assert.Equal(accept, result.QualifiedAccept);
        Assert.Null(result.IndependentVerdict); // E itself is terminal; it cannot spawn an independent-of-independent check.
        Assert.True(BoundaryGroundedCanary.Equal(Proposal, result.InputGroups));
        if (mode == "counterproposal") Assert.True(BoundaryGroundedCanary.Equal(Counterproposal, result.SuggestedGroups!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Original_independent_packet_is_reconstructed_and_hash_mismatch_is_rejected(bool tamper)
    {
        var seed = BoundaryGroundedCanaryTests.Seed();
        var packet = seed["packet"]!.DeepClone().AsObject();
        packet["candidates"]![0]!["proposedGroups"] = JsonSerializer.SerializeToNode(Proposal);
        var request = AgentGrouping.BuildRequest(packet.ToJsonString(new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), [], AgentGroupVerification.Prompt);
        var report = JsonSerializer.SerializeToNode(new { report = new { outcomes = new[] { new
        {
            id = "V17", category = "CHANGED_INDEPENDENT_REJECT_OR_UNRESOLVED",
            result = new { independentVerdict = "REGROUP", suggestedGroups = Proposal, seedPacketSha256 = Sha(seed["packet"]!.ToJsonString()) }
        } } } })!.AsObject();
        var cohortLock = JsonSerializer.SerializeToNode(new { eligibleSeeds = new[] { new
        {
            id = "V17", privateSeedSha256 = "seedsha", packetSha256 = Sha(seed["packet"]!.ToJsonString())
        } } })!.AsObject();
        var check = JsonSerializer.SerializeToNode(new
        {
            Verdicts = new[] { new AgentGroupVerification.Verdict("V1", "REGROUP", "PRIVATE_OLD_REASON", [A(1)],
                Proposal.SelectMany(g => g).Select(a => new AgentGroupVerification.Membership(a, "HEADING", [a])).ToArray()) },
            VerifierEvidence = new { Requests = Array.Empty<object>() },
            Evidence = new[] { JsonSerializer.Serialize(new { requestSha256 = tamper ? "tampered" : request.Sha256, response = "PRIVATE_OLD_RESPONSE" }) }
        })!.AsObject();
        if (tamper)
            Assert.Throws<InvalidOperationException>(() => BoundaryGroundedIndependentCommand.Prepare(new("V17", "seedsha", "oldsha"), report, cohortLock, seed, check));
        else
        {
            var prepared = BoundaryGroundedIndependentCommand.Prepare(new("V17", "seedsha", "oldsha"), report, cohortLock, seed, check);
            Assert.Equal(request.Sha256, prepared.OldRequestSha256);
            Assert.DoesNotContain("PRIVATE_OLD_REASON", JsonSerializer.Serialize(prepared));
            Assert.DoesNotContain("PRIVATE_OLD_RESPONSE", JsonSerializer.Serialize(prepared));
            Assert.True(JsonNode.DeepEquals(packet, prepared.Packet));
        }
    }

    [Fact]
    public void Independent_mode_refuses_complaint_before_any_model_call()
    {
        var packet = BoundaryGroundedCanaryTests.Seed()["packet"]!.DeepClone().AsObject();
        packet["frozenComplaint"] = "PRIVATE_COMPLAINT";
        int calls = 0;
        AgentUnit.Model model = (_, _, _) => { calls++; throw new InvalidOperationException(); };
        Assert.Throws<InvalidOperationException>(() => BoundaryGroundedCanary.Evaluate(BoundaryStaticCanaryTests.Source(), packet,
            new Dictionary<string, DocxHeaderExtractor.DocumentProcessing.Authority.TextOffsetSpan>(), new Dictionary<string, string>(),
            model, new(6, 1), default, independentOnly: true));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Explicit_mode_is_required_before_reading_inputs() => Assert.Equal(2,
        BoundaryGroundedIndependentCommand.Run(["boundary-grounded-e", "missing", "missing"]));
}
