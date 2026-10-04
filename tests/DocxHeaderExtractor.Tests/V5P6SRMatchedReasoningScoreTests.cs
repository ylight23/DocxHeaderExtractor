using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Offline P6S-R score using the exact strict/global P6S-F implementation, not a new scorer.</summary>
public sealed class V5P6SRMatchedReasoningScoreTests
{
    private const string CaptureRoot = "artifacts/v5-p6s-candidate-authority/p6sr-matched-reasoning-full31";
    private const string ControlScorePath = "artifacts/v5-p6s-candidate-authority/p6sf-global-association-score/full31-global-association-and-exact-score.v1.json";
    private const string OutputRoot = "artifacts/v5-p6s-candidate-authority/p6sr-matched-reasoning-score";
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";

    [Fact]
    public void P6SR_scores_frozen_reasoning_enabled_capture_with_the_same_P6SF_strict_and_global_scorer()
    {
        var capturePath = TestRepository.Path($"{CaptureRoot}/result.v1.json");
        using var capture = JsonDocument.Parse(File.ReadAllText(capturePath));
        var captureRoot = capture.RootElement;
        Assert.Equal(31, captureRoot.GetProperty("providerCalls").GetInt32());
        Assert.False(captureRoot.GetProperty("goldRead").GetBoolean());
        Assert.Equal(0, captureRoot.GetProperty("retry").GetInt32());
        Assert.True(captureRoot.GetProperty("treatment").GetProperty("reasoning").GetProperty("enabled").GetBoolean());
        Assert.False(captureRoot.GetProperty("treatment").GetProperty("reasoning").TryGetProperty("effort", out _));

        var plans = V5P6SFGlobalAssociationScoringTests.Documents.ToDictionary(item => item.Id, item =>
        {
            var sha = CanonicalSemanticSourceHash.Compute(TestRepository.Path(item.Pdf));
            return PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(TestRepository.Path($"{SnapshotRoot}/{sha}.json"), item.Id);
        }, StringComparer.Ordinal);
        V5P6SFGlobalAssociationScoringTests.VerifyEvaluationBasis(plans);
        var gold = V5P6SFGlobalAssociationScoringTests.ReadGold(plans);
        var model = new List<V5P6SFGlobalAssociationScoringTests.Prediction>();
        var final = new List<V5P6SFGlobalAssociationScoringTests.Prediction>();
        var representation = new List<V5P6SFGlobalAssociationScoringTests.Prediction>();
        foreach (var row in captureRoot.GetProperty("rows").EnumerateArray())
        {
            var documentId = row.GetProperty("documentId").GetString()!;
            var packId = row.GetProperty("PackId").GetString()!;
            var plan = plans[documentId]; var pack = plan.Packs.Single(value => value.PackId == packId);
            Assert.True(row.GetProperty("transportAccepted").GetBoolean());
            Assert.Equal("stop", row.GetProperty("finishReason").GetString());
            Assert.Equal(0, row.GetProperty("retryCount").GetInt32());
            var raw = row.GetProperty("rawResponse").GetString()!;
            Assert.Equal(Hash(Encoding.UTF8.GetBytes(raw)), row.GetProperty("rawResponseSha256").GetString());
            var parsed = PdfCandidateAuthorityQualificationAdapter.ParseCandidateDecision(pack, raw);
            foreach (var decision in parsed.AcceptedBeforeOverlapQuarantine)
            {
                var bound = SemanticSourcePartBinder.Bind(plan.SourceAtoms, decision.Candidate.Parts); Assert.True(bound.IsBound, bound.Reason);
                var prediction = V5P6SFGlobalAssociationScoringTests.PredictionFrom(documentId, packId, decision.Candidate.Id, bound.Parts);
                if (decision.Kind == V5CandidateDecisionKind.HEADING) model.Add(prediction); else representation.Add(prediction);
            }
            foreach (var decision in parsed.Headings)
            {
                var bound = SemanticSourcePartBinder.Bind(plan.SourceAtoms, decision.Candidate.Parts); Assert.True(bound.IsBound, bound.Reason);
                final.Add(V5P6SFGlobalAssociationScoringTests.PredictionFrom(documentId, packId, decision.Candidate.Id, bound.Parts));
            }
        }
        Assert.Equal(model.Count, model.Select(item => $"{item.DocumentId}:{item.Identity}").Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(final.Count, final.Select(item => $"{item.DocumentId}:{item.Identity}").Distinct(StringComparer.Ordinal).Count());
        var allGold = gold.SelectMany(item => item.Value).OrderBy(item => item.DocumentId, StringComparer.Ordinal).ThenBy(item => item.Ordinal).ToArray();
        var modelAssociation = V5P6SFGlobalAssociationScoringTests.GlobalAssociation(model, allGold);
        var finalAssociation = V5P6SFGlobalAssociationScoringTests.GlobalAssociation(final, allGold);
        var finalKeys = final.Select(V5P6SFGlobalAssociationScoringTests.CandidateKey).ToHashSet(StringComparer.Ordinal);
        var modelExact = V5P6SFGlobalAssociationScoringTests.ExactOccurrence(model, allGold);
        var finalExact = V5P6SFGlobalAssociationScoringTests.ExactOccurrence(final, allGold);
        var diagnostics = allGold.Select((item, index) => V5P6SFGlobalAssociationScoringTests.Diagnostic(index, item, model, final, representation, modelAssociation, finalAssociation, finalKeys)).ToArray();
        var conflictLosses = diagnostics.Count(item => item.FinalOutcome == "CONFLICT_LOSS");

        var controlScorePath = TestRepository.Path(ControlScorePath);
        using var controlScore = JsonDocument.Parse(File.ReadAllText(controlScorePath));
        var output = new
        {
            schemaVersion = "v5-p6sr-matched-reasoning-p6sf-score-v1",
            execution = new { providerCallsDuringScore = 0, frozenProviderCallsInCapture = 31, goldRead = true, goldMutation = "NONE", runtimeChanged = false,
                sourceArtifact = $"{CaptureRoot}/result.v1.json", sourceArtifactSha256 = Hash(File.ReadAllBytes(capturePath)), controlScore = ControlScorePath, controlScoreSha256 = Hash(File.ReadAllBytes(controlScorePath)) },
            scorer = new { implementation = "V5P6SFGlobalAssociationScoringTests", exactOccurrence = "strict candidate identity equals Gold identity; this is the only TP/FP/FN/F1 occurrence metric", goldAssociation = "same deterministic P6S-F global maximum-weight association; diagnostic only" },
            matchedControl = new { P6SD_reasoning = "effort:none", P6SR_reasoning = "enabled:true, effort omitted", independentVariable = "reasoning only" },
            metrics = new
            {
                modelSelection = new { exactOccurrence = modelExact, goldAssociation = V5P6SFGlobalAssociationScoringTests.Association(model, allGold, modelAssociation), extent = V5P6SFGlobalAssociationScoringTests.Extent(diagnostics, true) },
                finalFailClosedOutput = new { exactOccurrence = finalExact, goldAssociation = V5P6SFGlobalAssociationScoringTests.Association(final, allGold, finalAssociation), extent = V5P6SFGlobalAssociationScoringTests.Extent(diagnostics, false) },
                conflictProjection = new { headingCandidatesBefore = model.Count, headingCandidatesAfter = final.Count, candidatesQuarantined = model.Count(item => !finalKeys.Contains(V5P6SFGlobalAssociationScoringTests.CandidateKey(item))), goldAssociationsLost = conflictLosses, strictExactGoldLost = modelExact.TruePositive - finalExact.TruePositive },
                representationDiagnostics = new { total = representation.Count, src095 = representation.Count(item => item.DocumentId == "SRC-095"), relationAccuracy = "NOT_EVALUABLE_WITHOUT_RELATION_GOLD" },
                perDocument = V5P6SFGlobalAssociationScoringTests.Documents.Select(document => V5P6SFGlobalAssociationScoringTests.PerDocument(document.Id, model, final, allGold)).ToArray(),
            },
            goldOccurrenceDiagnostics = diagnostics,
        };
        FreezeArtifact.AssertJson(OutputRoot, "full31-matched-reasoning-p6sf-score.v1.json", output);
    }

    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
}
