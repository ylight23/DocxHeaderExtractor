using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Locks in the epistemic status of Gold R3 and renames this lineage's central abstraction from
/// "hard-case detector" to "semantic instability detector" - a scope correction, not new evidence.
/// Zero calls; every number here is read back from already-frozen R3 rescore artifacts (commit
/// fba2301), not recomputed.
/// <para>
/// The caveat this file exists to record: R3 was a human review performed AFTER seeing Qwen, Haiku,
/// text-adjudicator, and VLM evidence for ITEM-CCE2C592. That makes R3 rescores valid descriptions
/// of "which outputs align with the current semantic authority," but NOT an independent, held-out
/// benchmark of model performance for this corrected item - the models' own outputs were part of
/// what informed the correction. Qwen FULL's 54/54 under R3 is alignment with revised human-approved
/// authority, not a claim of 100% accuracy on an unbiased benchmark.
/// </para>
/// </summary>
public sealed class Doc0252SemanticInstabilityDetectorEpistemicStatusTests
{
    private const string Root = "eval/a99-closed-loop/gold-r3-rescore-v1/DOC-0252";
    private const string GoldR3Sha256 = "a26b83e14005a9a3e4957cd7a027155e97a534a73fd737551726d18a1c695654";

    [Fact]
    public void Freeze_epistemic_status_and_ontology()
    {
        var qwen = ReadQwenArms();
        var haiku = ReadHaikuArms();
        var (adjudicator, vlm) = ReadEscalation();

        FreezeArtifact.AssertJson(Root, "epistemic-status-and-ontology.v1.json", new
        {
            artifactKind = "a99_doc0252_agenda_epistemic_status_and_ontology",
            schemaVersion = "a99-doc0252-agenda-epistemic-status-and-ontology-v1",
            documentId = "DOC-0252",
            modelCalls = 0,
            providerCalls = 0,
            vlmCalls = 0,

            epistemicStatus = new
            {
                doc0252R3 = "CURRENT_SEMANTIC_AUTHORITY",
                agendaDependentHistoricalModelEvidence = "POST_HOC_RESCORED_AGAINST_R3",
                independentPerformanceValidation = "NOT_ESTABLISHED_FOR_THIS_CORRECTED_ITEM",
                reason = "R3 (eval/a99-closed-loop/canonical-semantic-gold-vnext/occurrence/DOC-0252.structured-source-parts.occurrence-gold.v1.json, commit 35e870d) was a human review performed AFTER seeing Qwen, Haiku, text-adjudicator, and VLM evidence for ITEM-CCE2C592 - the review that produced the correction was informed by the very outputs the R3 rescore then measures alignment against. This does not invalidate the correction (it was evidence-informed, human-approved, and explicitly documented as such); it means the resulting rescore numbers are not a held-out benchmark for this item.",
                validStatement = "Qwen FULL outputs align 54/54 with the revised, human-approved R3 authority.",
                invalidStatement = "Qwen achieved 100% on an unbiased benchmark.",
                vlmNote = "VLM -> IDENTITY matching Gold R3 -> IDENTITY is alignment with the current authority, not held-out validation, because the VLM's own evidence was part of what the human review considered when revising the boundary.",
            },

            ontology = new
            {
                abstraction = new[]
                {
                    new { legacyClass = "DOCUMENT_LABEL", semanticFunction = "IDENTITY", meaning = "identifies an artifact/object" },
                    new { legacyClass = "STRUCTURAL_UNIT", semanticFunction = "STRUCTURE", meaning = "organizes content hierarchy" },
                    new { legacyClass = "NON_STRUCTURAL", semanticFunction = "INFORMATION", meaning = "provides descriptive/contextual information" },
                },
                agendaCaseSignificance = "IDENTITY vs STRUCTURE is a real, non-cosmetic distinction, not a rename: ITEM-CCE2C592 ('Agenda') identifies the embedded artifact itself (IDENTITY/DOCUMENT_LABEL), while the DAY 1/SESSION I/SESSION II occurrences beneath it provide that artifact's internal structural hierarchy (STRUCTURE/STRUCTURAL_UNIT). The text adjudicator and VLM both independently converged on IDENTITY because they were recognizing the artifact boundary, not the parent-heading hierarchy Gold R2 had implicitly forced the item into.",
                documentStructureUnderR3 = new
                {
                    embeddedArtifactIdentity = new { text = "Agenda", semanticFunction = "IDENTITY", legacyClass = "DOCUMENT_LABEL" },
                    internalStructure = new[] { "DAY 1", "SESSION I", "SESSION II" }.Select(t => new { text = t, semanticFunction = "STRUCTURE", legacyClass = "STRUCTURAL_UNIT" }).ToArray(),
                },
            },

            contextSensitivityUnderR3 = new
            {
                table = new object[]
                {
                    new { view = "Qwen FULL", result = "IDENTITY", correct = true },
                    new { view = "Qwen LOCAL", result = "IDENTITY", correct = true },
                    new { view = "Qwen MINIMAL", result = "STRUCTURE", correct = false },
                    new { view = "Qwen FULL_STRUCTURED_CONTEXT_V2", result = "MIXED (1/3 IDENTITY, 2/3 STRUCTURE)", correct = (bool?)null },
                    new { view = "Haiku FULL", result = "STRUCTURE", correct = false },
                    new { view = "Haiku LOCAL", result = "STRUCTURE", correct = false },
                    new { view = "Haiku MINIMAL", result = "STRUCTURE", correct = false },
                    new { view = "Haiku FULL_STRUCTURED_CONTEXT_V2", result = "mostly STRUCTURE (0/3 IDENTITY)", correct = false },
                    new { view = "Text adjudicator", result = "IDENTITY", correct = true },
                    new { view = "VLM", result = "IDENTITY", correct = true },
                },
                oldStory = "MINIMAL_STRUCTURAL_CONTEXT_V1 = the context topology that gets Agenda right.",
                newStory = "Context topology changes interpretation, but no single topology is semantic authority - MINIMAL was aligned with the wrong (R2) boundary and is now the outlier under the corrected (R3) one.",
                twoDistinctFailureModes = new
                {
                    qwen = "Topology-sensitive: Agenda's answer changes across FULL/LOCAL/MINIMAL/V2 - a context-composition-driven failure mode.",
                    haiku = "Stable wrong prior: Agenda is STRUCTURE across every arm including FULL_STRUCTURED_CONTEXT_V2's near-total STRUCTURE result - not topology-sensitive at all under R3, just consistently wrong.",
                    implication = "These are not the same phenomenon and should not be treated with the same remediation - topology-sensitivity (Qwen) is plausibly addressed by better context composition or adjudication; a stable wrong prior (Haiku) is not, since no context view will fix it.",
                },
            },

            majorityVoteCounterexample = new
            {
                dominantPooledEvidence = "STRUCTURE (11 STRUCTURAL_UNIT / 4 DOCUMENT_LABEL / 1 NON_STRUCTURAL)",
                humanAuthorityR3 = "IDENTITY",
                textAdjudicator = "IDENTITY",
                vlm = "IDENTITY",
                conclusion = "This is not just the theoretical claim that majority voting isn't authority - it is an empirical counterexample where the pooled majority was actually wrong relative to the human-approved semantic boundary.",
            },

            escalationArchitecture = new
            {
                pipeline = "cross-view/repeat instability -> hard-case signal -> adjudication -> evidence conflict? -> modality escalation / review",
                mustNotBecome = "majority vote -> final label",
                note = "Unchanged by this correction - the architecture already treated the dominant pooled label as a conflict-detection signal only, never as authority, which is exactly what let this counterexample surface instead of being silently overridden.",
            },

            terminologyRevision = new
            {
                from = "hard-case detector",
                to = "semantic instability detector",
                reason = "It detects where a decision is sensitive to evidence framing (context composition, repeat variance) - it does not, and was never designed to, say which view is correct. 'Hard-case' implied the detector was finding cases the model gets wrong; 'semantic instability' correctly describes what CONTEXT_SENSITIVITY actually measures: disagreement across framings, independent of which framing (or Gold vintage) turns out to be right.",
            },

            supportingArtifacts = new
            {
                qwenR3 = qwen,
                haikuR3 = haiku,
                textAdjudicatorR3 = adjudicator,
                vlmR3 = vlm,
            },

            goldSha256 = GoldR3Sha256,
            crossDocumentGeneralization = false,
            generalizationEstablished = false,
            scope = "DOC-0252 only, ITEM-CCE2C592. Not evidence about the remaining corpus, and not an independent performance benchmark for this item.",
        });
    }

    private static object ReadQwenArms()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(
            "eval/a99-closed-loop/direct-semantic-context-ablation-score-v1/DOC-0252/gold-r3-rescore/direct-semantic-context-ablation-score-gold-r3.v1.json")));
        return doc.RootElement.GetProperty("arms").EnumerateArray()
            .Select(a => new
            {
                arm = a.GetProperty("armId").GetString(),
                agendaCorrect = a.GetProperty("Agenda").GetProperty("correct").GetInt32(),
                agendaTotal = a.GetProperty("Agenda").GetProperty("total").GetInt32(),
                accuracy = a.GetProperty("confusionMatrix").GetProperty("accuracy").GetDouble(),
            }).ToArray();
    }

    private static object ReadHaikuArms()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(
            "eval/a99-closed-loop/haiku-context-ablation-confirmation-v1/DOC-0252/gold-r3-rescore/haiku-context-ablation-score-gold-r3.v1.json")));
        return doc.RootElement.GetProperty("arms").EnumerateArray()
            .Select(a => new
            {
                arm = a.GetProperty("armId").GetString(),
                agendaCorrect = a.GetProperty("Agenda").GetProperty("correct").GetInt32(),
                agendaTotal = a.GetProperty("Agenda").GetProperty("total").GetInt32(),
                accuracy = a.GetProperty("confusionMatrix").GetProperty("accuracy").GetDouble(),
            }).ToArray();
    }

    private static (object Adjudicator, object Vlm) ReadEscalation()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(
            "eval/a99-closed-loop/selective-semantic-escalation-v1/DOC-0252/gold-r3-rescore/selective-escalation-score-gold-r3.v1.json")));
        var root = doc.RootElement;
        object Extract(string key)
        {
            var el = root.GetProperty(key);
            return new
            {
                classification = el.GetProperty("classification").GetString(),
                correctUnderR3 = el.GetProperty("correctUnderR3").GetBoolean(),
            };
        }
        return (Extract("textAdjudicator"), Extract("vlm"));
    }
}
