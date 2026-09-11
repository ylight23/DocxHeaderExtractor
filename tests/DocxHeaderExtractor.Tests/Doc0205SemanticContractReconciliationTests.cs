using System.Security.Cryptography;
using System.Text.Json;
using DocxHeaderExtractor.Eval.ReasoningRetention;

namespace DocxHeaderExtractor.Tests;

public sealed class Doc0205SemanticContractReconciliationTests
{
    private const string Root = "eval/a99-closed-loop/doc0205-semantic-contract-audit";
    private const string SemanticRoot = "eval/a99-closed-loop/semantic-text-exact-binding";
    private const string GeneralizationRoot = "eval/a99-closed-loop/semantic-text-generalization";
    private const string StabilityRoot = "eval/a99-closed-loop/semantic-text-repeat-stability";

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

    [Fact]
    public void Unique_verbatim_text_binds_exact_utf16_span()
    {
        var aliases = new[] { new SemanticTextSourceAlias("S0042", "body/p4", 42, "ARTICLE 1 Scope") };
        var bound = SemanticTextExactBinder.Bind([new("S0042", "Scope", "SECTION")], aliases, out var observations);
        var item = Assert.Single(bound);
        Assert.Equal(10, item.Start);
        Assert.Equal(15, item.End);
        Assert.Equal(SemanticTextBindingStatus.BOUND, Assert.Single(observations).Status);
    }

    [Fact]
    public void Zero_match_duplicate_and_unknown_aliases_are_rejected_without_fuzzy_binding()
    {
        var aliases = new[] { new SemanticTextSourceAlias("S0042", "body/p4", 42, "ARTICLE ARTICLE") };
        var headings = new[] { new SemanticTextHeading("S0042", "ARTICLEX", "ARTICLE"), new SemanticTextHeading("S0042", "ARTICLE", "ARTICLE"), new SemanticTextHeading("S9999", "ARTICLE", "ARTICLE") };
        var bound = SemanticTextExactBinder.Bind(headings, aliases, out var observations);
        Assert.Empty(bound);
        Assert.Equal([SemanticTextBindingStatus.TEXT_NOT_FOUND, SemanticTextBindingStatus.AMBIGUOUS_EXACT_TEXT, SemanticTextBindingStatus.INVALID_SOURCE_ALIAS], observations.Select(x => x.Status));
    }

    [Fact]
    public void Duplicate_occurrence_ordinal_and_multiple_headings_in_one_occurrence_are_deterministic()
    {
        var aliases = new[] { new SemanticTextSourceAlias("S0042", "body/p4", 42, "ARTICLE ARTICLE") };
        var headings = new[] { new SemanticTextHeading("S0042", "ARTICLE", "ARTICLE", 2), new SemanticTextHeading("S0042", "ARTICLE", "ARTICLE", 1) };
        var bound = SemanticTextExactBinder.Bind(headings, aliases, out _);
        Assert.Equal([8, 0], bound.Select(x => x.Start));
        Assert.Equal([15, 7], bound.Select(x => x.End));
    }

    [Fact]
    public void Semantic_contract_has_no_numeric_offset_fields_and_keeps_source_alias_authority()
    {
        var schema = JsonSerializer.Serialize(SemanticTextExactBindingContract.Schema());
        Assert.DoesNotContain("\"start\":", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"end\":", schema, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("source", schema, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("verbatim", SemanticTextExactBindingContract.System, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("semanticHeadingTotal", SemanticTextExactBindingContract.System, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Frozen_semantic_text_benchmark_recovers_exact_bindings_without_system_loss()
    {
        using var comparison = LoadSemantic("comparison.v1.json");
        var root = comparison.RootElement;
        Assert.Equal(0, root.GetProperty("providerCalls").GetInt32());
        Assert.False(root.GetProperty("goldReadBeforeFreeze").GetBoolean());
        Assert.Equal("SEMANTIC_TEXT_BINDING_RECOVERS_EXACT_ACCURACY", root.GetProperty("finalClassification").GetString());

        var documents = root.GetProperty("documents").EnumerateArray().ToDictionary(x => x.GetProperty("documentId").GetString()!);
        var doc0205 = documents["DOC-0205"].GetProperty("score");
        Assert.Equal((70, 1, 1), (doc0205.GetProperty("tp").GetInt32(), doc0205.GetProperty("fp").GetInt32(), doc0205.GetProperty("fn").GetInt32()));
        Assert.Equal(70, doc0205.GetProperty("semanticCorrespondence").GetInt32());
        Assert.Equal(0, doc0205.GetProperty("wrongBoundary").GetInt32());
        Assert.Equal(1, doc0205.GetProperty("textNotFound").GetInt32());
        Assert.Equal(0, doc0205.GetProperty("systemBindingLoss").GetInt32());
        Assert.Equal(0, doc0205.GetProperty("systemValidatorLoss").GetInt32());
        Assert.Equal(0, doc0205.GetProperty("systemProjectionLoss").GetInt32());

        var doc0258 = documents["DOC-0258"].GetProperty("score");
        Assert.Equal((19, 0, 5), (doc0258.GetProperty("tp").GetInt32(), doc0258.GetProperty("fp").GetInt32(), doc0258.GetProperty("fn").GetInt32()));
        Assert.Equal(5, doc0258.GetProperty("ambiguousExactText").GetInt32());
        Assert.Equal(0, doc0258.GetProperty("systemBindingLoss").GetInt32());
        Assert.Equal(0, doc0258.GetProperty("systemValidatorLoss").GetInt32());
        Assert.Equal(0, doc0258.GetProperty("systemProjectionLoss").GetInt32());

        var summaryPath = Path.Combine(RepoRoot(), SemanticRoot, "summary.v1.json");
        using var summary = JsonDocument.Parse(File.ReadAllText(summaryPath));
        Assert.Equal(2, summary.RootElement.GetProperty("modelCalls").GetInt32());
        Assert.False(summary.RootElement.GetProperty("goldReadBeforeFreeze").GetBoolean());
    }

    [Fact]
    public void Strict_gold_generalization_cohort_is_dynamic_and_excludes_doc0264()
    {
        var inventoryPath = Path.Combine(RepoRoot(), "eval/a99-dataset/document-inventory.v1.json");
        using var inventory = JsonDocument.Parse(File.ReadAllText(inventoryPath));
        var eligible = inventory.RootElement.GetProperty("documents").EnumerateArray()
            .Select(x => x.GetProperty("documentId").GetString()!)
            .Where(id => ReasoningGoldEligibilityEvaluator.EvaluateMetadataOnly(RepoRoot(), id).Eligible)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["DOC-0001", "DOC-0205", "DOC-0252", "DOC-0256", "DOC-0258"], eligible);
        Assert.False(ReasoningGoldEligibilityEvaluator.EvaluateMetadataOnly(RepoRoot(), "DOC-0264").Eligible);
    }

    [Fact]
    public void Generalization_artifacts_keep_three_repeats_independent_and_firewall_intact()
    {
        using var summary = LoadGeneralization("summary.v1.json");
        var root = summary.RootElement;
        Assert.Equal(15, root.GetProperty("providerAttempts").GetInt32());
        Assert.Equal(15, root.GetProperty("modelCalls").GetInt32());
        Assert.False(root.GetProperty("goldReadBeforeFreeze").GetBoolean());
        Assert.Equal("SEMANTIC_TEXT_CONTRACT_DOES_NOT_GENERALIZE", root.GetProperty("generalizationClassification").GetString());

        var cohort = root.GetProperty("selectedStrictGoldCohort").EnumerateArray().Select(x => x.GetString()!).ToArray();
        Assert.Equal(["DOC-0001", "DOC-0205", "DOC-0252", "DOC-0256", "DOC-0258"], cohort);
        foreach (var documentId in cohort)
        {
            foreach (var repeat in new[] { "r1", "r2", "r3" })
            {
                var dir = Path.Combine(RepoRoot(), GeneralizationRoot, documentId!, repeat);
                using var freeze = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "freeze.v1.json")));
                Assert.False(freeze.RootElement.GetProperty("goldReadBeforeFreeze").GetBoolean());
                Assert.Equal("a99-semantic-text-exact-binding-v1", freeze.RootElement.GetProperty("semanticContractVersion").GetString());
            }
        }

        using var r2 = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), GeneralizationRoot, "DOC-0258", "r2", "score.v1.json")));
        Assert.Equal("SUCCESS", r2.RootElement.GetProperty("status").GetString());
        using var repeats = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), GeneralizationRoot, "repeat-summary.v1.json")));
        foreach (var repeat in repeats.RootElement.GetProperty("cohortMicroByRepeat").EnumerateArray())
            Assert.Equal(153, repeat.GetProperty("gold").GetInt32());
    }

    [Fact]
    public void Generalization_freezes_use_one_contract_hash_across_all_completed_repeats()
    {
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        var providers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var documentId in new[] { "DOC-0001", "DOC-0205", "DOC-0252", "DOC-0256", "DOC-0258" })
        foreach (var repeat in new[] { "r1", "r2", "r3" })
        {
            using var freeze = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), GeneralizationRoot, documentId, repeat, "freeze.v1.json")));
            hashes.Add(freeze.RootElement.GetProperty("promptHash").GetString()! + ":" + freeze.RootElement.GetProperty("schemaHash").GetString()!);
            providers.Add(freeze.RootElement.GetProperty("actualProvider").GetString() ?? "NOT_EXPOSED");
        }
        Assert.Single(hashes);
        Assert.NotEmpty(providers);
    }

    [Fact]
    public void Omission_review_is_paired_additive_and_gold_firewalled()
    {
        const string rootPath = "eval/a99-closed-loop/semantic-text-omission-review";
        using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), rootPath, "summary.v1.json")));
        var root = summary.RootElement;
        Assert.Equal("1b1d3317fdb17406321fcc796afa17bb624cc5f1", root.GetProperty("startHead").GetString());
        Assert.True(root.GetProperty("controlReused").GetBoolean());
        Assert.Equal(0, root.GetProperty("controlProviderCallsCurrent").GetInt32());
        Assert.Equal(15, root.GetProperty("reviewProviderAttempts").GetInt32());
        Assert.Equal(15, root.GetProperty("modelCalls").GetInt32());
        Assert.Equal(0, root.GetProperty("passAProviderCallsCurrentRun").GetInt32());
        Assert.True(root.GetProperty("passAReused").GetBoolean());
        Assert.False(root.GetProperty("goldReadBeforeFreeze").GetBoolean());
        Assert.Equal("OMISSION_REVIEW_RECALL_UP_PRECISION_TRADEOFF", root.GetProperty("classification").GetString());
        Assert.Equal("REVERT", root.GetProperty("keepOrRevert").GetString());
        Assert.Equal(2, root.GetProperty("reviewRecoveredGoldCount").GetInt32());
        Assert.Equal(10, root.GetProperty("reviewIntroducedFpCount").GetInt32());
        Assert.Equal(4, root.GetProperty("remainingModelOmissions").GetInt32());
        Assert.Equal(0, root.GetProperty("finalTable").EnumerateArray().Single(x => x.GetProperty("mode").GetString() == "OMISSION_REVIEW").GetProperty("systemLoss").GetInt32());
        Assert.Equal("A99_NOT_MEASURED_DEV_MARGIN_BELOW_0.995", root.GetProperty("a99DevStatus").GetString());

        var reviewTable = root.GetProperty("finalTable").EnumerateArray().Single(x => x.GetProperty("mode").GetString() == "OMISSION_REVIEW");
        Assert.Equal(427, reviewTable.GetProperty("tp").GetInt32());
        Assert.Equal(32, reviewTable.GetProperty("fp").GetInt32());
        Assert.Equal(32, reviewTable.GetProperty("fn").GetInt32());
        var baselineTable = root.GetProperty("finalTable").EnumerateArray().Single(x => x.GetProperty("mode").GetString() == "BASELINE");
        Assert.Equal(425, baselineTable.GetProperty("tp").GetInt32());
        Assert.Equal(22, baselineTable.GetProperty("fp").GetInt32());
        Assert.Equal(34, baselineTable.GetProperty("fn").GetInt32());

        using var deltas = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), rootPath, "paired-deltas.v1.json")));
        Assert.Equal(15, deltas.RootElement.GetProperty("rows").GetArrayLength());
        foreach (var repeat in deltas.RootElement.GetProperty("cohortByRepeat").EnumerateArray())
        {
            Assert.Equal(153, repeat.GetProperty("baseline").GetProperty("gold").GetInt32());
            Assert.Equal(153, repeat.GetProperty("review").GetProperty("gold").GetInt32());
        }

        foreach (var documentId in new[] { "DOC-0001", "DOC-0205", "DOC-0252", "DOC-0256", "DOC-0258" })
        foreach (var repeat in new[] { "r1", "r2", "r3" })
        {
            var dir = Path.Combine(RepoRoot(), rootPath, documentId, repeat);
            using var freeze = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "freeze.v1.json")));
            Assert.False(freeze.RootElement.GetProperty("goldReadBeforeFreeze").GetBoolean());
            Assert.Equal("abc8bb1f767e7556f121ed4a1f708ed60bd97502ca420e399ce7af26711e6e53", freeze.RootElement.GetProperty("baseContractHash").GetString());
            Assert.True(freeze.RootElement.GetProperty("reviewPromptHash").GetString()!.Length == 64);
            Assert.False(string.IsNullOrWhiteSpace(freeze.RootElement.GetProperty("actualProvider").GetString()));
            Assert.Equal("stop", freeze.RootElement.GetProperty("finishReason").GetString());
            Assert.True(freeze.RootElement.GetProperty("reasoningConfiguration").GetProperty("enabled").GetBoolean());
            Assert.True(freeze.RootElement.GetProperty("reasoningTokens").GetInt32() > 0);
            Assert.Equal(freeze.RootElement.GetProperty("predictionSha256").GetString(), Sha256(Path.Combine(dir, "prediction.v1.json")));
            Assert.Equal(freeze.RootElement.GetProperty("resultSha256").GetString(), Sha256(Path.Combine(dir, "result.v1.json")));
            using var prediction = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "prediction.v1.json")));
            Assert.True(prediction.RootElement.GetProperty("passAReused").GetBoolean());
            Assert.Equal(0, prediction.RootElement.GetProperty("passAProviderCallsCurrentRun").GetInt32());
        }
    }

    [Fact]
    public void Omission_review_prompt_is_generic_and_uses_semantic_text_contract()
    {
        Assert.DoesNotContain("DOC-", SemanticTextOmissionReviewContract.System, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("semanticHeadingTotal", SemanticTextOmissionReviewContract.System, StringComparison.OrdinalIgnoreCase);
        var schema = JsonSerializer.Serialize(SemanticTextOmissionReviewContract.Schema());
        Assert.DoesNotContain("\"start\":", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"end\":", schema, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("source", schema, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("verbatim", SemanticTextOmissionReviewContract.System, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Frozen_semantic_text_optional_nulls_replay_without_provider_calls()
    {
        var response = SemanticTextExactBindingContract.Parse("{\"headings\":[{\"source\":\"S0001\",\"text\":\"Heading\",\"role\":\"SECTION\",\"occurrence\":null,\"leftExactContext\":null,\"rightExactContext\":null}]}" );
        Assert.Single(response.Headings);
        Assert.Null(response.Headings[0].Occurrence);
    }

    [Fact]
    public void Repeat_stability_audit_is_offline_and_freezes_before_gold_score()
    {
        using var summary = LoadStability("summary.v1.json");
        var root = summary.RootElement;
        Assert.Equal(0, root.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, root.GetProperty("modelCalls").GetInt32());
        Assert.False(root.GetProperty("goldReadBeforeFreeze").GetBoolean());
        Assert.Equal(153, root.GetProperty("goldTotal").GetInt32());
        Assert.Equal(0, root.GetProperty("systemLoss").GetInt32());
        Assert.Equal("MIXED_RESIDUALS_NO_CLEAR_WINNER", root.GetProperty("finalClassification").GetString());
        Assert.Equal("STOCHASTIC_FP", root.GetProperty("largestResidualBucket").GetString());

        using var matrix = LoadStability("baseline-matrix.v1.json");
        Assert.False(matrix.RootElement.GetProperty("goldReadBeforeFreeze").GetBoolean());
        Assert.DoesNotContain("goldOccurrences", matrix.RootElement.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(15, matrix.RootElement.GetProperty("runs").GetArrayLength());
        foreach (var operatorName in new[] { "intersection-3of3", "majority-2of3", "union-1of3" })
        {
            var dir = Path.Combine(RepoRoot(), StabilityRoot, "consensus", operatorName);
            using var freeze = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "freeze.v1.json")));
            Assert.False(freeze.RootElement.GetProperty("goldReadBeforeFreeze").GetBoolean());
            Assert.Equal(0, freeze.RootElement.GetProperty("providerCalls").GetInt32());
            var predictionPath = Path.Combine(dir, "prediction.v1.json");
            var expectedHash = freeze.RootElement.GetProperty("predictionSha256").GetString();
            Assert.Equal(expectedHash, Sha256(predictionPath));
        }
    }

    [Fact]
    public void Repeat_stability_and_consensus_counts_are_exact_and_predefined()
    {
        using var stability = LoadStability("gold-stability.v1.json");
        var root = stability.RootElement;
        Assert.Equal(142, root.GetProperty("found3of3").GetInt32());
        Assert.Equal(1, root.GetProperty("found2of3").GetInt32());
        Assert.Equal(0, root.GetProperty("found1of3").GetInt32());
        Assert.Equal(10, root.GetProperty("found0of3").GetInt32());
        Assert.Equal(142, root.GetProperty("stableExact").GetInt32());
        Assert.Equal(1, root.GetProperty("spanVariant").GetInt32());
        Assert.Equal(10, root.GetProperty("stableOmission").GetInt32());
        Assert.Equal(0, root.GetProperty("stochasticOmission").GetInt32());
        Assert.Equal(7, root.GetProperty("fpStability").GetProperty("fp3of3").GetInt32());
        Assert.Equal(0, root.GetProperty("fpStability").GetProperty("fp2of3").GetInt32());
        Assert.Equal(11, root.GetProperty("fpStability").GetProperty("fp1of3").GetInt32());

        using var summary = LoadStability("summary.v1.json");
        var metrics = summary.RootElement.GetProperty("consensus").EnumerateArray().ToDictionary(x => x.GetProperty("label").GetString()!);
        Assert.Equal((142, 7, 11), (metrics["INTERSECTION_3_OF_3"].GetProperty("tp").GetInt32(), metrics["INTERSECTION_3_OF_3"].GetProperty("fp").GetInt32(), metrics["INTERSECTION_3_OF_3"].GetProperty("fn").GetInt32()));
        Assert.Equal((143, 7, 10), (metrics["MAJORITY_2_OF_3"].GetProperty("tp").GetInt32(), metrics["MAJORITY_2_OF_3"].GetProperty("fp").GetInt32(), metrics["MAJORITY_2_OF_3"].GetProperty("fn").GetInt32()));
        Assert.Equal((143, 18, 10), (metrics["UNION_1_OF_3"].GetProperty("tp").GetInt32(), metrics["UNION_1_OF_3"].GetProperty("fp").GetInt32(), metrics["UNION_1_OF_3"].GetProperty("fn").GetInt32()));
    }

    private static JsonDocument Load(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), Root, name)));
    private static JsonDocument LoadSemantic(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), SemanticRoot, name)));
    private static JsonDocument LoadGeneralization(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), GeneralizationRoot, name)));
    private static JsonDocument LoadStability(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), StabilityRoot, name)));
    private static string RepoRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
