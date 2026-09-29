using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Validates and scores the real HAIKU_VLM_AGENDA_INDEPENDENT_ADJUDICATION_V1 evidence
/// (haiku-vlm-decision.v1.json, hand-recorded from the real blind subagent call - Haiku vision has no
/// OpenRouter transport path in this repo to auto-capture through) against Gold R3. Zero calls here;
/// the one real Haiku call already happened and is immutable evidence.
/// </summary>
public sealed class HaikuVlmAgendaIndependentAdjudicationV1ScoringTests
{
    private const string EvidenceRoot = "eval/a99-closed-loop/haiku-vlm-agenda-independent-adjudication-v1/DOC-0252";
    private const string GoldR3Sha256 = "a26b83e14005a9a3e4957cd7a027155e97a534a73fd737551726d18a1c695654";
    private const string GoldR3SemanticFunction = "IDENTITY";
    private static readonly string[] AllowedFunctions = ["IDENTITY", "STRUCTURE", "INFORMATION"];

    [Fact]
    public void Evidence_is_a_single_blind_repeat_with_a_valid_semantic_function()
    {
        var decision = ReadDecision();
        Assert.Equal(1, decision.Repeats);
        Assert.Contains(decision.SemanticFunction, AllowedFunctions);
        Assert.True(decision.Blind.GoldVisible == false);
        Assert.True(decision.Blind.QuestionBalanced);
    }

    [Fact]
    public void Freeze_score()
    {
        var decision = ReadDecision();
        var matchesGoldR3 = string.Equals(decision.SemanticFunction, GoldR3SemanticFunction, StringComparison.Ordinal);

        FreezeArtifact.AssertJson(EvidenceRoot, "haiku-vlm-score.v1.json", new
        {
            artifactKind = "a99_haiku_vlm_agenda_independent_adjudication_score",
            schemaVersion = "a99-haiku-vlm-agenda-independent-adjudication-score-v1",
            task = "HAIKU_VLM_AGENDA_INDEPENDENT_ADJUDICATION_V1",
            status = "SCORED_OFFLINE",
            goldSha256 = GoldR3Sha256,
            scoringModelCalls = 0,
            scoringProviderCalls = 0,
            haikuVlm = new
            {
                model = "Haiku",
                modality = "vision, independent subagent call, no text-arm context",
                repeats = 1,
                semanticFunction = decision.SemanticFunction,
                resolvedSelfReport = decision.Resolved,
                visualEvidence = decision.VisualEvidence,
            },
            goldR3SemanticFunction = GoldR3SemanticFunction,
            matchesGoldR3,
            outcome = matchesGoldR3
                ? "HAIKU_VLM_INDEPENDENT_MODEL_AND_MODALITY_AGREES_WITH_R3"
                : "HAIKU_VLM_STRUCTURE_PRIOR_PERSISTS_ACROSS_TEXT_AND_VISION",
            interpretation = matchesGoldR3
                ? "An independent model (Haiku, not Qwen) in an independent modality (vision, not text) reaches IDENTITY, matching the human-approved R3 authority without ever seeing it, Qwen's result, the pooled vote, or any prior Haiku or adjudicator/VLM result."
                : "Haiku's STRUCTURE answer persists across both text (FULL/LOCAL/MINIMAL/FULL_STRUCTURED_CONTEXT_V2 - all STRUCTURE) and now vision (this call, also STRUCTURE), under a fully blind, three-way-balanced question that did not prime toward any class. This is evidence for a stable STRUCTURE prior specific to this model on this item, not a text-rendering artifact and not a framing/priming effect of the earlier text prompts - the boundary must be held as human-approved authority (R3), not resolved by adding more Haiku evidence or more repeats.",
                doesNotEstablish = new[]
                {
                    "That Haiku is unreliable in general - this is one item, one document.",
                    "That vision evidence is less trustworthy than text evidence - the opposite pattern (VLM agreeing with R3) was already observed for this same model family via Qwen.",
                    "Anything about cross-document or cross-model generalization.",
                },
            modelCalls = 1,
            providerCalls = 0,
            generalizationEstablished = false,
            scope = "DOC-0252 only, ITEM-CCE2C592, one Haiku vision call, no repeats (as specified). Not evidence about the remaining corpus or about Haiku's reliability in general.",
        });
    }

    private static Decision ReadDecision()
    {
        var path = TestRepository.Path($"{EvidenceRoot}/haiku-vlm-decision.v1.json");
        Assert.True(File.Exists(path), $"missing {path}");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var raw = root.GetProperty("rawResponse");
        var blind = root.GetProperty("blindProtocol");
        return new Decision(
            root.GetProperty("repeats").GetInt32(),
            raw.GetProperty("semanticFunction").GetString()!,
            raw.GetProperty("resolved").GetBoolean(),
            raw.GetProperty("visualEvidence").GetString()!,
            new BlindFlags(
                blind.GetProperty("goldVisible").GetBoolean(),
                blind.GetProperty("questionBalancedAcrossThreeClasses").GetBoolean()));
    }

    private sealed record Decision(int Repeats, string SemanticFunction, bool Resolved, string VisualEvidence, BlindFlags Blind);
    private sealed record BlindFlags(bool GoldVisible, bool QuestionBalanced);
}
