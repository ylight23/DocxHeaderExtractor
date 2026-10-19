using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.HeadingAgent;
using DocxHeaderExtractor.V6Qualification;
using static DocxHeaderExtractor.HeadingAgent.AgentProtocol;

namespace DocxHeaderExtractor.Tests;

public sealed class AdjudicationBiasCanariesTests
{
    const string BoundaryProtocol = "QUALIFICATION_BOUNDARY_LABEL_SWAP_V1";
    const string HierarchyProtocol = "QUALIFICATION_SINGLE_CHILD_HIERARCHY_V1";

    [Fact]
    public void Label_swap_changes_only_label_assignment_and_excludes_trajectory()
    {
        var source = SourceComparativeCanariesTests.Source();
        var item = SourceComparativeCanaries.BoundaryCases[0];
        var normal = AdjudicationBiasCanaries.BoundaryPacket(source, item, swap: false);
        var swapped = AdjudicationBiasCanaries.BoundaryPacket(source, item, swap: true);
        Assert.True(JsonNode.DeepEquals(normal["sourceEvidence"], swapped["sourceEvidence"]));
        Assert.True(JsonNode.DeepEquals(normal["alternatives"]!["A"], swapped["alternatives"]!["B"]));
        Assert.True(JsonNode.DeepEquals(normal["alternatives"]!["B"], swapped["alternatives"]!["A"]));
        Assert.Null(normal["trajectory"]);
        Assert.DoesNotContain("oscillation", normal.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Physical_choice_is_normalized_across_label_swap()
    {
        var source = SourceComparativeCanariesTests.Source();
        var item = SourceComparativeCanaries.BoundaryCases[0];
        string Reply(string label) => JsonSerializer.Serialize(new { protocol = BoundaryProtocol, caseId = "CASE-01",
            decision = label, evidenceRefs = new[] { "P026-L003", "P026-L004" } });
        AgentUnit.Model baseModel = (_, _, request) =>
        {
            Assert.DoesNotContain("trajectory", request.Body, StringComparison.OrdinalIgnoreCase);
            return new(Reply("A"), [], "stop");
        };
        AgentUnit.Model swappedModel = (_, _, _) => new(Reply("B"), [], "stop");
        var baseArm = AdjudicationBiasCanaries.JudgeBoundary(source, item, false, baseModel, new(1, 0.5), default);
        var swappedArm = AdjudicationBiasCanaries.JudgeBoundary(source, item, true, swappedModel, new(1, 0.5), default);
        Assert.Equal("ORIGINAL_A", baseArm.PhysicalChoice);
        Assert.Equal("ORIGINAL_A", swappedArm.PhysicalChoice);
        Assert.Equal("ORIGINAL_A_AS_A", baseArm.LabelOrder);
        Assert.Equal("ORIGINAL_B_AS_A", swappedArm.LabelOrder);
    }

    [Fact]
    public void Hierarchy_packet_is_one_child_and_hides_old_proposal_and_joint_consistency()
    {
        var packet = AdjudicationBiasCanaries.HierarchyPacket(SourceComparativeCanariesTests.Source(), "P008-L018");
        Assert.Equal("P008-L018", packet["target"]!["childAlias"]!.GetValue<string>());
        Assert.Null(packet["graph"]);
        Assert.Null(packet["proposedParent"]);
        Assert.Null(packet["consistencyNote"]);
        Assert.Equal(new[] { "P008-L016", "P008-L013", "ROOT", "UNRESOLVED" },
            packet["target"]!["candidateParents"]!.Deserialize<string[]>());
    }

    [Theory]
    [InlineData("P008-L016", null)]
    [InlineData("P008-L013", null)]
    [InlineData("P008-L999", "RESPONSE_PROTOCOL_INVALID")]
    public void Single_child_parent_response_is_bounded_to_listed_parents(string parent, string? error)
    {
        var json = JsonSerializer.Serialize(new { protocol = HierarchyProtocol, childAlias = "P008-L018", parentAlias = parent,
            evidenceRefs = new[] { "P008-L016", "P008-L018" } });
        var parsed = AdjudicationBiasCanaries.ParseParent(json, "P008-L018");
        Assert.Equal(error, parsed.Error);
        if (error is null) Assert.Equal(parent, parsed.Decision!.ParentAlias);
        else Assert.Null(parsed.Decision);
    }

    [Fact]
    public void Qualification_commands_require_explicit_mode()
    {
        Assert.Equal(2, AdjudicationBiasCanaries.Run(["boundary-label-swap-canary", "missing", "unused"], hierarchy: false));
        Assert.Equal(2, AdjudicationBiasCanaries.Run(["hierarchy-single-child-canary", "missing", "unused"], hierarchy: true));
    }
}
