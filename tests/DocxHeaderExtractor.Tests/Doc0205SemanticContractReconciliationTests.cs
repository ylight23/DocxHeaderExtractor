using System.Security.Cryptography;
using System.Text.Json;
using DocxHeaderExtractor.Eval.ReasoningRetention;

namespace DocxHeaderExtractor.Tests;

public sealed class Doc0205SemanticContractReconciliationTests
{
    private const string Root = "eval/a99-closed-loop/doc0205-semantic-contract-audit";

    [Theory]
    [InlineData("EXACT_MATCH", 10, 20, "body/p4", "Heading", "article", 10, 20, "body/p4", "Heading")]
    [InlineData("MODEL_PROPOSAL_WRONG_ROLE", 10, 20, "body/p4", "Heading", "chapter", 10, 20, "body/p4", "Heading")]
    [InlineData("SEMANTIC_PRESENT_PARTIAL_SPAN", 12, 18, "body/p4", "eadin", "article", 10, 20, "body/p4", "Heading")]
    [InlineData("SEMANTIC_PRESENT_SUPERSET_SPAN", 8, 22, "body/p4", "xxHeadingxx", "article", 10, 20, "body/p4", "Heading")]
    [InlineData("SEMANTIC_PRESENT_OFFSET_SHIFT", 18, 25, "body/p4", "ing text", "article", 10, 20, "body/p4", "Heading")]
    [InlineData("SEMANTIC_PRESENT_WRONG_SOURCE_SAME_TEXT", 10, 20, "body/p5", "Heading", "article", 10, 20, "body/p4", "Heading")]
    public void Span_correspondence_is_explicit_and_deterministic(string expected, int ps, int pe, string psource, string ptext, string role, int gs, int ge, string gsource, string gtext)
    {
        var goldRole = expected == "MODEL_PROPOSAL_WRONG_ROLE" ? "article" : role;
        Assert.Equal(expected, Doc0205SemanticContractReconciliationRunner.ClassifySpan(ps, pe, psource, ptext, role, gs, ge, gsource, gtext, goldRole));
    }

    [Fact]
    public void Partial_and_superset_are_not_omissions_and_exact_score_authority_is_preserved()
    {
        using var summary = Load("summary.v1.json");
        using var reconciliation = Load("run-reconciliation.v1.json");
        var c0 = reconciliation.RootElement.GetProperty("c0WrongSpanReconciliation");
        Assert.Equal(56, c0.GetProperty("officialWrongSpanLoss").GetInt32());
        Assert.Equal(56, c0.GetProperty("semanticCorrespondence").GetInt32());
        Assert.Equal(14, c0.GetProperty("trueOmission").GetInt32());
        var score = summary.RootElement.GetProperty("runTable").EnumerateArray().Single(x => x.GetProperty("model").GetString() == "C0 structure-preserving");
        Assert.Equal(1, score.GetProperty("tp").GetInt32());
        Assert.Equal(57, score.GetProperty("fp").GetInt32());
        Assert.Equal(70, score.GetProperty("fn").GetInt32());
    }

    [Fact]
    public void Audit_is_offline_gold_is_firewalled_and_all_gold_rows_are_present()
    {
        using var summary = Load("summary.v1.json");
        using var authority = Load("authority-profile.v1.json");
        Assert.Equal(0, summary.RootElement.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, summary.RootElement.GetProperty("modelCalls").GetInt32());
        Assert.False(summary.RootElement.GetProperty("goldReadBeforeFreeze").GetBoolean());
        Assert.Equal(71, authority.RootElement.GetProperty("observedGoldProfile").GetProperty("total").GetInt32());
        Assert.True(authority.RootElement.GetProperty("observedGoldProfile").GetProperty("allRolesEvaluable").GetBoolean());
        Assert.Equal("SEMANTIC_DISCOVERY_GOOD_SPAN_CONTRACT_BAD", summary.RootElement.GetProperty("primaryClassification").GetString());
        Assert.False(Doc0205SemanticContractReconciliationRunner.IsGoldRoleEvaluable("heading"));
        Assert.True(Doc0205SemanticContractReconciliationRunner.IsGoldRoleEvaluable("article"));
    }

    [Fact]
    public void Frozen_prediction_and_result_hashes_match_authorities_and_gold_firewall()
    {
        var runs = new[]
        {
            ("openrouter-qwen35-9b-per-segment-recovery/documents/DOC-0205", "prediction.v1.json", "result.v1.json", "freeze.v1.json"),
            ("qwen37-flash-reasoning-ceiling/DOC-0205/r1-ceiling", "prediction.v1.json", "result.v1.json", "freeze.v1.json"),
            ("structure-preserving-ir/DOC-0205", "text-ceiling.prediction.v1.json", "text-ceiling.result.v1.json", "text-ceiling.freeze.v1.json"),
            ("flash-heading-contract-realignment/DOC-0205/c1_boundary", "prediction.v1.json", "result.v1.json", "freeze.v1.json"),
            ("flash-heading-contract-realignment/DOC-0205/c2_addressed", "prediction.v1.json", "result.v1.json", "freeze.v1.json"),
            ("heading-target-ontology/DOC-0205", "prediction.v1.json", "result.v1.json", "freeze.v1.json"),
            ("qwen37-flash-visual-ceiling/DOC-0205", "prediction.v1.json", "result.v1.json", "freeze.v1.json"),
        };
        foreach (var (dir, prediction, result, freeze) in runs)
        {
            var basePath = Path.Combine(RepoRoot(), "eval/a99-closed-loop", dir.Replace('/', Path.DirectorySeparatorChar));
            using var authority = JsonDocument.Parse(File.ReadAllText(Path.Combine(basePath, freeze)));
            var root = authority.RootElement;
            Assert.False(root.GetProperty("goldReadBeforeFreeze").GetBoolean());
            Assert.Equal(root.GetProperty("predictionSha256").GetString(), Sha256(Path.Combine(basePath, prediction)));
            Assert.Equal(root.GetProperty("resultSha256").GetString(), Sha256(Path.Combine(basePath, result)));
        }
    }

    private static JsonDocument Load(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), Root, name)));
    private static string RepoRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
