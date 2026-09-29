using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Offline scoring of the real VLM escalation result against Gold R2, applied only after the real
/// capture (commit fbaf0bb) already exists. Zero calls. Gold is used here for the first time in the
/// VLM lineage, strictly as evaluation - it was never shown to the VLM.
/// </summary>
public sealed class SelectiveSemanticEscalationV1VlmScoringTests
{
    private const string ScoreRoot = "eval/a99-closed-loop/selective-semantic-escalation-v1/DOC-0252";
    private const string VlmCapturePath = "eval/a99-closed-loop/selective-semantic-escalation-v1/DOC-0252/vlm/ITEM-CCE2C592.transport-capture.v1.json";
    private const string GoldSha256 = "2ab040e93a06d6c8afa1e6b3daf97350bea7cec45477f8bb23b607abee4c313e";
    private const string GoldLabel = "STRUCTURAL_UNIT";
    private const string DominantPooledLabel = "STRUCTURAL_UNIT";

    [Fact]
    public void Vlm_capture_is_contract_valid()
    {
        var (parseStatus, contractStatus, _, _, _) = ReadCapture();
        Assert.Equal("OK", parseStatus);
        Assert.Equal("PASS", contractStatus);
    }

    [Fact]
    public void Freeze_vlm_score()
    {
        var (_, _, semanticFunction, legacyLabel, visualEvidence) = ReadCapture();
        var matchesGold = string.Equals(legacyLabel, GoldLabel, StringComparison.Ordinal);
        var matchesDominantEvidence = string.Equals(legacyLabel, DominantPooledLabel, StringComparison.Ordinal);

        FreezeArtifact.AssertJson(ScoreRoot, "vlm-score.v1.json", new
        {
            artifactKind = "a99_selective_semantic_escalation_vlm_score",
            schemaVersion = "a99-selective-semantic-escalation-vlm-score-v1",
            itemId = "ITEM-CCE2C592",
            status = "SCORED_OFFLINE",
            goldSha256 = GoldSha256,
            scoringModelCalls = 0,
            scoringProviderCalls = 0,
            vlm = new
            {
                model = "qwen/qwen3.7-flash",
                modality = "vision (rendered PDF page image)",
                semanticFunction,
                legacyProjectedLabel = legacyLabel,
                visualEvidence,
            },
            goldLabel = GoldLabel,
            dominantPooledLabel = DominantPooledLabel,
            matchesGold,
            matchesDominantEvidence,
            interpretation = matchesGold
                ? "The VLM's visual-evidence judgment matches Gold; visual escalation resolved this case in the direction the pooled cross-view evidence already favored."
                : "The VLM reproduced the same DOCUMENT_LABEL judgment the text adjudicator reached, via a genuinely different modality (a rendered page image, not text views or pooled labels). Its own stated rationale - the word is centered, positioned like a title - shows this is a real, modality-independent lexical/positional attractor for this item, not a text-rendering artifact that visual evidence could see past. Escalating to VLM was still the right call: it tested a real, falsifiable hypothesis (that visual layout evidence would disambiguate this case) with a genuinely informative negative result, rather than assuming visual evidence would confirm the majority view.",
            finalPipelineState = new
            {
                contextSensitive = true,
                textAdjudicatorResolved = true,
                adjudicatorEvidenceConflict = true,
                visualLayoutRelevant = true,
                vlmEscalated = true,
                vlmResult = legacyLabel,
                outcome = matchesGold ? "VLM_ESCALATION_RESOLVED_CORRECTLY" : "VLM_ESCALATION_INCONCLUSIVE_SAME_ATTRACTOR",
            },
            productionNote = "This item remains an open case for DOC-0252: two independent evaluation paths (text adjudicator, VLM) now agree with each other (DOCUMENT_LABEL) but disagree with the pooled majority evidence and Gold (STRUCTURAL_UNIT). The correct next step is human review, not accepting either model output or the pooled vote as authority - see evidence-conflict-signal.v1.json's own warning that the dominant label is a conflict signal, not semantic authority.",
            generalizationEstablished = false,
            scope = "DOC-0252 only, ITEM-CCE2C592, one VLM call. Not evidence about VLM escalation's effectiveness in general.",
        });
    }

    private static (string ParseStatus, string ContractStatus, string SemanticFunction, string LegacyLabel, string VisualEvidence) ReadCapture()
    {
        var path = TestRepository.Path(VlmCapturePath);
        Assert.True(File.Exists(path), $"missing {path}");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        return (
            root.GetProperty("parseStatus").GetString()!,
            root.GetProperty("contractStatus").GetString()!,
            root.GetProperty("semanticFunction").GetString()!,
            root.GetProperty("legacyProjectedLabel").GetString()!,
            root.GetProperty("visualEvidence").GetString()!);
    }
}
