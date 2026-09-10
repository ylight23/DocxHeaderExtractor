using System.Text.Json;
using DocxHeaderExtractor.Eval.ReasoningRetention;

namespace DocxHeaderExtractor.Tests;

public sealed class CanonicalHeadingTaskContractV2Tests
{
    [Fact]
    public void Contract_is_generic_and_contains_no_gold_instance_or_document_leak()
    {
        var text = CanonicalHeadingTaskContractV2.ContractTextForHash();

        Assert.DoesNotContain("DOC-", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Gold", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("71", text, StringComparison.Ordinal);
        Assert.Contains("UTF-16", text, StringComparison.Ordinal);
        Assert.Contains("half-open", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rich_roles_are_representable_but_projection_is_explicit()
    {
        Assert.True(CanonicalHeadingTaskContractV2.IsTaskProjectionRole("ARTICLE"));
        Assert.True(CanonicalHeadingTaskContractV2.IsTaskProjectionRole("DOCUMENT_TITLE"));
        Assert.False(CanonicalHeadingTaskContractV2.IsTaskProjectionRole("TOC_ENTRY"));
        Assert.False(CanonicalHeadingTaskContractV2.IsTaskProjectionRole("RUNNING_HEADER"));
        Assert.True(CanonicalHeadingTaskContractV2.IsNonTaskStructuralRole("CAPTION"));
        Assert.Contains("CAPTION", CanonicalHeadingTaskContractV2.SemanticRoles);
        Assert.Contains("BODY_FRAGMENT", CanonicalHeadingTaskContractV2.SemanticRoles);
    }

    [Fact]
    public void Candidate_visibility_is_not_a_contract_gate_and_parser_accepts_non_task_roles()
    {
        Assert.Contains("source occurrence may contain zero, one, or many", CanonicalHeadingTaskContractV2.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("candidate list", CanonicalHeadingTaskContractV2.SystemPrompt, StringComparison.OrdinalIgnoreCase);

        using var response = JsonDocument.Parse("""
            {"headings":[{"i":0,"start":0,"end":7,"role":"RUNNING_HEADER"},{"i":0,"start":8,"end":15,"role":"ARTICLE"}]}
            """);
        var parsed = CeilingSemanticResponseParser.Parse(response.RootElement.GetRawText());
        Assert.Equal(2, parsed.Headings.Count);
        Assert.Equal("RUNNING_HEADER", parsed.Headings[0].Role);
    }

    [Fact]
    public void Contract_has_one_identical_semantic_contract_for_text_and_visual_routes()
    {
        Assert.Equal(CanonicalHeadingTaskContractV2.ProtocolVersion, "a99-canonical-heading-task-v2");
        Assert.Equal(CanonicalHeadingTaskContractV2.SystemPrompt, CanonicalHeadingTaskContractV2.SystemPrompt);
        Assert.Contains("XML style", CanonicalHeadingTaskContractV2.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("visual", CanonicalHeadingTaskContractV2.SystemPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Frozen_contract_and_clean_benchmark_artifacts_preserve_the_gold_firewall()
    {
        using var freeze = LoadArtifact("contract-freeze.v2.json");
        Assert.Equal("a99-canonical-heading-contract-freeze-v2", freeze.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal(0, freeze.RootElement.GetProperty("providerCalls").GetInt32());
        Assert.False(freeze.RootElement.GetProperty("goldReadBeforeFreeze").GetBoolean());

        using var summary = LoadArtifact("summary.v2.json");
        Assert.Equal("TASK_CONTRACT_ALIGNMENT_NO_MATERIAL_GAIN", summary.RootElement.GetProperty("finalClassification").GetString());
        Assert.Equal(2, summary.RootElement.GetProperty("providerCalls").GetInt32());
        Assert.False(summary.RootElement.GetProperty("goldReadBeforeFreeze").GetBoolean());
        Assert.Equal(0, summary.RootElement.GetProperty("doc0205").GetProperty("tp").GetInt32());
        Assert.Equal(101, summary.RootElement.GetProperty("doc0205").GetProperty("fp").GetInt32());
        Assert.Equal(17, summary.RootElement.GetProperty("doc0258").GetProperty("tp").GetInt32());
        Assert.Equal(0, summary.RootElement.GetProperty("doc0258").GetProperty("systemLossCount").GetInt32());
    }

    [Fact]
    public void Contract_v2_forensic_audit_is_offline_and_binding_sound_before_vlm()
    {
        using var audit = LoadArtifactFrom("doc0205-contract-v2-forensic", "audit.v1.json");
        Assert.True(audit.RootElement.GetProperty("offlineOnly").GetBoolean());
        Assert.Equal(0, audit.RootElement.GetProperty("providerCalls").GetInt32());
        Assert.False(audit.RootElement.GetProperty("goldFirewall").GetProperty("goldReadBeforeFreeze").GetBoolean());
        Assert.True(audit.RootElement.GetProperty("bindingAudit").GetProperty("allEqual").GetBoolean());
        Assert.Equal(101, audit.RootElement.GetProperty("bindingAudit").GetProperty("recomputedCount").GetInt32());
        Assert.Equal("VLM_VISUAL_EVIDENCE_JUSTIFIED", audit.RootElement.GetProperty("decision").GetProperty("classification").GetString());
    }

    [Fact]
    public void Visual_contract_v2_freeze_and_page_packet_audit_preserve_invariants()
    {
        var root = Path.Combine(Root(), "eval", "a99-closed-loop", "qwen37-flash-visual-ceiling", "DOC-0205", "visual-v2");
        using var freeze = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "freeze.v2.json")));
        Assert.Equal(CanonicalHeadingTaskContractV2.ProtocolVersion, freeze.RootElement.GetProperty("semanticContractVersion").GetString());
        Assert.Equal(3, freeze.RootElement.GetProperty("providerAttempts").GetInt32());
        Assert.All(freeze.RootElement.GetProperty("finishReasons").EnumerateArray(), x => Assert.Equal("stop", x.GetString()));
        Assert.False(freeze.RootElement.GetProperty("goldReadBeforeFreeze").GetBoolean());

        using var score = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "score.v2.json")));
        Assert.Equal(0, score.RootElement.GetProperty("tp").GetInt32());
        Assert.Equal(3, score.RootElement.GetProperty("fp").GetInt32());
        Assert.Equal(71, score.RootElement.GetProperty("fn").GetInt32());
        Assert.Equal(0, score.RootElement.GetProperty("systemLossCount").GetInt32());

        using var packets = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "visual-packet-audit.v2.json")));
        var rows = packets.RootElement.GetProperty("windows").EnumerateArray().ToArray();
        var eight = rows.First(x => x.GetProperty("kind").GetString() == "PLANNED_WINDOW" && x.GetProperty("packetMetrics").GetProperty("pageCountInRequest").GetInt32() == 8);
        var one = rows.First(x => x.GetProperty("kind").GetString() == "SINGLE_PAGE_PROBE" && x.GetProperty("packetMetrics").GetProperty("pageCountInRequest").GetInt32() == 1);
        Assert.True(one.GetProperty("packetMetrics").GetProperty("packetChars").GetInt32() < eight.GetProperty("packetMetrics").GetProperty("packetChars").GetInt32());
    }

    [Fact]
    public void Visual_semantic_presence_audit_reconciles_all_71_gold_rows_without_provider_calls()
    {
        using var audit = LoadArtifactFrom("doc0205-visual-semantic-audit", "audit.v1.json");
        var root = audit.RootElement;
        Assert.True(root.GetProperty("offlineOnly").GetBoolean());
        Assert.Equal(0, root.GetProperty("providerCalls").GetInt32());
        Assert.Equal(4, root.GetProperty("sourceStructure").GetProperty("sourceOccurrenceCount").GetInt32());
        Assert.Equal(60769, root.GetProperty("sourceStructure").GetProperty("dominantOccurrence").GetProperty("rawCharacterLength").GetInt32());
        Assert.Equal(1512, root.GetProperty("sourceStructure").GetProperty("ooxmlFacts").GetProperty("breakElementCount").GetInt32());
        Assert.Equal(72, root.GetProperty("vlmPipeline").GetProperty("rawVlmProposalCount").GetInt32());
        Assert.Equal(5, root.GetProperty("vlmPipeline").GetProperty("boundCount").GetInt32());
        Assert.Equal(3, root.GetProperty("vlmPipeline").GetProperty("finalCount").GetInt32());
        Assert.Equal(31, root.GetProperty("semantic").GetProperty("mappingFailure").GetInt32());
        Assert.Equal(40, root.GetProperty("semantic").GetProperty("trueOmission").GetInt32());
        Assert.Equal("MIXED_REPRESENTATION_AND_MODEL_FAILURE", root.GetProperty("finalClassification").GetString());
        Assert.True(root.GetProperty("goldFirewall").GetProperty("goldMutation").GetBoolean() == false);
    }

    private static JsonDocument LoadArtifact(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "eval", "a99-closed-loop", "canonical-heading-contract-v2", name)));

    private static JsonDocument LoadArtifactFrom(string directory, string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "eval", "a99-closed-loop", directory, name)));

    private static string Root() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
}
