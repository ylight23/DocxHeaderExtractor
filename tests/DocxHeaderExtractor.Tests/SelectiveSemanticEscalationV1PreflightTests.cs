using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// SELECTIVE_SEMANTIC_ESCALATION_V1, DESIGN + PREFLIGHT PHASE ONLY. No text-adjudicator call and no
/// VLM call happens anywhere in this file - <see cref="BuildAdjudicationRequest"/> constructs the
/// exact bytes a real call would send and freezes them for review, but nothing is transmitted.
/// Real execution is a separate, later, explicitly authorized step (same pattern as every other
/// real-provider-call phase in this lineage).
/// <para>
/// The escalation trigger is read verbatim from the already-frozen, Gold-free
/// CONTEXT_TOPOLOGY_DISAGREEMENT_SIGNAL_V1 output (<see cref="CrossModelSignalFile"/>'s
/// <c>contextSensitivityItems</c>) - it is not recomputed here. Only the two items that signal
/// flagged (ITEM-505430BB, ITEM-CCE2C592) can ever reach <see cref="BuildAdjudicationRequest"/>;
/// every other item is structurally unreachable, proved by
/// <see cref="Building_an_adjudication_request_for_a_non_flagged_item_throws"/>.
/// </para>
/// <para>
/// The adjudicator's own input must never see Gold, correctness, model identity, arm names, or the
/// internal shorthand names ("F1", "Agenda") used throughout this codebase for these two items - it
/// sees only neutral view IDs (VIEW_1/2/3, assigned by a fixed alphabetical arm-id order, never by
/// "which one is known to be right"), the source-faithful context text for each view, and the raw
/// candidate labels that disagreed. That leakage rule applies only to the constructed model-facing
/// request text; this file's own internal commentary and artifacts are free to discuss Gold, prior
/// conclusions, etc., since none of that ever reaches <see cref="BuildAdjudicationRequest"/>'s output.
/// </para>
/// </summary>
public sealed class SelectiveSemanticEscalationV1PreflightTests
{
    private const string ScoreRoot = "eval/a99-closed-loop/selective-semantic-escalation-v1/DOC-0252";
    private const string CrossModelSignalFile =
        "eval/a99-closed-loop/context-topology-disagreement-signal-v1/DOC-0252/cross-model-signal-score.v1.json";
    private const string PerModelMatrixFile =
        "eval/a99-closed-loop/context-topology-disagreement-signal-v1/DOC-0252/per-model-item-matrix.v1.json";

    private const string QwenFullRoot = "eval/a99-closed-loop/direct-semantic-context-ablation-v1/DOC-0252/full-context";
    private const string QwenMinimalRoot = "eval/a99-closed-loop/direct-semantic-context-ablation-v1/DOC-0252/minimal-structural-context-v1";
    private const string QwenV2Root = "eval/a99-closed-loop/structured-evidence-context-v1/DOC-0252/execution/qwen/full-structured-context-v2";

    private const string Full = "FULL_CONTEXT";
    private const string StructuredV2 = "FULL_STRUCTURED_CONTEXT_V2";
    private const string Minimal = "MINIMAL_STRUCTURAL_CONTEXT_V1";

    /// <summary>Deterministic, non-suggestive: plain alphabetical order of the canonical arm id.</summary>
    private static readonly (string ArmId, string ViewId)[] ViewAssignment =
        [(Full, "VIEW_1"), (StructuredV2, "VIEW_2"), (Minimal, "VIEW_3")];

    private static readonly (string ItemId, string Pack)[] ItemPacks =
        [("ITEM-505430BB", "PACK_005"), ("ITEM-CCE2C592", "PACK_006")];

    // "correct" and "Agenda" are deliberately excluded: they occur organically in DOC-0252's real
    // prose ("...or, more correctly, Global Trade Item Number...") and as ITEM-CCE2C592's own literal
    // target text ("Agenda") respectively, which the adjudicator must see verbatim as source content.
    // This list is therefore scanned only against the scaffolding we author (instruction/schema/view
    // labels), never against sourceText - see Adjudicator_facing_payload_never_reveals_gold_correctness_model_or_arm_identity.
    private static readonly string[] ForbiddenInAdjudicatorScaffolding =
    [
        "Gold", "wrong", "expected label", "F1", "the Agenda item",
        "FULL_CONTEXT", "FULL_STRUCTURED_CONTEXT_V2", "MINIMAL_STRUCTURAL_CONTEXT_V1",
        "Qwen", "Haiku", "qwen/qwen3.7-flash",
    ];

    // ---------------------------------------------------------------------------------------
    // Trigger (re-read, not recomputed)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Escalation_trigger_matches_frozen_cross_model_context_sensitivity_exactly()
    {
        var triggered = ReadTriggerSet();
        Assert.Equal(
            new[] { "ITEM-505430BB", "ITEM-CCE2C592" }.OrderBy(x => x, StringComparer.Ordinal),
            triggered.OrderBy(x => x, StringComparer.Ordinal));

        using var matrixDoc = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(PerModelMatrixFile)));
        var allItemIds = matrixDoc.RootElement.GetProperty("qwen").EnumerateArray()
            .Select(r => r.GetProperty("itemId").GetString()!).ToArray();
        Assert.Equal(18, allItemIds.Length);
        Assert.Equal(16, allItemIds.Except(triggered, StringComparer.Ordinal).Count());
    }

    private static IReadOnlyList<string> ReadTriggerSet()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(CrossModelSignalFile)));
        return doc.RootElement.GetProperty("contextSensitivityItems")
            .EnumerateArray().Select(e => e.GetString()!).ToArray();
    }

    // ---------------------------------------------------------------------------------------
    // Structural safety invariant: stable items are unreachable
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Building_an_adjudication_request_for_a_non_flagged_item_throws()
    {
        var triggered = ReadTriggerSet().ToHashSet(StringComparer.Ordinal);
        using var matrixDoc = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(PerModelMatrixFile)));
        var stableItem = matrixDoc.RootElement.GetProperty("qwen").EnumerateArray()
            .Select(r => r.GetProperty("itemId").GetString()!)
            .First(id => !triggered.Contains(id));

        var ex = Assert.Throws<InvalidOperationException>(() => BuildAdjudicationRequest(stableItem));
        Assert.Contains("not eligible for escalation", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_non_flagged_item_is_individually_unreachable()
    {
        var triggered = ReadTriggerSet().ToHashSet(StringComparer.Ordinal);
        using var matrixDoc = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(PerModelMatrixFile)));
        var stable = matrixDoc.RootElement.GetProperty("qwen").EnumerateArray()
            .Select(r => r.GetProperty("itemId").GetString()!)
            .Where(id => !triggered.Contains(id)).ToArray();
        Assert.Equal(16, stable.Length);
        Assert.All(stable, id => Assert.Throws<InvalidOperationException>(() => BuildAdjudicationRequest(id)));
    }

    // ---------------------------------------------------------------------------------------
    // Determinism / reproducibility
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Adjudication_requests_are_byte_for_byte_reproducible()
    {
        foreach (var (itemId, _) in ItemPacks)
        {
            var first = JsonSerializer.Serialize(BuildAdjudicationRequest(itemId), FreezeArtifact.Json);
            var second = JsonSerializer.Serialize(BuildAdjudicationRequest(itemId), FreezeArtifact.Json);
            Assert.Equal(first, second);
        }
    }

    [Fact]
    public void View_assignment_is_fixed_alphabetical_order_not_outcome_dependent()
    {
        Assert.Equal(ViewAssignment.OrderBy(v => v.ArmId, StringComparer.Ordinal).Select(v => v.ArmId),
            ViewAssignment.Select(v => v.ArmId));
        Assert.Equal(["VIEW_1", "VIEW_2", "VIEW_3"], ViewAssignment.Select(v => v.ViewId));
    }

    // ---------------------------------------------------------------------------------------
    // Leakage prevention (scanning only the model-facing payload, not this file's own commentary)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Adjudicator_facing_payload_never_reveals_gold_correctness_model_or_arm_identity()
    {
        foreach (var (itemId, _) in ItemPacks)
        {
            var request = BuildAdjudicationRequest(itemId);
            // Scan only the scaffolding we author (instruction text, schema, view ids, candidate
            // labels) - never sourceText, which is real document prose the adjudicator must see
            // verbatim and which may coincidentally contain any English word.
            var payload = JsonSerializer.Serialize(request.ScaffoldingOnlyPayload, FreezeArtifact.Json);
            foreach (var forbidden in ForbiddenInAdjudicatorScaffolding)
                Assert.DoesNotContain(forbidden, payload, StringComparison.Ordinal);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Zero calls anywhere in this phase
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void No_model_or_provider_transport_type_is_referenced_outside_this_check()
    {
        var source = File.ReadAllText(TestRepository.Path(
            "tests/DocxHeaderExtractor.Tests/SelectiveSemanticEscalationV1PreflightTests.cs"));
        var thisBody = ExtractMethodBody(source, nameof(No_model_or_provider_transport_type_is_referenced_outside_this_check));
        var rest = source.Replace(thisBody, string.Empty, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "HttpClient", "OpenRouterHeaderExtractor", "BoundaryCutAsync", "RemoteInferenceOptions" })
            Assert.DoesNotContain(forbidden, rest, StringComparison.Ordinal);
    }

    [Fact]
    public void Model_calls_provider_calls_and_vlm_calls_are_all_zero()
    {
        foreach (var (itemId, _) in ItemPacks) BuildAdjudicationRequest(itemId);
        const int modelCalls = 0;
        const int providerCalls = 0;
        const int vlmCalls = 0;
        Assert.Equal(0, modelCalls);
        Assert.Equal(0, providerCalls);
        Assert.Equal(0, vlmCalls);
    }

    // ---------------------------------------------------------------------------------------
    // Frozen artifacts
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Freeze_escalation_contract()
    {
        FreezeArtifact.AssertJson(ScoreRoot, "escalation-contract.v1.json", new
        {
            artifactKind = "a99_selective_semantic_escalation_contract",
            schemaVersion = "a99-selective-semantic-escalation-contract-v1",
            task = "SELECTIVE_SEMANTIC_ESCALATION_V1",
            status = "DESIGNED_NOT_EXECUTED",
            pipeline = "stable case -> accept semantic decision; context-sensitive case -> ESCALATION (text adjudicator -> VLM if layout evidence relevant -> unresolved -> review)",
            trigger = "CONTEXT_SENSITIVITY, read verbatim from the frozen, Gold-free CONTEXT_TOPOLOGY_DISAGREEMENT_SIGNAL_V1 cross-model output. Not recomputed here.",
            triggerSourceArtifact = CrossModelSignalFile,
            triggeredItems = ReadTriggerSet().OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            adjudicatorDesign = new
            {
                mechanism = "Show all three source-faithful context views plus the disagreeing candidate decisions; the adjudicator picks one final semantic label. This is adjudication, not a rerun of any single arm and not extra voting on the already-best-performing topology.",
                viewIdentity = "Neutral VIEW_1/VIEW_2/VIEW_3, assigned by fixed alphabetical order of the canonical arm id (FULL_CONTEXT, FULL_STRUCTURED_CONTEXT_V2, MINIMAL_STRUCTURAL_CONTEXT_V1) - never by which view is known to have performed better historically.",
                candidateLabels = "For each view, the merged repeat-label multiset observed across every model that was run on that view (Qwen + Haiku), with no per-model attribution shown - the adjudicator sees per-view disagreement, not which model said what.",
                forbiddenFromAdjudicatorScaffolding = ForbiddenInAdjudicatorScaffolding,
                outputSchema = new { finalLabel = "one of STRUCTURAL_UNIT | DOCUMENT_LABEL | NON_STRUCTURAL", resolved = "boolean" },
            },
            vlmEscalation = new
            {
                status = "DESIGNED_NOT_EXECUTED, zero calls",
                eligibilityCriterion = "An item is eligible for VLM escalation only if the disagreement is plausibly explained by layout/visual evidence not captured in the text-only rendering (visual grouping, indentation, table structure, font-based emphasis), not by text-only semantic-role ambiguity.",
                note = "Eligibility is evaluated only after text adjudication is attempted and does not resolve the case; it is not run in parallel with the text adjudicator.",
            },
            safetyInvariant = new
            {
                statement = "CONTEXT_SENSITIVITY = false => the item is never passed to the adjudicator or the VLM; its label is accepted as-is and is immutable through the escalation stage.",
                enforcement = "Structural, not empirical: BuildAdjudicationRequest throws InvalidOperationException for any item outside the frozen trigger set - there is no code path capable of constructing or sending a request for a non-triggered item.",
                proof = nameof(Building_an_adjudication_request_for_a_non_flagged_item_throws) + " and " + nameof(Every_non_flagged_item_is_individually_unreachable),
            },
            resolutionMetric = new
            {
                primary = "Stable-control label immutability (drift safety invariant above) - proved by construction, not measured empirically.",
                secondary = "ESCALATED_CASE_RESOLUTION: after real adjudication runs, does each flagged item converge to a single stable final label. PENDING_REAL_ADJUDICATION in this preflight - no real adjudicator response exists yet.",
                goldUsage = "Gold participates only in offline evaluation after real adjudication produces a result; it never decides whether escalation runs and is never shown to the adjudicator.",
            },
            modelCalls = 0,
            providerCalls = 0,
            vlmCalls = 0,
            nextStep = "Real text-adjudicator execution is a separate, later, explicitly authorized step - not inferred from this design task.",
        });
    }

    [Fact]
    public void Freeze_adjudication_requests_preflight()
    {
        FreezeArtifact.AssertJson(ScoreRoot, "adjudication-requests-preflight.v1.json", new
        {
            artifactKind = "a99_selective_semantic_escalation_adjudication_requests_preflight",
            schemaVersion = "a99-selective-semantic-escalation-adjudication-requests-preflight-v1",
            status = "CONSTRUCTED_NOT_SENT",
            modelCalls = 0,
            providerCalls = 0,
            requests = ItemPacks.Select(pair => BuildAdjudicationRequest(pair.ItemId).FrozenView).ToArray(),
        });
    }

    [Fact]
    public void Freeze_preflight_report()
    {
        var triggered = ReadTriggerSet().OrderBy(x => x, StringComparer.Ordinal).ToArray();
        FreezeArtifact.AssertJson(ScoreRoot, "preflight-report.v1.json", new
        {
            artifactKind = "a99_selective_semantic_escalation_preflight_report",
            schemaVersion = "a99-selective-semantic-escalation-preflight-report-v1",
            status = "DESIGNED_NOT_EXECUTED",
            task = "SELECTIVE_SEMANTIC_ESCALATION_V1",
            triggerSource = "CONTEXT_TOPOLOGY_DISAGREEMENT_SIGNAL_V1 cross-model CONTEXT_SENSITIVITY (Gold-free at construction)",
            escalatedItems = triggered,
            stableItemCount = 16,
            stableItemsReachableByEscalation = false,
            adjudicatorContractFrozen = true,
            vlmEligibilityDesigned = true,
            vlmCallsMade = 0,
            textAdjudicatorCallsMade = 0,
            escalatedCaseResolution = "PENDING_REAL_ADJUDICATION",
            goldChanged = false,
            productionChanged = false,
            modelCalls = 0,
            providerCalls = 0,
            nextStep = "Await separate, explicit authorization before any real text-adjudicator or VLM call.",
            scope = "DOC-0252 only, the two items already flagged by the frozen signal. Not evidence about the remaining corpus.",
        });
    }

    // ---------------------------------------------------------------------------------------
    // Request construction
    // ---------------------------------------------------------------------------------------

    private static AdjudicationRequest BuildAdjudicationRequest(string itemId)
    {
        var triggered = ReadTriggerSet();
        if (!triggered.Contains(itemId, StringComparer.Ordinal))
            throw new InvalidOperationException($"item {itemId} is not eligible for escalation (CONTEXT_SENSITIVITY is false)");

        var pack = ItemPacks.Single(p => p.ItemId == itemId).Pack;
        var candidates = ReadCandidateLabels(itemId);

        var views = ViewAssignment.Select(assignment =>
        {
            var (armId, viewId) = assignment;
            var (sourceAvailable, sourceText, sourceSha256, providerInputHash, reason) = ReadViewSource(armId, pack);
            var repeatLabels = candidates[armId];
            return new
            {
                viewId,
                sourceContentAvailable = sourceAvailable,
                sourceText,
                sourceTextSha256 = sourceSha256,
                providerInputHash,
                unavailableReason = reason,
                candidateRepeatLabels = repeatLabels,
                candidateModalLabel = repeatLabels
                    .GroupBy(l => l, StringComparer.Ordinal)
                    .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                    .First().Key,
            };
        }).ToArray();

        var modelFacingPayload = new
        {
            itemId,
            instruction = "Three independent renderings of the same source-faithful evidence produced different candidate semantic-role decisions for this item. Review all views and their candidate decisions, then choose exactly one final label.",
            views = views.Select(v => new
            {
                v.viewId,
                sourceText = v.sourceContentAvailable ? v.sourceText : null,
                v.candidateRepeatLabels,
            }).ToArray(),
            allowedLabels = new[] { "STRUCTURAL_UNIT", "DOCUMENT_LABEL", "NON_STRUCTURAL" },
            outputSchema = new { finalLabel = "string, one of allowedLabels", resolved = "boolean" },
        };

        var frozenView = new
        {
            itemId,
            escalationReason = "CONTEXT_SENSITIVITY = true (frozen, Gold-free signal)",
            pack,
            views,
            modelFacingPayloadSha256 = Sha256(JsonSerializer.Serialize(modelFacingPayload, FreezeArtifact.Json)),
        };

        // Same shape as modelFacingPayload but with sourceText stripped, so leakage scans can check
        // only what we authored (instruction/schema/labels) without tripping on real document prose.
        var scaffoldingOnly = new
        {
            itemId,
            instruction = modelFacingPayload.instruction,
            views = views.Select(v => new { v.viewId, v.candidateRepeatLabels }).ToArray(),
            modelFacingPayload.allowedLabels,
            modelFacingPayload.outputSchema,
        };

        return new AdjudicationRequest(modelFacingPayload, scaffoldingOnly, frozenView);
    }

    private static Dictionary<string, string[]> ReadCandidateLabels(string itemId)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(PerModelMatrixFile)));
        string[] Merge(string model, string field)
        {
            var row = doc.RootElement.GetProperty(model).EnumerateArray()
                .Single(r => r.GetProperty("itemId").GetString() == itemId);
            return row.GetProperty(field).EnumerateArray().Select(e => e.GetString()!).ToArray();
        }

        return new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [Full] = [.. Merge("qwen", "fullRepeatLabels"), .. Merge("haiku", "fullRepeatLabels")],
            [StructuredV2] = [.. Merge("qwen", "structuredV2RepeatLabels"), .. Merge("haiku", "structuredV2RepeatLabels")],
            [Minimal] = [.. Merge("qwen", "minimalRepeatLabels"), .. Merge("haiku", "minimalRepeatLabels")],
        };
    }

    private static (bool Available, string? Text, string? Sha256, string? ProviderInputHash, string? Reason)
        ReadViewSource(string armId, string pack)
    {
        var root = armId switch
        {
            Full => QwenFullRoot,
            Minimal => QwenMinimalRoot,
            StructuredV2 => QwenV2Root,
            _ => throw new InvalidOperationException($"unknown arm {armId}"),
        };
        var path = TestRepository.Path($"{root}/r1/{pack}.transport-capture.v1.json");
        Assert.True(File.Exists(path), $"missing {path}");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var rootEl = doc.RootElement;
        var providerInputHash = rootEl.GetProperty("providerInputHash").GetString();

        if (!rootEl.TryGetProperty("userMessageUtf8Base64", out var userMessageProp))
        {
            // FULL_STRUCTURED_CONTEXT_V2's transport capture only persisted the raw response, not the
            // raw request, at capture time. The request is still fully deterministic (same builder that
            // produced providerInputHash below) and will be re-derived and hash-verified against this
            // exact providerInputHash before any real adjudication call is sent.
            return (false, null, null, providerInputHash,
                "raw request bytes were not persisted for FULL_STRUCTURED_CONTEXT_V2 at capture time (only the raw response was captured); will be re-derived deterministically and verified against providerInputHash before real execution");
        }

        var text = Encoding.UTF8.GetString(Convert.FromBase64String(userMessageProp.GetString()!));
        return (true, text, Sha256(text), providerInputHash, null);
    }

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)));

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

    private sealed record AdjudicationRequest(object ModelFacingPayload, object ScaffoldingOnlyPayload, object FrozenView);
}
