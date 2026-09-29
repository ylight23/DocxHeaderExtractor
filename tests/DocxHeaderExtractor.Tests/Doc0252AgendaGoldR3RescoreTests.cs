using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Offline-rescores every current-conclusion experiment DOC0252_AGENDA_IDENTITY_GOLD_R3 (commit
/// 35e870d) affects, from already-frozen raw responses only. Zero model, provider, or VLM calls.
/// Gold R2's historical artifacts are not touched; every artifact here is a NEW, separate file.
/// <para>
/// Only ITEM-CCE2C592's expected label changes (STRUCTURAL_UNIT -&gt; DOCUMENT_LABEL); every other
/// item's expected label and every raw returned value in every cell is unchanged from the Gold R2
/// rescores this supersedes for current-conclusion purposes.
/// </para>
/// </summary>
public sealed class Doc0252AgendaGoldR3RescoreTests
{
    private const string GoldR3Sha256 = "a26b83e14005a9a3e4957cd7a027155e97a534a73fd737551726d18a1c695654";
    private const string GoldR2Sha256 = "2ab040e93a06d6c8afa1e6b3daf97350bea7cec45477f8bb23b607abee4c313e";
    private const string AgendaItemId = "ITEM-CCE2C592";
    private const string F1ItemId = "ITEM-505430BB";

    private static readonly IReadOnlyDictionary<string, string> GoldR3 = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ITEM-AB89229E"] = "DOCUMENT_LABEL",
        ["ITEM-505430BB"] = "DOCUMENT_LABEL",
        ["ITEM-2D78F82D"] = "NON_STRUCTURAL",
        ["ITEM-968E70AF"] = "NON_STRUCTURAL",
        ["ITEM-BAAD25EE"] = "STRUCTURAL_UNIT",
        ["ITEM-1D843A9F"] = "STRUCTURAL_UNIT",
        ["ITEM-82675EC0"] = "STRUCTURAL_UNIT",
        ["ITEM-024B8661"] = "STRUCTURAL_UNIT",
        ["ITEM-CCE2C592"] = "DOCUMENT_LABEL", // changed R2 -> R3
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
    // Qwen context ablation (FULL / LOCAL / MINIMAL) + FULL_STRUCTURED_CONTEXT_V2
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Freeze_qwen_context_ablation_score_gold_r3()
    {
        var arms = new[]
        {
            ("FULL_CONTEXT", "eval/a99-closed-loop/direct-semantic-context-ablation-v1/DOC-0252/full-context"),
            ("LOCAL_CONTEXT_RADIUS_3", "eval/a99-closed-loop/direct-semantic-context-ablation-v1/DOC-0252/local-context-radius-3"),
            ("MINIMAL_STRUCTURAL_CONTEXT_V1", "eval/a99-closed-loop/direct-semantic-context-ablation-v1/DOC-0252/minimal-structural-context-v1"),
            ("FULL_STRUCTURED_CONTEXT_V2", "eval/a99-closed-loop/structured-evidence-context-v1/DOC-0252/execution/qwen/full-structured-context-v2"),
        };

        var armScores = arms.Select(arm => ScoreQwenArm(arm.Item1, arm.Item2)).ToArray();

        FreezeArtifact.AssertJson("eval/a99-closed-loop/direct-semantic-context-ablation-score-v1/DOC-0252/gold-r3-rescore",
            "direct-semantic-context-ablation-score-gold-r3.v1.json", new
            {
                artifactKind = "a99_direct_semantic_context_ablation_score_gold_r3_rescore",
                schemaVersion = "a99-direct-semantic-context-ablation-score-gold-r3-rescore-v1",
                documentId = "DOC-0252",
                status = "RESCORED_OFFLINE_UNDER_GOLD_R3",
                supersedesForCurrentConclusion = "eval/a99-closed-loop/direct-semantic-context-ablation-score-v1/DOC-0252/gold-r2-rescore/direct-semantic-context-ablation-score-gold-r2.v1.json",
                note = "That R2 rescore remains a correct, immutable description of what was scored under Gold R2. It is not edited. This is the CURRENT conclusion under Gold R3.",
                goldSha256Old = GoldR2Sha256,
                goldSha256New = GoldR3Sha256,
                onlyChangedInput = "ITEM-CCE2C592 (Agenda) expected label: STRUCTURAL_UNIT (R2) -> DOCUMENT_LABEL (R3). Every raw returned value in every cell is unchanged.",
                modelCalls = 0,
                providerCalls = 0,
                arms = armScores,
            });
    }

    private static object ScoreQwenArm(string armId, string root)
    {
        var byItem = ReadQwenArmDecisions(root);
        return ScoreArm(armId, byItem);
    }

    // ---------------------------------------------------------------------------------------
    // Haiku context ablation (FULL / LOCAL) + MINIMAL baseline + FULL_STRUCTURED_CONTEXT_V2
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Freeze_haiku_context_ablation_score_gold_r3()
    {
        var full = ReadHaikuArmFromAblationFile("FULL_CONTEXT");
        var local = ReadHaikuArmFromAblationFile("LOCAL_CONTEXT_RADIUS_3");
        var minimal = ReadHaikuMinimalBlind();
        var structuredV2 = ReadHaikuStructuredV2();

        var armScores = new[]
        {
            ScoreArm("FULL_CONTEXT", full),
            ScoreArm("LOCAL_CONTEXT_RADIUS_3", local),
            ScoreArm("MINIMAL_STRUCTURAL_CONTEXT_V1", minimal),
            ScoreArm("FULL_STRUCTURED_CONTEXT_V2", structuredV2),
        };

        FreezeArtifact.AssertJson("eval/a99-closed-loop/haiku-context-ablation-confirmation-v1/DOC-0252/gold-r3-rescore",
            "haiku-context-ablation-score-gold-r3.v1.json", new
            {
                artifactKind = "a99_haiku_context_ablation_confirmation_score_gold_r3_rescore",
                schemaVersion = "a99-haiku-context-ablation-confirmation-score-gold-r3-rescore-v1",
                documentId = "DOC-0252",
                model = "Haiku",
                status = "RESCORED_OFFLINE_UNDER_GOLD_R3",
                supersedesForCurrentConclusion = "eval/a99-closed-loop/haiku-context-ablation-confirmation-v1/DOC-0252/haiku-context-ablation-score.v1.json",
                goldSha256Old = GoldR2Sha256,
                goldSha256New = GoldR3Sha256,
                onlyChangedInput = "ITEM-CCE2C592 (Agenda) expected label: STRUCTURAL_UNIT (R2) -> DOCUMENT_LABEL (R3). Every raw returned value in every cell is unchanged.",
                modelCalls = 0,
                providerCalls = 0,
                arms = armScores,
                headline = "Under R3, Haiku never once returns DOCUMENT_LABEL for Agenda across any arm (FULL, LOCAL, MINIMAL, or FULL_STRUCTURED_CONTEXT_V2) - the model that appeared to 'get Agenda right everywhere' under R2 is wrong everywhere under R3. This is the mirror image of Qwen's FULL/LOCAL arms, which happen to already say DOCUMENT_LABEL and become correct.",
            });
    }

    private static Dictionary<string, string[]> ReadHaikuArmFromAblationFile(string armId)
    {
        const string path = "eval/a99-closed-loop/haiku-context-ablation-confirmation-v1/DOC-0252/haiku-context-ablation-decisions.v1.json";
        using var doc = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(path)));
        var repeats = doc.RootElement.GetProperty("arms").EnumerateArray()
            .Where(a => a.GetProperty("arm").GetString() == armId)
            .OrderBy(a => a.GetProperty("repeat").GetInt32()).ToArray();
        Assert.Equal(3, repeats.Length);
        var byItem = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var repeat in repeats)
            foreach (var decision in repeat.GetProperty("decisions").EnumerateArray())
            {
                var id = decision.GetProperty("itemId").GetString()!;
                if (!byItem.TryGetValue(id, out var list)) byItem[id] = list = [];
                list.Add(decision.GetProperty("label").GetString()!);
            }
        return byItem.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal);
    }

    private static Dictionary<string, string[]> ReadHaikuMinimalBlind()
    {
        const string path = "eval/a99-closed-loop/haiku-blind-minimal-semantic-classification-v1/DOC-0252/haiku-blind-decisions.v1.json";
        using var doc = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(path)));
        return doc.RootElement.GetProperty("decisions").EnumerateArray()
            .ToDictionary(d => d.GetProperty("itemId").GetString()!, d => new[] { d.GetProperty("label").GetString()! }, StringComparer.Ordinal);
    }

    private static Dictionary<string, string[]> ReadHaikuStructuredV2()
    {
        const string path = "eval/a99-closed-loop/structured-evidence-context-v1/DOC-0252/execution/haiku/haiku-full-structured-v2-decisions.v1.json";
        using var doc = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(path)));
        var repeats = doc.RootElement.GetProperty("repeats").EnumerateArray()
            .OrderBy(r => r.GetProperty("repeat").GetInt32()).ToArray();
        Assert.Equal(3, repeats.Length);
        var byItem = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var repeat in repeats)
            foreach (var decision in repeat.GetProperty("decisions").EnumerateArray())
            {
                var id = decision.GetProperty("itemId").GetString()!;
                if (!byItem.TryGetValue(id, out var list)) byItem[id] = list = [];
                list.Add(decision.GetProperty("label").GetString()!);
            }
        return byItem.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal);
    }

    private static Dictionary<string, string[]> ReadQwenArmDecisions(string root)
    {
        var byItem = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var repeat in new[] { 1, 2, 3 })
        foreach (var pack in new[] { "PACK_001", "PACK_005", "PACK_006" })
        {
            var path = TestRepository.Path($"{root}/r{repeat}/{pack}.transport-capture.v1.json");
            Assert.True(File.Exists(path), $"missing {path}");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var rootEl = doc.RootElement;
            Assert.Equal("PASS", rootEl.GetProperty("contractStatus").GetString());
            var rawBytes = Convert.FromBase64String(rootEl.GetProperty("rawResponseUtf8Base64").GetString()!);
            using var response = JsonDocument.Parse(rawBytes);
            foreach (var decision in response.RootElement.GetProperty("decisions").EnumerateArray())
            {
                var id = decision.GetProperty("itemId").GetString()!;
                if (!byItem.TryGetValue(id, out var list)) byItem[id] = list = [];
                list.Add(decision.GetProperty("classification").GetString()!);
            }
        }
        return byItem.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // Shared arm scoring (F1 / Agenda / F2 / F3 / structural / documentLabel / confusion)
    // ---------------------------------------------------------------------------------------

    private static object ScoreArm(string armId, Dictionary<string, string[]> byItem)
    {
        Assert.Equal(18, byItem.Count);
        var labels = new[] { "STRUCTURAL_UNIT", "DOCUMENT_LABEL", "NON_STRUCTURAL" };

        (int Correct, int Total, string[] Returned) Family(string itemId)
        {
            var returned = byItem[itemId];
            var expected = GoldR3[itemId];
            return (returned.Count(r => r == expected), returned.Length, returned);
        }

        var f1 = Family(F1ItemId);
        var agenda = Family(AgendaItemId);
        var f2 = Family("ITEM-2D78F82D");
        var f3 = Family("ITEM-968E70AF");

        var structuralItems = GoldR3.Where(kv => kv.Value == "STRUCTURAL_UNIT").Select(kv => kv.Key).ToArray();
        var documentLabelItems = GoldR3.Where(kv => kv.Value == "DOCUMENT_LABEL").Select(kv => kv.Key).ToArray();
        Assert.Equal(13, structuralItems.Length); // 14 under R2, minus Agenda which moved out under R3
        Assert.Equal(3, documentLabelItems.Length); // 2 under R2, plus Agenda which moved in under R3

        int CorrectCount(IEnumerable<string> items) => items.Sum(id => Family(id).Correct);
        int TotalCount(IEnumerable<string> items) => items.Sum(id => Family(id).Total);

        var matrix = labels.ToDictionary(expected => expected,
            expected => labels.ToDictionary(returned => returned,
                returned => GoldR3.Where(kv => kv.Value == expected)
                    .Sum(kv => byItem[kv.Key].Count(r => r == returned)),
                StringComparer.Ordinal),
            StringComparer.Ordinal);
        var totalDecisions = matrix.Values.SelectMany(row => row.Values).Sum();
        var correct = labels.Sum(l => matrix[l][l]);

        return new
        {
            armId,
            F1 = new { itemId = F1ItemId, expected = GoldR3[F1ItemId], correct = f1.Correct, total = f1.Total, returnedByRepeat = f1.Returned },
            Agenda = new { itemId = AgendaItemId, expected = GoldR3[AgendaItemId], correct = agenda.Correct, total = agenda.Total, returnedByRepeat = agenda.Returned },
            F2 = new { itemId = "ITEM-2D78F82D", expected = GoldR3["ITEM-2D78F82D"], correct = f2.Correct, total = f2.Total },
            F3 = new { itemId = "ITEM-968E70AF", expected = GoldR3["ITEM-968E70AF"], correct = f3.Correct, total = f3.Total },
            structuralControls = new { correct = CorrectCount(structuralItems), total = TotalCount(structuralItems), itemCount = structuralItems.Length },
            documentLabelControl = new { correct = CorrectCount(documentLabelItems), total = TotalCount(documentLabelItems), itemCount = documentLabelItems.Length, items = documentLabelItems.OrderBy(x => x, StringComparer.Ordinal).ToArray() },
            confusionMatrix = new { expectedActual = matrix, totalDecisions, correct, accuracy = (double)correct / totalDecisions },
        };
    }

    // ---------------------------------------------------------------------------------------
    // Direct semantic probe (same raw responses as the FULL_CONTEXT arm - its origin)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Freeze_direct_probe_score_gold_r3()
    {
        var full = ScoreQwenArm("FULL_CONTEXT", "eval/a99-closed-loop/direct-semantic-context-ablation-v1/DOC-0252/full-context");
        FreezeArtifact.AssertJson("eval/a99-closed-loop/direct-semantic-discrimination-probe-score-v1/DOC-0252/gold-r3-rescore",
            "direct-semantic-probe-score-gold-r3.v1.json", new
            {
                artifactKind = "a99_direct_semantic_probe_score_gold_r3_rescore",
                schemaVersion = "a99-direct-semantic-probe-score-gold-r3-rescore-v1",
                documentId = "DOC-0252",
                status = "RESCORED_OFFLINE_UNDER_GOLD_R3",
                note = "This probe's raw responses are the FULL_CONTEXT arm's origin - identical raw data, so this rescore is that arm's rescore, referenced rather than re-derived independently.",
                goldSha256Old = GoldR2Sha256,
                goldSha256New = GoldR3Sha256,
                modelCalls = 0,
                providerCalls = 0,
                fullContextArmScore = full,
                classificationDerivation = "F1 (3/3) and Agenda (3/3, now that Agenda's expected label is DOCUMENT_LABEL and this probe's raw answer for it was always DOCUMENT_LABEL) are both fully correct under R3, and F2/F3 remain 3/3 - every named family and every structural control is now correct. No discrimination limit remains in this probe under Gold R3.",
                directProbeClassification = "NO_DISCRIMINATION_LIMIT_OBSERVED_UNDER_GOLD_R3",
                previousClassification = "DIRECT_SEMANTIC_DISCRIMINATION_LIMIT_OBSERVED (Gold R2) - SUPERSEDED_BY_GOLD_R3: the remaining Agenda 'limit' the R2 rescore reported was itself a Gold R2 defect, not a model limit, exactly as the R1->R2 correction had already established for F1.",
            });
    }

    // ---------------------------------------------------------------------------------------
    // Context-topology-disagreement-signal: rescore ARM_MODAL_ERROR / ANY_REPEAT_ERROR only.
    // Signal construction (TOPOLOGY_DISAGREEMENT / REPEAT_INSTABILITY / CONTEXT_SENSITIVITY) is
    // Gold-free and unchanged - re-verified below, not retuned.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Freeze_context_topology_disagreement_rescore_gold_r3()
    {
        var qwenFull = ReadQwenArmDecisions("eval/a99-closed-loop/direct-semantic-context-ablation-v1/DOC-0252/full-context");
        var qwenV2 = ReadQwenArmDecisions("eval/a99-closed-loop/structured-evidence-context-v1/DOC-0252/execution/qwen/full-structured-context-v2");
        var qwenMinimal = ReadQwenArmDecisions("eval/a99-closed-loop/direct-semantic-context-ablation-v1/DOC-0252/minimal-structural-context-v1");
        var haikuFull = ReadHaikuArmFromAblationFile("FULL_CONTEXT");
        var haikuV2 = ReadHaikuStructuredV2();
        var haikuMinimal = ReadHaikuMinimalBlind();

        object RescoreModel(string model, Dictionary<string, string[]> full, Dictionary<string, string[]> v2, Dictionary<string, string[]> minimal)
        {
            var results = new[] { F1ItemId, AgendaItemId }.Select(itemId =>
            {
                var arms = new[] { full[itemId], v2[itemId], minimal[itemId] };
                string Modal(string[] labels) => labels.GroupBy(l => l, StringComparer.Ordinal)
                    .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).First().Key;
                var modals = arms.Select(Modal).ToArray();
                var topologyDisagreement = modals.Distinct(StringComparer.Ordinal).Count() > 1;
                var repeatInstability = arms.Any(a => a.Length > 1 && a.Distinct(StringComparer.Ordinal).Count() > 1);
                var contextSensitivity = topologyDisagreement || repeatInstability;
                var goldR3 = GoldR3[itemId];
                var armModalError = modals.Any(m => m != goldR3);
                var anyRepeatError = arms.Any(a => a.Any(l => l != goldR3));
                return new { itemId, modals, topologyDisagreement, repeatInstability, contextSensitivity, goldR3, armModalError, anyRepeatError };
            }).ToArray();

            return new { model, items = results };
        }

        var qwen = RescoreModel("qwen/qwen3.7-flash", qwenFull, qwenV2, qwenMinimal);
        var haiku = RescoreModel("Haiku", haikuFull, haikuV2, haikuMinimal);

        FreezeArtifact.AssertJson("eval/a99-closed-loop/context-topology-disagreement-signal-v1/DOC-0252/gold-r3-rescore",
            "context-topology-disagreement-rescore-gold-r3.v1.json", new
            {
                artifactKind = "a99_context_topology_disagreement_rescore_gold_r3",
                schemaVersion = "a99-context-topology-disagreement-rescore-gold-r3-v1",
                status = "RESCORED_OFFLINE_UNDER_GOLD_R3",
                signalConstructionChanged = false,
                note = "TOPOLOGY_DISAGREEMENT, REPEAT_INSTABILITY and CONTEXT_SENSITIVITY are Gold-free and identical to the frozen R2-era signal (per-model-item-matrix.v1.json) - only ARM_MODAL_ERROR and ANY_REPEAT_ERROR, which depend on Gold, are recomputed here for the two items Gold R3 could affect (F1 unaffected, Agenda changed).",
                goldSha256Old = GoldR2Sha256,
                goldSha256New = GoldR3Sha256,
                modelCalls = 0,
                providerCalls = 0,
                qwen,
                haiku,
                headline = "For Qwen, Agenda was already both ARM_MODAL_ERROR and ANY_REPEAT_ERROR under R2 (FULL's modal disagreed with the R2 label) and remains both under R3 (MINIMAL and V2's modals now disagree with the R3 label instead) - CONTEXT_SENSITIVITY still catches it, for a different underlying reason. For Haiku, Agenda's ARM_MODAL_ERROR flips from false (R2: all three arms' modal STRUCTURAL_UNIT matched R2's expectation) to true (R3: the same STRUCTURAL_UNIT modals now mismatch) - the earlier CONTEXT_SENSITIVITY-vs-ARM_MODAL_ERROR false positive for Haiku's Agenda disappears entirely under R3, since it is now a true positive.",
            });
    }

    // ---------------------------------------------------------------------------------------
    // Selective escalation: text adjudicator + VLM rescore
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Freeze_selective_escalation_rescore_gold_r3()
    {
        var adjudicatorLabel = ReadFinalLabel(
            "eval/a99-closed-loop/selective-semantic-escalation-v1/DOC-0252/adjudication/ITEM-CCE2C592.transport-capture.v1.json",
            "finalLabel");
        var vlmLabel = ReadFinalLabel(
            "eval/a99-closed-loop/selective-semantic-escalation-v1/DOC-0252/vlm/ITEM-CCE2C592.transport-capture.v1.json",
            "legacyProjectedLabel");

        var goldR3 = GoldR3[AgendaItemId];
        var adjudicatorCorrect = string.Equals(adjudicatorLabel, goldR3, StringComparison.Ordinal);
        var vlmCorrect = string.Equals(vlmLabel, goldR3, StringComparison.Ordinal);

        // Gold-free evidence-conflict signal is unchanged: same pooled tally, same dominant label,
        // computed without Gold - only its correctness relative to Gold flips.
        const string dominantPooledLabel = "STRUCTURAL_UNIT";

        FreezeArtifact.AssertJson("eval/a99-closed-loop/selective-semantic-escalation-v1/DOC-0252/gold-r3-rescore",
            "selective-escalation-score-gold-r3.v1.json", new
            {
                artifactKind = "a99_selective_semantic_escalation_score_gold_r3_rescore",
                schemaVersion = "a99-selective-semantic-escalation-score-gold-r3-rescore-v1",
                itemId = AgendaItemId,
                status = "RESCORED_OFFLINE_UNDER_GOLD_R3",
                goldSha256Old = GoldR2Sha256,
                goldSha256New = GoldR3Sha256,
                modelCalls = 0,
                providerCalls = 0,
                vlmCalls = 0,
                textAdjudicator = new
                {
                    finalLabel = adjudicatorLabel,
                    resolvedSelfReport = true,
                    goldR3,
                    correctUnderR3 = adjudicatorCorrect,
                    classification = adjudicatorCorrect ? "RESOLVED_CORRECT_UNDER_GOLD_R3" : "RESOLVED_INCORRECT_UNDER_GOLD_R3",
                    previousClassification = "RESOLVED_INCORRECT (Gold R2) - SUPERSEDED_BY_GOLD_R3",
                },
                vlm = new
                {
                    legacyProjectedLabel = vlmLabel,
                    goldR3,
                    correctUnderR3 = vlmCorrect,
                    classification = vlmCorrect ? "VLM_IDENTITY_CORRECT_UNDER_GOLD_R3" : "VLM_IDENTITY_INCORRECT_UNDER_GOLD_R3",
                    previousClassification = "VLM_ESCALATION_INCONCLUSIVE_SAME_ATTRACTOR (Gold R2) - SUPERSEDED_BY_GOLD_R3",
                },
                adjudicatorEvidenceConflict = new
                {
                    stillTrue = true,
                    dominantPooledLabel,
                    adjudicatorLabel,
                    conflict = !string.Equals(adjudicatorLabel, dominantPooledLabel, StringComparison.Ordinal),
                    interpretationUnderR3 = "The adjudicator is correct under R3 despite disagreeing with the dominant pooled vote. ADJUDICATOR_EVIDENCE_CONFLICT remains an escalation/disagreement signal, not evidence that the adjudicator is wrong - the dominant pooled label (STRUCTURAL_UNIT) is itself wrong under the human-approved Gold. This is the concrete negative example for MAJORITY != SEMANTIC AUTHORITY: the pooled 11-4-1 majority disagreed with human-approved semantic authority.",
                },
                dominantLabelAuthority = false,
                contextSignalDefinitionChanged = false,
            });
    }

    private static string ReadFinalLabel(string path, string property)
    {
        var full = TestRepository.Path(path);
        Assert.True(File.Exists(full), $"missing {full}");
        using var doc = JsonDocument.Parse(File.ReadAllText(full));
        Assert.Equal("PASS", doc.RootElement.GetProperty("contractStatus").GetString());
        return doc.RootElement.GetProperty(property).GetString()!;
    }
}
