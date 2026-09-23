using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Synthesizes the cross-model, cross-modal picture for ITEM-CCE2C592 ("Agenda") from evidence
/// already frozen in this lineage. Zero calls - no new model, provider, or VLM call is made here or
/// recommended by this file; adding another model at this point would only produce another vote, not
/// evidence that resolves anything a vote cannot resolve.
/// <para>
/// The finding this file locks in: visual evidence did not resolve the Qwen-vs-Haiku disagreement -
/// it reproduced it. Each model is internally consistent across modality (Qwen: IDENTITY in both
/// text and vision; Haiku: STRUCTURE in both text and vision), which reframes this from "vision fixes
/// a text-rendering ambiguity" to "a real semantic-boundary disagreement that survives modality
/// change." Both readings of the same layout are semantically defensible - Qwen reads the artifact
/// boundary, Haiku reads the parent-heading/divider role - which is itself evidence the ontology's
/// three legacy classes may not be mutually exclusive at the occurrence level for this kind of item.
/// </para>
/// </summary>
public sealed class Doc0252AgendaCrossModelCrossModalSynthesisTests
{
    private const string Root = "eval/a99-closed-loop/gold-r3-rescore-v1/DOC-0252";
    private const string GoldR3Sha256 = "a26b83e14005a9a3e4957cd7a027155e97a534a73fd737551726d18a1c695654";

    [Fact]
    public void Freeze_cross_model_cross_modal_synthesis()
    {
        var haikuVlm = ReadHaikuVlm();
        Assert.Equal("STRUCTURE", haikuVlm.SemanticFunction);

        FreezeArtifact.AssertJson(Root, "agenda-cross-model-cross-modal-synthesis.v1.json", new
        {
            artifactKind = "a99_doc0252_agenda_cross_model_cross_modal_synthesis",
            schemaVersion = "a99-doc0252-agenda-cross-model-cross-modal-synthesis-v1",
            documentId = "DOC-0252",
            itemId = "ITEM-CCE2C592",
            status = "SYNTHESIZED_OFFLINE_NO_NEW_CALLS",
            modelCalls = 0,
            providerCalls = 0,
            vlmCalls = 0,
            recommendationAgainstFurtherModelCalls = "Adding another model here would produce another vote, not evidence that resolves what a vote cannot resolve. The next-value step is examining the ontology assumption (mutual exclusivity), not gathering more model opinions.",

            evidenceMatrix = new object[]
            {
                new { model = "Qwen", modality = "text (FULL/LOCAL context ablation)", result = "IDENTITY", alignsWithR3 = true },
                new { model = "Qwen", modality = "vision (VLM escalation)", result = "IDENTITY", alignsWithR3 = true },
                new { model = "Qwen", modality = "text adjudicator (3-view synthesis)", result = "IDENTITY", alignsWithR3 = true },
                new { model = "Haiku", modality = "text (FULL/LOCAL/MINIMAL/FULL_STRUCTURED_CONTEXT_V2)", result = "STRUCTURE", alignsWithR3 = false },
                new { model = "Haiku", modality = "vision (independent blind adjudication)", result = "STRUCTURE", alignsWithR3 = false },
                new { model = "Human review", modality = "textual, structural, and visual evidence combined", result = "IDENTITY", alignsWithR3 = true },
            },

            crossModelResult = "QWEN_IDENTITY_VS_HAIKU_STRUCTURE",
            crossModalConsistencyWithinModel = "SUPPORTED",
            crossModalConsistencyDetail = "Qwen is IDENTITY in both text and vision; Haiku is STRUCTURE in both text and vision. Neither model's answer for this item is modality-dependent - each model brings a stable interpretive frame across text and vision, not a per-modality guess.",
            visualEvidenceResolvesBoundary = "NOT_SUPPORTED",
            visualEvidenceResolvesBoundaryDetail = "The VLM escalation's original premise was that visual/layout evidence, unavailable to a flat text rendering, might disambiguate this case. Haiku's independent, blind, balanced vision judgment shows the opposite: given the same real layout, it reaches the same STRUCTURE conclusion its text arms already reached. The ambiguity is not primarily a text-rendering artifact - it is a semantic-boundary disagreement that survives the modality change this escalation tested.",
            modelSpecificFrameVsModalityError = "MODEL_SPECIFIC_SEMANTIC_FRAME",
            modelSpecificFrameDetail = "The data supports 'each model brings a consistent interpretive frame to this item' over 'vision vs. text is the source of the disagreement.' Qwen's frame treats the occurrence as identifying an embedded artifact (the Agenda-as-subdocument reading); Haiku's frame treats it as the divider/parent-heading that opens the schedule content beneath it (the Agenda-as-organizing-heading reading). Both are semantically coherent readings of the same real layout.",

            singlePrimaryFunctionAmbiguity = "SUPPORTED_NEEDS_ONTOLOGY_REVIEW",
            ontologyProposal = new
            {
                observation = "Forcing a single primary semantic function may be the wrong contract for this class of occurrence. 'Agenda' plausibly carries two simultaneous roles: it identifies the embedded artifact ('this sub-document is the Agenda') AND it organizes where the subsequent content belongs ('the schedule starts here; DAY/SESSION nest beneath it'). Qwen's frame foregrounds the first role; Haiku's frame foregrounds the second. Both readings have real evidentiary support in the same layout.",
                proposedRepresentation = new
                {
                    conceptual = "semanticFunctions = { IDENTITY, STRUCTURE }, primary = IDENTITY",
                    legacyProjectionUnaffected = "The 3-class legacy contract (STRUCTURAL_UNIT / DOCUMENT_LABEL / NON_STRUCTURAL) would still be satisfiable by projecting the declared primary function - DOCUMENT_LABEL here - so this proposal does not require changing the existing production output contract to be adopted.",
                },
                status = "PROPOSED_NOT_IMPLEMENTED",
                note = "This is a proposal for future ontology review, not a change made in this commit. No occurrence-gold schema, scorer, or production contract is modified here. Implementing multi-function representation would be its own separate task with its own review.",
            },

            interpretation = "Gold R3 (IDENTITY) remains the current human-approved semantic authority for this item and is not moved by this finding - Haiku's STRUCTURE reading, however well-evidenced, does not become an alternative authority. What this synthesis adds is a sharper characterization of the disagreement itself: it is real, cross-modal, and plausibly reflects that the item carries two coherent semantic roles the current single-label ontology cannot represent simultaneously, rather than reflecting an error in either model or a resolvable rendering artifact.",
            doesNotEstablish = new[]
            {
                "That Haiku is 'wrong' in a strong research sense - its reading is evidenced and internally consistent, just not the one human review selected as primary.",
                "That the ontology must be changed - the proposal is a hypothesis for review, not an adopted design.",
                "Anything about cross-document generalization: this is one item on one document.",
            },
            goldSha256 = GoldR3Sha256,
            crossDocumentGeneralization = false,
            generalizationEstablished = false,
            scope = "DOC-0252 only, ITEM-CCE2C592. Not evidence about the remaining corpus.",
        });
    }

    private static (string SemanticFunction, bool Resolved) ReadHaikuVlm()
    {
        var path = TestRepository.Path(
            "eval/a99-closed-loop/haiku-vlm-agenda-independent-adjudication-v1/DOC-0252/haiku-vlm-decision.v1.json");
        Assert.True(File.Exists(path), $"missing {path}");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var raw = doc.RootElement.GetProperty("rawResponse");
        return (raw.GetProperty("semanticFunction").GetString()!, raw.GetProperty("resolved").GetBoolean());
    }
}
