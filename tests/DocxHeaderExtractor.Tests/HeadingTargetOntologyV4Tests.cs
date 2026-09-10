using System.Text.Json;
using DocxHeaderExtractor.Eval.ReasoningRetention;

namespace DocxHeaderExtractor.Tests;

public sealed class HeadingTargetOntologyV4Tests
{
    [Fact]
    public void V4_is_generic_and_has_no_document_or_count_leakage()
    {
        var text = HeadingTargetOntologyV4Contract.ContractTextForHash();

        Assert.DoesNotContain("DOC-", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("71", text, StringComparison.Ordinal);
        Assert.DoesNotContain("24", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Gold", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("substantive", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UTF-16", text, StringComparison.Ordinal);
        Assert.Contains("half-open", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bold =>", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("candidate", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void V4_uses_the_existing_compact_shape_and_generic_roles_only()
    {
        var schema = JsonSerializer.Serialize(HeadingTargetOntologyV4Contract.Schema());
        Assert.Contains("headings", schema, StringComparison.Ordinal);
        Assert.Contains("\"i\"", schema, StringComparison.Ordinal);
        Assert.Contains("\"start\"", schema, StringComparison.Ordinal);
        Assert.Contains("\"end\"", schema, StringComparison.Ordinal);
        Assert.Contains("\"role\"", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("confidence", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hierarchy", schema, StringComparison.OrdinalIgnoreCase);
        Assert.All(HeadingTargetOntologyV4Contract.Roles, role => Assert.True(CeilingSemanticRole.IsAllowed(role)));
        Assert.DoesNotContain("TOC_ENTRY", HeadingTargetOntologyV4Contract.Roles);
        Assert.DoesNotContain("LIST_ITEM", HeadingTargetOntologyV4Contract.Roles);
    }

    [Fact]
    public void V4_keeps_binder_validator_and_projection_outside_runtime_contract()
    {
        Assert.Contains("owned range", HeadingTargetOntologyV4Contract.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("uncertain", HeadingTargetOntologyV4Contract.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.True(HeadingTargetOntologyV4Contract.IsTaskRole("ARTICLE"));
        Assert.False(HeadingTargetOntologyV4Contract.IsTaskRole("BODY_FRAGMENT"));
        Assert.Equal("a99-heading-target-ontology-v4", HeadingTargetOntologyV4Contract.ProtocolVersion);
    }

    [Fact]
    public void Completed_artifacts_prove_gold_firewall_and_zero_system_loss()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var campaign = Path.Combine(root, "eval", "a99-closed-loop", "heading-target-ontology");
        if (!File.Exists(Path.Combine(campaign, "summary.v1.json"))) return;

        using var contract = JsonDocument.Parse(File.ReadAllText(Path.Combine(campaign, "contract-v4.v1.json")));
        Assert.False(contract.RootElement.GetProperty("goldReadBeforeFreeze").GetBoolean());
        Assert.Equal(0, contract.RootElement.GetProperty("providerCalls").GetInt32());
        Assert.True(contract.RootElement.GetProperty("goldLeakageTest").GetProperty("passed").GetBoolean());

        foreach (var id in new[] { "DOC-0205", "DOC-0258" })
        {
            using var score = JsonDocument.Parse(File.ReadAllText(Path.Combine(campaign, id, "score.v1.json")));
            Assert.Equal(0, score.RootElement.GetProperty("systemLossCount").GetInt32());
            Assert.False(score.RootElement.GetProperty("goldReadBeforeFreeze").GetBoolean());
        }
    }
}
