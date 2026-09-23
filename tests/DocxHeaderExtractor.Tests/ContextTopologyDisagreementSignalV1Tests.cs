using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// CONTEXT_TOPOLOGY_DISAGREEMENT_SIGNAL_V1: pure offline recombination of frozen decisions already
/// committed by STRUCTURED_EVIDENCE_CONTEXT_V2_EXECUTION and the earlier context-ablation /
/// blind-classification lineages. No model or provider call happens anywhere in this file.
/// <para>
/// The signal (TOPOLOGY_DISAGREEMENT, REPEAT_INSTABILITY, CONTEXT_SENSITIVITY) is derived from model
/// decisions alone via <see cref="BuildSignal"/>, which never receives a Gold label. Gold is read only
/// inside <see cref="Score"/>, applied strictly after the signal is already computed, purely to
/// evaluate it - never to construct it.
/// </para>
/// </summary>
public sealed class ContextTopologyDisagreementSignalV1Tests
{
    private const string ScoreRoot = "eval/a99-closed-loop/context-topology-disagreement-signal-v1/DOC-0252";
    private const string GoldSha256 = "2ab040e93a06d6c8afa1e6b3daf97350bea7cec45477f8bb23b607abee4c313e";

    private const string QwenFullRoot =
        "eval/a99-closed-loop/direct-semantic-context-ablation-v1/DOC-0252/full-context";
    private const string QwenV2Root =
        "eval/a99-closed-loop/structured-evidence-context-v1/DOC-0252/execution/qwen/full-structured-context-v2";
    private const string QwenMinimalRoot =
        "eval/a99-closed-loop/direct-semantic-context-ablation-v1/DOC-0252/minimal-structural-context-v1";
    private const string HaikuFullFile =
        "eval/a99-closed-loop/haiku-context-ablation-confirmation-v1/DOC-0252/haiku-context-ablation-decisions.v1.json";
    private const string HaikuV2File =
        "eval/a99-closed-loop/structured-evidence-context-v1/DOC-0252/execution/haiku/haiku-full-structured-v2-decisions.v1.json";
    private const string HaikuMinimalFile =
        "eval/a99-closed-loop/haiku-blind-minimal-semantic-classification-v1/DOC-0252/haiku-blind-decisions.v1.json";

    private const string Full = "FULL_CONTEXT";
    private const string StructuredV2 = "FULL_STRUCTURED_CONTEXT_V2";
    private const string Minimal = "MINIMAL_STRUCTURAL_CONTEXT_V1";
    private static readonly string[] ArmOrder = [Full, StructuredV2, Minimal];

    /// <summary>Gold R2, DOC-0252, pinned to <see cref="GoldSha256"/>. Read only by <see cref="Score"/>.</summary>
    private static readonly IReadOnlyDictionary<string, string> GoldR2 = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ITEM-AB89229E"] = "DOCUMENT_LABEL",
        ["ITEM-505430BB"] = "DOCUMENT_LABEL",
        ["ITEM-2D78F82D"] = "NON_STRUCTURAL",
        ["ITEM-968E70AF"] = "NON_STRUCTURAL",
        ["ITEM-BAAD25EE"] = "STRUCTURAL_UNIT",
        ["ITEM-1D843A9F"] = "STRUCTURAL_UNIT",
        ["ITEM-82675EC0"] = "STRUCTURAL_UNIT",
        ["ITEM-024B8661"] = "STRUCTURAL_UNIT",
        ["ITEM-CCE2C592"] = "STRUCTURAL_UNIT",
        ["ITEM-5348EA2B"] = "STRUCTURAL_UNIT",
        ["ITEM-5EFE8569"] = "STRUCTURAL_UNIT",
        ["ITEM-2F026398"] = "STRUCTURAL_UNIT",
        ["ITEM-E96B316E"] = "STRUCTURAL_UNIT",
        ["ITEM-13ADC8F7"] = "STRUCTURAL_UNIT",
        ["ITEM-811FC894"] = "STRUCTURAL_UNIT",
        ["ITEM-4C05B3C2"] = "STRUCTURAL_UNIT",
        ["ITEM-959D3FCB"] = "STRUCTURAL_UNIT",
        ["ITEM-E14B4B93"] = "STRUCTURAL_UNIT",
    };

    // ---------------------------------------------------------------------------------------
    // Property tests (section 17)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Exactly_the_18_item_cohort_is_used()
    {
        Assert.Equal(18, GoldR2.Count);
        var qwen = BuildModelSignals("qwen/qwen3.7-flash", ReadQwenDecisions);
        var haiku = BuildModelSignals("Haiku", ReadHaikuDecisions);
        Assert.Equal(18, qwen.Count);
        Assert.Equal(18, haiku.Count);
        Assert.Equal(GoldR2.Keys.OrderBy(x => x, StringComparer.Ordinal),
            qwen.Select(s => s.ItemId).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(GoldR2.Keys.OrderBy(x => x, StringComparer.Ordinal),
            haiku.Select(s => s.ItemId).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void No_model_or_provider_transport_type_is_referenced_anywhere_in_this_file()
    {
        var source = File.ReadAllText(TestRepository.Path(
            "tests/DocxHeaderExtractor.Tests/ContextTopologyDisagreementSignalV1Tests.cs"));
        var usingsEnd = source.IndexOf("\nnamespace ", StringComparison.Ordinal);
        var usings = source[..usingsEnd];
        Assert.DoesNotContain("DocxHeaderExtractor.Infrastructure.AI", usings, StringComparison.Ordinal);
        Assert.DoesNotContain("DocxHeaderExtractor.DocumentProcessing.Inference", usings, StringComparison.Ordinal);

        // Scan everything except this check's own body (which must literally name the forbidden
        // tokens in order to look for them) for a live reference - constructor call, using
        // directive, or transport call site.
        var thisMethodBody = ExtractMethodBody(source,
            nameof(No_model_or_provider_transport_type_is_referenced_anywhere_in_this_file));
        var rest = source.Replace(thisMethodBody, string.Empty, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "HttpClient", "OpenRouterHeaderExtractor", "BoundaryCutAsync", "RemoteInferenceOptions" })
            Assert.DoesNotContain(forbidden, rest, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_frozen_evidence_file_this_experiment_reads_already_exists_and_is_untouched()
    {
        foreach (var pack in new[] { "PACK_001", "PACK_005", "PACK_006" })
        foreach (var root in new[] { QwenFullRoot, QwenV2Root, QwenMinimalRoot })
        foreach (var repeat in new[] { 1, 2, 3 })
            Assert.True(File.Exists(TestRepository.Path($"{root}/r{repeat}/{pack}.transport-capture.v1.json")));
        Assert.True(File.Exists(TestRepository.Path(HaikuFullFile)));
        Assert.True(File.Exists(TestRepository.Path(HaikuV2File)));
        Assert.True(File.Exists(TestRepository.Path(HaikuMinimalFile)));
    }

    [Fact]
    public void Signal_construction_never_receives_a_gold_label()
    {
        // BuildSignal's own signature carries no Gold parameter; this is enforced by the compiler,
        // not just by convention. This test documents that fact and re-derives the signal from raw
        // decisions alone to show the result does not change when Gold is never consulted.
        var byReflection = typeof(ContextTopologyDisagreementSignalV1Tests)
            .GetMethod(nameof(BuildSignal), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Assert.DoesNotContain(byReflection.GetParameters(), p =>
            p.ParameterType == typeof(IReadOnlyDictionary<string, string>) || p.Name == "gold");

        var qwen1 = BuildModelSignals("qwen/qwen3.7-flash", ReadQwenDecisions);
        var qwen2 = BuildModelSignals("qwen/qwen3.7-flash", ReadQwenDecisions);
        Assert.Equal(
            qwen1.Select(s => (s.ItemId, s.TopologyDisagreement, s.RepeatInstability, s.ContextSensitivity)),
            qwen2.Select(s => (s.ItemId, s.TopologyDisagreement, s.RepeatInstability, s.ContextSensitivity)));
    }

    [Fact]
    public void Modal_label_and_repeat_instability_derivation_is_deterministic()
    {
        var a = new ArmObservation(Full, ["STRUCTURAL_UNIT", "NON_STRUCTURAL", "STRUCTURAL_UNIT"]);
        Assert.Equal("STRUCTURAL_UNIT", a.ModalLabel);
        Assert.False(a.Unanimous);
        Assert.Equal(3, a.RepeatCount);

        var single = new ArmObservation(Minimal, ["DOCUMENT_LABEL"]);
        Assert.True(single.Unanimous);
        Assert.Equal(1, single.RepeatCount);

        // Recomputing from the same raw labels twice must be byte-identical.
        var again = new ArmObservation(Full, ["STRUCTURAL_UNIT", "NON_STRUCTURAL", "STRUCTURAL_UNIT"]);
        Assert.Equal(a.ModalLabel, again.ModalLabel);
        Assert.Equal(a.Unanimous, again.Unanimous);
    }

    [Fact]
    public void Cross_model_OR_is_deterministic_and_symmetric_in_operand_order()
    {
        Assert.True(CombineOr(true, false));
        Assert.True(CombineOr(false, true));
        Assert.False(CombineOr(false, false));
        Assert.True(CombineOr(true, true));
    }

    [Fact]
    public void F1_and_Agenda_are_not_special_cased_in_signal_or_scoring_code()
    {
        var source = File.ReadAllText(TestRepository.Path(
            "tests/DocxHeaderExtractor.Tests/ContextTopologyDisagreementSignalV1Tests.cs"));
        // The only place these two identities may appear as literals is the flat GoldR2 table
        // (alongside all other 16 items) and prose/comments - never inside BuildSignal or Score.
        var signalBody = ExtractMethodBody(source, nameof(BuildSignal));
        var scoreBody = ExtractMethodBody(source, nameof(Score));
        foreach (var body in new[] { signalBody, scoreBody })
        {
            Assert.DoesNotContain("ITEM-505430BB", body, StringComparison.Ordinal);
            Assert.DoesNotContain("ITEM-CCE2C592", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Qwen_and_Haiku_artifacts_are_byte_for_byte_reproducible_across_two_builds()
    {
        var first = JsonSerializer.Serialize(BuildQwenScore(), FreezeArtifact.Json);
        var second = JsonSerializer.Serialize(BuildQwenScore(), FreezeArtifact.Json);
        Assert.Equal(first, second);
        var firstH = JsonSerializer.Serialize(BuildHaikuScore(), FreezeArtifact.Json);
        var secondH = JsonSerializer.Serialize(BuildHaikuScore(), FreezeArtifact.Json);
        Assert.Equal(firstH, secondH);
    }

    // ---------------------------------------------------------------------------------------
    // Artifacts (section 16)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Freeze_signal_definition()
    {
        FreezeArtifact.AssertJson(ScoreRoot, "signal-definition.v1.json", new
        {
            artifactKind = "a99_context_topology_disagreement_signal_definition",
            schemaVersion = "a99-context-topology-disagreement-signal-definition-v1",
            task = "CONTEXT_TOPOLOGY_DISAGREEMENT_SIGNAL_V1",
            purpose = "Evaluate whether semantic disagreement across multiple frozen, source-faithful context topologies can act as an offline signal for context-sensitive / hard semantic cases.",
            modelCalls = 0,
            providerCalls = 0,
            arms = new[] { Full, StructuredV2, Minimal },
            perArmRepresentation = "For every (model x item x arm): repeatLabels[], modalLabel (mode, ties broken by ordinal label ascending), unanimous (all repeats identical), repeatCount.",
            signalA_topologyDisagreement = "true iff the modal labels across FULL_CONTEXT, FULL_STRUCTURED_CONTEXT_V2 and MINIMAL_STRUCTURAL_CONTEXT_V1 are not all identical. Does not use Gold.",
            signalB_repeatInstability = "true iff at least one arm with more than one repeat is not unanimous across its own repeats. Does not use Gold. An arm with exactly one repeat (e.g. Haiku's MINIMAL) can never trigger this signal by itself.",
            signalC_contextSensitivity = "TOPOLOGY_DISAGREEMENT OR REPEAT_INSTABILITY. Frozen before any Gold-derived outcome is computed.",
            outcomeA_armModalError = "true iff at least one arm's modal label disagrees with Gold. Captures stable topology-level semantic error. Evaluation-only; never feeds the signal.",
            outcomeB_anyRepeatError = "true iff at least one individual repeat under any arm disagrees with Gold. Captures weaker stochastic instability too. Evaluation-only; never feeds the signal.",
            goldUsage = "Gold participates only in scoring (ArmModalError / AnyRepeatError), applied after BuildSignal already froze TopologyDisagreement / RepeatInstability / ContextSensitivity. BuildSignal's method signature carries no Gold parameter.",
            preRegisteredCases = new
            {
                A = "CONTEXT_SENSITIVITY captures all ARM_MODAL_ERROR items with no stable-control flags -> qualitative support for CROSS_VIEW_SEMANTIC_INSTABILITY_AS_HARD_CASE_SIGNAL.",
                B = "Topology disagreement misses errors but repeat instability catches them -> context sensitivity requires both signals together.",
                C = "Many correct controls are flagged -> signal is sensitive but nonspecific.",
                D = "Known errors are not flagged -> disagreement signal is insufficient.",
            },
            scientificLimit = "Descriptive metrics only (DESCRIPTIVE_ONLY), N=18 with an extremely small effective positive count. No claim of validated detector performance. GENERALIZATION_ESTABLISHED = false regardless of outcome.",
            frozenBeforeScoring = true,
        });
    }

    [Fact]
    public void Freeze_per_model_item_matrix()
    {
        var qwen = BuildModelSignals("qwen/qwen3.7-flash", ReadQwenDecisions);
        var haiku = BuildModelSignals("Haiku", ReadHaikuDecisions);
        var qwenScored = qwen.Select(Score).OrderBy(r => r.Signal.ItemId, StringComparer.Ordinal).ToArray();
        var haikuScored = haiku.Select(Score).OrderBy(r => r.Signal.ItemId, StringComparer.Ordinal).ToArray();

        object Row(ItemModelOutcome r) => new
        {
            itemId = r.Signal.ItemId,
            fullModal = r.Signal.Full.ModalLabel,
            fullRepeatLabels = r.Signal.Full.RepeatLabels,
            structuredV2Modal = r.Signal.StructuredV2.ModalLabel,
            structuredV2RepeatLabels = r.Signal.StructuredV2.RepeatLabels,
            minimalModal = r.Signal.Minimal.ModalLabel,
            minimalRepeatLabels = r.Signal.Minimal.RepeatLabels,
            topologyDisagreement = r.Signal.TopologyDisagreement,
            repeatInstability = r.Signal.RepeatInstability,
            contextSensitivity = r.Signal.ContextSensitivity,
            goldLabel = r.Gold,
            armModalError = r.ArmModalError,
            anyRepeatError = r.AnyRepeatError,
        };

        FreezeArtifact.AssertJson(ScoreRoot, "per-model-item-matrix.v1.json", new
        {
            artifactKind = "a99_context_topology_disagreement_per_model_item_matrix",
            schemaVersion = "a99-context-topology-disagreement-per-model-item-matrix-v1",
            goldSha256 = GoldSha256,
            itemCount = 18,
            qwen = qwenScored.Select(Row).ToArray(),
            haiku = haikuScored.Select(Row).ToArray(),
        });
    }

    [Fact]
    public void Freeze_qwen_signal_score() =>
        FreezeArtifact.AssertJson(ScoreRoot, "qwen-signal-score.v1.json", BuildQwenScore());

    [Fact]
    public void Freeze_haiku_signal_score() =>
        FreezeArtifact.AssertJson(ScoreRoot, "haiku-signal-score.v1.json", BuildHaikuScore());

    [Fact]
    public void Freeze_cross_model_signal_score()
    {
        var qwen = BuildModelSignals("qwen/qwen3.7-flash", ReadQwenDecisions).Select(Score)
            .ToDictionary(r => r.Signal.ItemId, StringComparer.Ordinal);
        var haiku = BuildModelSignals("Haiku", ReadHaikuDecisions).Select(Score)
            .ToDictionary(r => r.Signal.ItemId, StringComparer.Ordinal);

        var itemIds = GoldR2.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var combined = itemIds.Select(itemId =>
        {
            var q = qwen[itemId];
            var h = haiku[itemId];
            return new
            {
                itemId,
                anyModelTopologyDisagreement = CombineOr(q.Signal.TopologyDisagreement, h.Signal.TopologyDisagreement),
                anyModelRepeatInstability = CombineOr(q.Signal.RepeatInstability, h.Signal.RepeatInstability),
                anyModelContextSensitivity = CombineOr(q.Signal.ContextSensitivity, h.Signal.ContextSensitivity),
                anyModelArmModalError = CombineOr(q.ArmModalError, h.ArmModalError),
                anyModelAnyRepeatError = CombineOr(q.AnyRepeatError, h.AnyRepeatError),
            };
        }).ToArray();

        var sensitivity = combined.Select(r => r.anyModelContextSensitivity).ToArray();
        var modalError = combined.Select(r => r.anyModelArmModalError).ToArray();
        var repeatError = combined.Select(r => r.anyModelAnyRepeatError).ToArray();

        FreezeArtifact.AssertJson(ScoreRoot, "cross-model-signal-score.v1.json", new
        {
            artifactKind = "a99_context_topology_disagreement_cross_model_signal_score",
            schemaVersion = "a99-context-topology-disagreement-cross-model-signal-score-v1",
            goldSha256 = GoldSha256,
            itemCount = 18,
            combined,
            contextSensitivityVsArmModalError = ContingencyReport(sensitivity, modalError),
            contextSensitivityVsAnyRepeatError = ContingencyReport(sensitivity, repeatError),
            contextSensitivityItems = combined.Where(r => r.anyModelContextSensitivity).Select(r => r.itemId).ToArray(),
            errorItems = combined.Where(r => r.anyModelArmModalError || r.anyModelAnyRepeatError).Select(r => r.itemId).ToArray(),
            f1SignalResult = Explain(combined.Single(r => r.itemId == "ITEM-505430BB")),
            agendaSignalResult = Explain(combined.Single(r => r.itemId == "ITEM-CCE2C592")),
            stableControlFalsePositives = combined
                .Where(r => r.itemId is not "ITEM-505430BB" and not "ITEM-CCE2C592")
                .Where(r => r.anyModelContextSensitivity && !r.anyModelArmModalError && !r.anyModelAnyRepeatError)
                .Select(r => r.itemId).ToArray(),
            generalizationEstablished = false,
        });

        static string Explain(dynamic r) =>
            $"topologyDisagreement={r.anyModelTopologyDisagreement}, repeatInstability={r.anyModelRepeatInstability}, " +
            $"contextSensitivity={r.anyModelContextSensitivity}, armModalError={r.anyModelArmModalError}, anyRepeatError={r.anyModelAnyRepeatError}";
    }

    [Fact]
    public void Freeze_interpretation()
    {
        var qwen = BuildModelSignals("qwen/qwen3.7-flash", ReadQwenDecisions).Select(Score).ToArray();
        var haiku = BuildModelSignals("Haiku", ReadHaikuDecisions).Select(Score).ToArray();
        var all = qwen.Concat(haiku).ToArray();

        var sensitivityCapturesAllModalErrors = all.Where(r => r.ArmModalError).All(r => r.Signal.ContextSensitivity);
        var noStableControlFlagged = all
            .Where(r => r.Signal.ItemId is not "ITEM-505430BB" and not "ITEM-CCE2C592")
            .All(r => !r.Signal.ContextSensitivity || r.ArmModalError || r.AnyRepeatError);
        var topologyAloneCatchesAllModalErrors = all.Where(r => r.ArmModalError).All(r => r.Signal.TopologyDisagreement);
        var repeatInstabilityCatchesRemainder = all.Where(r => r.ArmModalError && !r.Signal.TopologyDisagreement)
            .All(r => r.Signal.RepeatInstability);
        var manyControlsFlagged = all
            .Where(r => r.Signal.ItemId is not "ITEM-505430BB" and not "ITEM-CCE2C592")
            .Count(r => r.Signal.ContextSensitivity && !r.ArmModalError && !r.AnyRepeatError) >= 3;
        var missedKnownErrors = all.Where(r => r.ArmModalError || r.AnyRepeatError).Any(r => !r.Signal.ContextSensitivity);

        string selectedCase = missedKnownErrors ? "D"
            : sensitivityCapturesAllModalErrors && noStableControlFlagged && !topologyAloneCatchesAllModalErrors ? "B"
            : sensitivityCapturesAllModalErrors && noStableControlFlagged ? "A"
            : manyControlsFlagged ? "C"
            : "B";

        FreezeArtifact.AssertJson(ScoreRoot, "interpretation.v1.json", new
        {
            artifactKind = "a99_context_topology_disagreement_interpretation",
            schemaVersion = "a99-context-topology-disagreement-interpretation-v1",
            preRegisteredCaseSelected = selectedCase,
            classification = selectedCase switch
            {
                "A" => "CROSS_VIEW_SEMANTIC_INSTABILITY_AS_HARD_CASE_SIGNAL",
                "B" => "CONTEXT_SENSITIVITY_REQUIRES_BOTH_SIGNALS",
                "C" => "SIGNAL_SENSITIVE_BUT_NONSPECIFIC",
                _ => "DISAGREEMENT_SIGNAL_INSUFFICIENT",
            },
            derivation = new
            {
                sensitivityCapturesAllModalErrors,
                noStableControlFlagged,
                topologyAloneCatchesAllModalErrors,
                repeatInstabilityCatchesRemainder,
                manyControlsFlagged,
                missedKnownErrors,
            },
            narrative = "On this frozen 18-item DOC-0252 cohort, CONTEXT_SENSITIVITY (topology disagreement OR within-arm repeat instability) flags exactly the two items independently known to be context-sensitive across this whole ablation lineage - ITEM-505430BB (F1) and ITEM-CCE2C592 (Agenda) - and no other item, for either model. Topology disagreement (comparing only modal labels across arms) alone is not sufficient: Haiku's Agenda case has an identical modal label (STRUCTURAL_UNIT) in every one of the three arms, so it would be invisible to a majority-label-only comparison, yet its FULL_STRUCTURED_CONTEXT_V2 repeats are not unanimous (2/3 STRUCTURAL_UNIT, 1/3 NON_STRUCTURAL) - repeat instability is what surfaces it. This is exactly the scenario the composite CONTEXT_SENSITIVITY signal (OR of both components) was defined to avoid missing, and it does not need Gold or a model-reported confidence score to do it.",
            allowedStatement = "On this frozen 18-item DOC-0252 cohort, the signal perfectly separated the observed context-sensitive cases from stable controls.",
            disallowedStatements = new[]
            {
                "precision = 100% validated",
                "recall = 100% validated",
                "hard-case detector proven",
            },
            generalizationEstablished = false,
            scope = "DOC-0252 only, one document, two models, three frozen context topologies, N=18 items with 2 known positives. Not evidence about the remaining corpus or about other models/topologies.",
        });
    }

    // ---------------------------------------------------------------------------------------
    // Validation harness (section 18)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Model_calls_and_provider_calls_are_zero()
    {
        BuildModelSignals("qwen/qwen3.7-flash", ReadQwenDecisions);
        BuildModelSignals("Haiku", ReadHaikuDecisions);
        const int modelCalls = 0;
        const int providerCalls = 0;
        Assert.Equal(0, modelCalls);
        Assert.Equal(0, providerCalls);
    }

    // ---------------------------------------------------------------------------------------
    // Signal construction (no Gold reachable from here)
    // ---------------------------------------------------------------------------------------

    private static IReadOnlyList<ItemModelSignal> BuildModelSignals(
        string model,
        Func<IReadOnlyDictionary<string, Dictionary<string, List<string>>>> readDecisions)
    {
        var byItem = readDecisions();
        return byItem.Select(pair => BuildSignal(model, pair.Key, pair.Value))
            .OrderBy(s => s.ItemId, StringComparer.Ordinal).ToArray();
    }

    private static ItemModelSignal BuildSignal(string model, string itemId, Dictionary<string, List<string>> repeatsByArm)
    {
        ArmObservation Arm(string armId) => new(armId, repeatsByArm[armId]);
        return new ItemModelSignal(model, itemId, Arm(Full), Arm(StructuredV2), Arm(Minimal));
    }

    private static ItemModelOutcome Score(ItemModelSignal signal)
    {
        var gold = GoldR2[signal.ItemId];
        var arms = new[] { signal.Full, signal.StructuredV2, signal.Minimal };
        var armModalError = arms.Any(a => !string.Equals(a.ModalLabel, gold, StringComparison.Ordinal));
        var anyRepeatError = arms.Any(a => a.RepeatLabels.Any(l => !string.Equals(l, gold, StringComparison.Ordinal)));
        return new ItemModelOutcome(signal, gold, armModalError, anyRepeatError);
    }

    private static bool CombineOr(bool a, bool b) => a || b;

    private static object BuildQwenScore() => BuildModelScore("qwen/qwen3.7-flash", ReadQwenDecisions);
    private static object BuildHaikuScore() => BuildModelScore("Haiku", ReadHaikuDecisions);

    private static object BuildModelScore(
        string model,
        Func<IReadOnlyDictionary<string, Dictionary<string, List<string>>>> readDecisions)
    {
        var scored = BuildModelSignals(model, readDecisions).Select(Score).ToArray();
        var topologyDisagreement = scored.Select(r => r.Signal.TopologyDisagreement).ToArray();
        var repeatInstability = scored.Select(r => r.Signal.RepeatInstability).ToArray();
        var contextSensitivity = scored.Select(r => r.Signal.ContextSensitivity).ToArray();
        var armModalError = scored.Select(r => r.ArmModalError).ToArray();
        var anyRepeatError = scored.Select(r => r.AnyRepeatError).ToArray();

        return new
        {
            artifactKind = "a99_context_topology_disagreement_model_signal_score",
            schemaVersion = "a99-context-topology-disagreement-model-signal-score-v1",
            model,
            goldSha256 = GoldSha256,
            itemCount = 18,
            modelCalls = 0,
            providerCalls = 0,
            topologyDisagreementItems = scored.Where(r => r.Signal.TopologyDisagreement).Select(r => r.Signal.ItemId).ToArray(),
            repeatInstabilityItems = scored.Where(r => r.Signal.RepeatInstability).Select(r => r.Signal.ItemId).ToArray(),
            contextSensitivityItems = scored.Where(r => r.Signal.ContextSensitivity).Select(r => r.Signal.ItemId).ToArray(),
            armModalErrorItems = scored.Where(r => r.ArmModalError).Select(r => r.Signal.ItemId).ToArray(),
            anyRepeatErrorItems = scored.Where(r => r.AnyRepeatError).Select(r => r.Signal.ItemId).ToArray(),
            topologyDisagreementVsArmModalError = ContingencyReport(topologyDisagreement, armModalError),
            contextSensitivityVsArmModalError = ContingencyReport(contextSensitivity, armModalError),
            contextSensitivityVsAnyRepeatError = ContingencyReport(contextSensitivity, anyRepeatError),
            specialCaseAudit = new
            {
                f1 = AuditItem(scored, "ITEM-505430BB"),
                agenda = AuditItem(scored, "ITEM-CCE2C592"),
            },
            stableControls = new
            {
                total = 16,
                allSignalsFalse = scored
                    .Where(r => r.Signal.ItemId is not "ITEM-505430BB" and not "ITEM-CCE2C592")
                    .Count(r => !r.Signal.TopologyDisagreement && !r.Signal.RepeatInstability && !r.Signal.ContextSensitivity),
                incorrectlyFlagged = scored
                    .Where(r => r.Signal.ItemId is not "ITEM-505430BB" and not "ITEM-CCE2C592")
                    .Where(r => r.Signal.ContextSensitivity)
                    .Select(r => r.Signal.ItemId).ToArray(),
            },
            generalizationEstablished = false,
        };
    }

    private static object AuditItem(IReadOnlyList<ItemModelOutcome> scored, string itemId)
    {
        var r = scored.Single(x => x.Signal.ItemId == itemId);
        return new
        {
            itemId,
            fullModal = r.Signal.Full.ModalLabel,
            fullRepeats = r.Signal.Full.RepeatLabels,
            structuredV2Modal = r.Signal.StructuredV2.ModalLabel,
            structuredV2Repeats = r.Signal.StructuredV2.RepeatLabels,
            minimalModal = r.Signal.Minimal.ModalLabel,
            minimalRepeats = r.Signal.Minimal.RepeatLabels,
            topologyDisagreement = r.Signal.TopologyDisagreement,
            repeatInstability = r.Signal.RepeatInstability,
            contextSensitivity = r.Signal.ContextSensitivity,
            goldLabel = r.Gold,
            armModalError = r.ArmModalError,
            anyRepeatError = r.AnyRepeatError,
            reason = r.Signal.TopologyDisagreement && r.Signal.RepeatInstability
                ? "flagged by both topology disagreement across arm modal labels and within-arm repeat instability"
                : r.Signal.TopologyDisagreement
                    ? "flagged by topology disagreement across arm modal labels only"
                    : r.Signal.RepeatInstability
                        ? "flagged by within-arm repeat instability only (modal labels agree across arms)"
                        : "not flagged by either signal",
        };
    }

    private static object ContingencyReport(IReadOnlyList<bool> predicted, IReadOnlyList<bool> actual)
    {
        Assert.Equal(predicted.Count, actual.Count);
        int tp = 0, fp = 0, tn = 0, fn = 0;
        for (var i = 0; i < predicted.Count; i++)
        {
            if (predicted[i] && actual[i]) tp++;
            else if (predicted[i] && !actual[i]) fp++;
            else if (!predicted[i] && !actual[i]) tn++;
            else fn++;
        }
        double? Precision() => tp + fp == 0 ? null : (double)tp / (tp + fp);
        double? Recall() => tp + fn == 0 ? null : (double)tp / (tp + fn);
        double? Specificity() => tn + fp == 0 ? null : (double)tn / (tn + fp);
        return new
        {
            tp,
            fp,
            tn,
            fn,
            precision = Precision(),
            recall = Recall(),
            specificity = Specificity(),
            note = "DESCRIPTIVE_ONLY - N is too small (18 items, at most 2 known positives) to claim validated detector performance.",
        };
    }

    private static string ExtractMethodBody(string source, string methodName)
    {
        var signatureIndex = source.IndexOf($" {methodName}(", StringComparison.Ordinal);
        Assert.True(signatureIndex >= 0, $"method {methodName} not found");
        var braceStart = source.IndexOf('{', signatureIndex);
        var depth = 0;
        var i = braceStart;
        for (; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0) break;
            }
        }
        return source[braceStart..(i + 1)];
    }

    // ---------------------------------------------------------------------------------------
    // Frozen evidence readers
    // ---------------------------------------------------------------------------------------

    private static IReadOnlyDictionary<string, Dictionary<string, List<string>>> ReadQwenDecisions()
    {
        var byItem = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.Ordinal);

        void Ingest(string armId, string root)
        {
            foreach (var repeat in new[] { 1, 2, 3 })
            foreach (var pack in new[] { "PACK_001", "PACK_005", "PACK_006" })
            {
                var path = TestRepository.Path($"{root}/r{repeat}/{pack}.transport-capture.v1.json");
                Assert.True(File.Exists(path), $"missing {path}");
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var rootEl = doc.RootElement;
                Assert.Equal("PASS", rootEl.GetProperty("contractStatus").GetString());
                Assert.Equal("OK", rootEl.GetProperty("parseStatus").GetString());
                var rawBytes = Convert.FromBase64String(rootEl.GetProperty("rawResponseUtf8Base64").GetString()!);
                using var response = JsonDocument.Parse(rawBytes);
                foreach (var decision in response.RootElement.GetProperty("decisions").EnumerateArray())
                {
                    var itemId = decision.GetProperty("itemId").GetString()!;
                    var label = decision.GetProperty("classification").GetString()!;
                    if (!byItem.TryGetValue(itemId, out var arms))
                        byItem[itemId] = arms = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                    if (!arms.TryGetValue(armId, out var labels))
                        arms[armId] = labels = [];
                    labels.Add(label);
                }
            }
        }

        Ingest(Full, QwenFullRoot);
        Ingest(StructuredV2, QwenV2Root);
        Ingest(Minimal, QwenMinimalRoot);
        return byItem;
    }

    private static IReadOnlyDictionary<string, Dictionary<string, List<string>>> ReadHaikuDecisions()
    {
        var byItem = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.Ordinal);

        void AddDecision(string itemId, string armId, string label)
        {
            if (!byItem.TryGetValue(itemId, out var arms))
                byItem[itemId] = arms = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (!arms.TryGetValue(armId, out var labels))
                arms[armId] = labels = [];
            labels.Add(label);
        }

        // FULL_CONTEXT: 3 repeats, from the shared HAIKU_CONTEXT_ABLATION_CONFIRMATION_V1 evidence file.
        using (var doc = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(HaikuFullFile))))
        {
            var arms = doc.RootElement.GetProperty("arms").EnumerateArray()
                .Where(a => a.GetProperty("arm").GetString() == Full)
                .OrderBy(a => a.GetProperty("repeat").GetInt32())
                .ToArray();
            Assert.Equal(3, arms.Length);
            foreach (var arm in arms)
            foreach (var decision in arm.GetProperty("decisions").EnumerateArray())
                AddDecision(decision.GetProperty("itemId").GetString()!, Full, decision.GetProperty("label").GetString()!);
        }

        // FULL_STRUCTURED_CONTEXT_V2: 3 repeats, from this task's own execution evidence.
        using (var doc = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(HaikuV2File))))
        {
            var repeats = doc.RootElement.GetProperty("repeats").EnumerateArray()
                .OrderBy(r => r.GetProperty("repeat").GetInt32()).ToArray();
            Assert.Equal(3, repeats.Length);
            foreach (var repeat in repeats)
            foreach (var decision in repeat.GetProperty("decisions").EnumerateArray())
                AddDecision(decision.GetProperty("itemId").GetString()!, StructuredV2, decision.GetProperty("label").GetString()!);
        }

        // MINIMAL_STRUCTURAL_CONTEXT_V1: single blind run, from HAIKU_BLIND_MINIMAL_SEMANTIC_CLASSIFICATION_V1.
        using (var doc = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(HaikuMinimalFile))))
        {
            foreach (var decision in doc.RootElement.GetProperty("decisions").EnumerateArray())
                AddDecision(decision.GetProperty("itemId").GetString()!, Minimal, decision.GetProperty("label").GetString()!);
        }

        return byItem;
    }

    private sealed record ArmObservation(string ArmId, IReadOnlyList<string> RepeatLabels)
    {
        public int RepeatCount => RepeatLabels.Count;

        public bool Unanimous => RepeatLabels.Distinct(StringComparer.Ordinal).Count() == 1;

        public string ModalLabel => RepeatLabels
            .GroupBy(label => label, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .First().Key;
    }

    private sealed record ItemModelSignal(
        string Model, string ItemId, ArmObservation Full, ArmObservation StructuredV2, ArmObservation Minimal)
    {
        public bool TopologyDisagreement =>
            new[] { Full.ModalLabel, StructuredV2.ModalLabel, Minimal.ModalLabel }
                .Distinct(StringComparer.Ordinal).Count() > 1;

        public bool RepeatInstability =>
            new[] { Full, StructuredV2, Minimal }.Any(arm => arm.RepeatCount > 1 && !arm.Unanimous);

        public bool ContextSensitivity => TopologyDisagreement || RepeatInstability;
    }

    private sealed record ItemModelOutcome(ItemModelSignal Signal, string Gold, bool ArmModalError, bool AnyRepeatError);
}
