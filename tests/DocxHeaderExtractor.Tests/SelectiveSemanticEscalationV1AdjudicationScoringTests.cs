using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Offline scoring of the real SELECTIVE_SEMANTIC_ESCALATION_V1 text-adjudicator captures against
/// Gold R2. Zero model or provider calls - reads only the already-committed raw captures. Gold
/// participates here for the first time in this lineage, strictly as an evaluation-only step after
/// the adjudicator already committed to its answers; it was never shown to the adjudicator itself.
/// </summary>
public sealed class SelectiveSemanticEscalationV1AdjudicationScoringTests
{
    private const string ScoreRoot = "eval/a99-closed-loop/selective-semantic-escalation-v1/DOC-0252";
    private const string CaptureRoot = "eval/a99-closed-loop/selective-semantic-escalation-v1/DOC-0252/adjudication";
    private const string GoldSha256 = "2ab040e93a06d6c8afa1e6b3daf97350bea7cec45477f8bb23b607abee4c313e";

    private static readonly IReadOnlyDictionary<string, string> GoldR2 = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ITEM-505430BB"] = "DOCUMENT_LABEL",
        ["ITEM-CCE2C592"] = "STRUCTURAL_UNIT",
    };

    [Fact]
    public void Both_captures_are_contract_valid_and_unmodified()
    {
        foreach (var itemId in GoldR2.Keys)
        {
            var capture = ReadCapture(itemId);
            Assert.Equal("PASS", capture.ContractStatus);
            Assert.Equal("OK", capture.ParseStatus);
        }
    }

    [Fact]
    public void Freeze_adjudication_score()
    {
        var perItem = GoldR2.Keys.OrderBy(x => x, StringComparer.Ordinal).Select(itemId =>
        {
            var capture = ReadCapture(itemId);
            var gold = GoldR2[itemId];
            var matchesGold = string.Equals(capture.FinalLabel, gold, StringComparison.Ordinal);
            var resolution = !capture.Resolved
                ? "UNRESOLVED"
                : matchesGold ? "RESOLVED_CORRECT" : "RESOLVED_INCORRECT";
            return new
            {
                itemId,
                adjudicatorFinalLabel = capture.FinalLabel,
                adjudicatorSelfReportedResolved = capture.Resolved,
                goldLabel = gold,
                matchesGold,
                escalatedCaseResolution = resolution,
                vlmEligibleUnderProtocol = resolution == "UNRESOLVED",
            };
        }).ToArray();

        var bothSelfReportedResolved = perItem.All(r => r.adjudicatorSelfReportedResolved);
        var bothCorrect = perItem.All(r => r.matchesGold);
        var anyResolvedButIncorrect = perItem.Any(r => r.escalatedCaseResolution == "RESOLVED_INCORRECT");

        FreezeArtifact.AssertJson(ScoreRoot, "adjudication-score.v1.json", new
        {
            artifactKind = "a99_selective_semantic_escalation_adjudication_score",
            schemaVersion = "a99-selective-semantic-escalation-adjudication-score-v1",
            status = "SCORED_OFFLINE",
            goldSha256 = GoldSha256,
            scoringModelCalls = 0,
            scoringProviderCalls = 0,
            perItem,
            summary = new
            {
                bothSelfReportedResolved,
                bothCorrect,
                anyResolvedButIncorrect,
                headline = anyResolvedButIncorrect
                    ? "The text adjudicator resolved both cases by its own report (resolved=true for both), but only one of the two matches Gold R2. Self-reported resolution does not imply correctness - ITEM-CCE2C592 (Agenda) was confidently adjudicated to the wrong label."
                    : bothCorrect
                        ? "The text adjudicator resolved and correctly labeled both flagged items; no VLM escalation is indicated."
                        : "At least one item was left unresolved by the adjudicator's own report; that item is eligible for VLM escalation under the frozen protocol.",
            },
            protocolNote = "Per the frozen escalation protocol, only an UNRESOLVED (or layout-dependent) case is eligible for VLM. RESOLVED_INCORRECT is a distinct, new outcome this run surfaced: the adjudicator's self-reported 'resolved' flag is not a reliable proxy for correctness on this item. Whether to treat RESOLVED_INCORRECT as VLM-eligible, or to require a different remediation (e.g. a second independent adjudication, or accepting the disagreement as unresolved regardless of the self-report), is a protocol decision for the requester, not something this scorer decides unilaterally.",
            vlmCallsMadeThisRun = 0,
            goldUsage = "Evaluation-only, applied after both adjudicator responses were already committed; never shown to the adjudicator.",
            generalizationEstablished = false,
            scope = "DOC-0252 only, the 2 items flagged by the frozen signal, 1 adjudication attempt per item (no repeats). Not evidence about the remaining corpus or about adjudicator reliability in general.",
        });
    }

    private static (string ItemId, string ParseStatus, string ContractStatus, string FinalLabel, bool Resolved) ReadCapture(string itemId)
    {
        var path = TestRepository.Path($"{CaptureRoot}/{itemId}.transport-capture.v1.json");
        Assert.True(File.Exists(path), $"missing {path}");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        return (
            itemId,
            root.GetProperty("parseStatus").GetString()!,
            root.GetProperty("contractStatus").GetString()!,
            root.GetProperty("finalLabel").GetString()!,
            root.GetProperty("resolved").GetBoolean());
    }
}
