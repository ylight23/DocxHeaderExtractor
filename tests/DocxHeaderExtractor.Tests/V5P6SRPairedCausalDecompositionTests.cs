using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Provider-free causal decomposition of the immutable P6S-D (reasoning off) and
/// P6S-R (reasoning enabled) captures.  It deliberately does not select a new
/// treatment, mutate Gold, or infer a routing rule from the two documents.
/// </summary>
public sealed class V5P6SRPairedCausalDecompositionTests
{
    private const string ControlRoot = "artifacts/v5-p6s-candidate-authority/p6sd-full31";
    private const string TreatmentRoot = "artifacts/v5-p6s-candidate-authority/p6sr-matched-reasoning-full31";
    private const string OutputRoot = "artifacts/v5-p6s-candidate-authority/p6sr-paired-causal-decomposition";
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";

    private sealed record Decision(string DocumentId, string PackId, string CandidateId, string Identity,
        IReadOnlyList<V5P6SFGlobalAssociationScoringTests.Part> Parts, string Kind, bool SurvivesProjection);
    private sealed record ReviewFact(string Verdict, string Pattern);

    [Fact]
    public void P6SR_decomposes_the_matched_reasoning_delta_without_provider_calls()
    {
        var controlPath = TestRepository.Path($"{ControlRoot}/result.v1.json");
        var treatmentPath = TestRepository.Path($"{TreatmentRoot}/result.v1.json");
        using var controlCapture = JsonDocument.Parse(File.ReadAllText(controlPath));
        using var treatmentCapture = JsonDocument.Parse(File.ReadAllText(treatmentPath));
        AssertCapture(controlCapture.RootElement, expectedReasoningEnabled: false);
        AssertCapture(treatmentCapture.RootElement, expectedReasoningEnabled: true);

        var plans = V5P6SFGlobalAssociationScoringTests.Documents.ToDictionary(document => document.Id, document =>
        {
            var sha = CanonicalSemanticSourceHash.Compute(TestRepository.Path(document.Pdf));
            return PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(TestRepository.Path($"{SnapshotRoot}/{sha}.json"), document.Id);
        }, StringComparer.Ordinal);
        V5P6SFGlobalAssociationScoringTests.VerifyEvaluationBasis(plans);
        var gold = V5P6SFGlobalAssociationScoringTests.ReadGold(plans).SelectMany(pair => pair.Value)
            .OrderBy(value => value.DocumentId, StringComparer.Ordinal).ThenBy(value => value.Ordinal).ToArray();
        var control = Read(controlCapture.RootElement, plans);
        var treatment = Read(treatmentCapture.RootElement, plans);
        Assert.Equal(control.ByPack.Keys.OrderBy(value => value, StringComparer.Ordinal), treatment.ByPack.Keys.OrderBy(value => value, StringComparer.Ordinal));
        foreach (var key in control.ByPack.Keys)
        {
            Assert.Equal(control.ByPack[key].CandidateFingerprint, treatment.ByPack[key].CandidateFingerprint);
            Assert.Equal(control.ByPack[key].SemanticRequestHash, treatment.ByPack[key].SemanticRequestHash);
        }

        var review = ReviewPatterns();
        var controlDiagnostics = Diagnostics(control, gold);
        var treatmentDiagnostics = Diagnostics(treatment, gold);
        var goldTransitions = gold.Select((item, index) => new
        {
            item.DocumentId, goldOrdinal = item.Ordinal, item.Identity,
            off = controlDiagnostics[index].FinalOutcome,
            on = treatmentDiagnostics[index].FinalOutcome,
            transition = GoldTransition(controlDiagnostics[index].FinalOutcome, treatmentDiagnostics[index].FinalOutcome),
            offCandidate = Candidate(controlDiagnostics[index].Final), onCandidate = Candidate(treatmentDiagnostics[index].Final),
        }).ToArray();

        var controlFinalByCandidate = control.FinalHeadings.ToDictionary(Key, StringComparer.Ordinal);
        var treatmentFinalByCandidate = treatment.FinalHeadings.ToDictionary(Key, StringComparer.Ordinal);
        var controlPreByCandidate = control.PreDecisions.ToDictionary(Key, StringComparer.Ordinal);
        var treatmentPreByCandidate = treatment.PreDecisions.ToDictionary(Key, StringComparer.Ordinal);
        var candidateKeys = controlPreByCandidate.Keys.Union(treatmentPreByCandidate.Keys, StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var goldKeys = gold.Select(value => $"{value.DocumentId}:{value.Identity}").ToHashSet(StringComparer.Ordinal);
        var decisionTransitions = candidateKeys.Select(key =>
        {
            controlPreByCandidate.TryGetValue(key, out var off); treatmentPreByCandidate.TryGetValue(key, out var on);
            var documentId = (on ?? off)!.DocumentId;
            return new
            {
                documentId, candidateKey = key, candidateId = (on ?? off)!.CandidateId,
                off = off?.Kind ?? "NO_DECISION", on = on?.Kind ?? "NO_DECISION",
                transition = $"{off?.Kind ?? "NO_DECISION"}_TO_{on?.Kind ?? "NO_DECISION"}",
                offFinalHeading = controlFinalByCandidate.ContainsKey(key), onFinalHeading = treatmentFinalByCandidate.ContainsKey(key),
                exactGold = goldKeys.Contains($"{documentId}:{(on ?? off)!.Identity}"), identity = (on ?? off)!.Identity,
            };
        }).ToArray();

        var src095FpRemoved = controlFinalByCandidate.Values.Where(value => value.DocumentId == "SRC-095" &&
                !goldKeys.Contains($"{value.DocumentId}:{value.Identity}") && !treatmentFinalByCandidate.ContainsKey(Key(value)))
            .Select(value => new
            {
                value.PackId, value.CandidateId, value.Identity,
                treatmentDecision = treatmentPreByCandidate.TryGetValue(Key(value), out var decision) ? decision.Kind : "NO_DECISION",
                sourceFamily = SourceFamily(value.Parts.SelectMany(part => review.GetValueOrDefault($"{value.DocumentId}:{part.Alias}") ?? []).Distinct().ToArray()),
            }).OrderBy(value => value.PackId, StringComparer.Ordinal).ThenBy(value => value.CandidateId, StringComparer.Ordinal).ToArray();
        var src095FpIntroduced = treatmentFinalByCandidate.Values.Where(value => value.DocumentId == "SRC-095" &&
                !goldKeys.Contains($"{value.DocumentId}:{value.Identity}") && !controlFinalByCandidate.ContainsKey(Key(value)))
            .OrderBy(value => value.PackId, StringComparer.Ordinal).ThenBy(value => value.CandidateId, StringComparer.Ordinal).ToArray();
        Assert.Equal(56, src095FpRemoved.Length);
        Assert.Equal(3, src095FpIntroduced.Length);
        Assert.Equal(53, src095FpRemoved.Length - src095FpIntroduced.Length); // P6S-D 100 strict FPs -> P6S-R 47, not 49.

        var output = new
        {
            schemaVersion = "v5-p6sr-paired-causal-decomposition-v1",
            execution = new
            {
                providerCalls = 0, goldRead = true, goldMutation = "NONE", runtimeChanged = false,
                controlCapture = $"{ControlRoot}/result.v1.json", controlCaptureSha256 = Hash(File.ReadAllBytes(controlPath)),
                treatmentCapture = $"{TreatmentRoot}/result.v1.json", treatmentCaptureSha256 = Hash(File.ReadAllBytes(treatmentPath)),
            },
            authority = new
            {
                treatment = "P6S-D reasoning effort:none versus P6S-R reasoning enabled:true with effort omitted; immutable matched raw captures.",
                causalScope = "The request/parsing/binding control is matched. This observed two-document cohort establishes heterogeneous treatment outcomes, not a production selective-routing rule.",
                gold = "Current USER_RECONFIRMED_FROZEN 139-occurrence Gold is read only after capture; no mutation.",
            },
            totals = new
            {
                control = Metrics(control, gold), treatment = Metrics(treatment, gold),
                goldTransitions = goldTransitions.GroupBy(value => value.transition, StringComparer.Ordinal).OrderBy(value => value.Key, StringComparer.Ordinal).Select(value => new { transition = value.Key, count = value.Count() }).ToArray(),
                decisionTransitions = decisionTransitions.GroupBy(value => value.transition, StringComparer.Ordinal).OrderByDescending(value => value.Count()).ThenBy(value => value.Key, StringComparer.Ordinal).Select(value => new { transition = value.Key, count = value.Count() }).ToArray(),
                src095StrictFalsePositiveReduction = new { control = 100, treatment = 47, removed = src095FpRemoved.Length, introduced = src095FpIntroduced.Length, netReduction = src095FpRemoved.Length - src095FpIntroduced.Length, byTreatmentDecision = src095FpRemoved.GroupBy(value => value.treatmentDecision, StringComparer.Ordinal).OrderBy(value => value.Key, StringComparer.Ordinal).Select(value => new { decision = value.Key, count = value.Count() }).ToArray(), bySourceFamily = src095FpRemoved.GroupBy(value => value.sourceFamily, StringComparer.Ordinal).OrderBy(value => value.Key, StringComparer.Ordinal).Select(value => new { family = value.Key, count = value.Count() }).ToArray() },
            },
            perDocument = V5P6SFGlobalAssociationScoringTests.Documents.Select(document => new
            {
                documentId = document.Id,
                gold = gold.Count(value => value.DocumentId == document.Id),
                control = Metrics(control, gold, document.Id), treatment = Metrics(treatment, gold, document.Id),
                goldTransitions = goldTransitions.Where(value => value.DocumentId == document.Id).GroupBy(value => value.transition, StringComparer.Ordinal).OrderBy(value => value.Key, StringComparer.Ordinal).Select(value => new { transition = value.Key, count = value.Count() }).ToArray(),
                decisionTransitions = decisionTransitions.Where(value => value.documentId == document.Id).GroupBy(value => value.transition, StringComparer.Ordinal).OrderByDescending(value => value.Count()).ThenBy(value => value.Key, StringComparer.Ordinal).Select(value => new { transition = value.Key, count = value.Count() }).ToArray(),
            }).ToArray(),
            src095RemovedStrictFalsePositives = src095FpRemoved,
            goldOccurrenceTransitions = goldTransitions,
        };
        FreezeArtifact.AssertJson(OutputRoot, "full31-paired-reasoning-causal-decomposition.v1.json", output);
    }

    private static void AssertCapture(JsonElement capture, bool expectedReasoningEnabled)
    {
        Assert.Equal(31, capture.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, capture.GetProperty("retry").GetInt32());
        Assert.False(capture.GetProperty("goldRead").GetBoolean());
        Assert.Equal(31, capture.GetProperty("rows").GetArrayLength());
        var reasoning = expectedReasoningEnabled
            ? capture.GetProperty("treatment").GetProperty("reasoning")
            : capture.GetProperty("route").GetProperty("reasoning");
        if (expectedReasoningEnabled) { Assert.True(reasoning.GetProperty("enabled").GetBoolean()); Assert.False(reasoning.TryGetProperty("effort", out _)); }
        else Assert.Equal("none", reasoning.GetString());
    }

    private static Parsed Read(JsonElement capture, IReadOnlyDictionary<string, PdfCandidateAuthorityDocumentPlan> plans)
    {
        var pre = new List<Decision>(); var final = new List<Decision>(); var reps = new List<V5P6SFGlobalAssociationScoringTests.Prediction>();
        var byPack = new Dictionary<string, (string CandidateFingerprint, string SemanticRequestHash)>(StringComparer.Ordinal);
        foreach (var row in capture.GetProperty("rows").EnumerateArray())
        {
            var documentId = row.GetProperty("documentId").GetString()!; var packId = row.GetProperty("PackId").GetString()!;
            Assert.True(row.GetProperty("transportAccepted").GetBoolean()); Assert.Equal("stop", row.GetProperty("finishReason").GetString()); Assert.Equal(0, row.GetProperty("retryCount").GetInt32());
            var raw = row.GetProperty("rawResponse").GetString()!; Assert.Equal(Hash(Encoding.UTF8.GetBytes(raw)), row.GetProperty("rawResponseSha256").GetString());
            var pack = plans[documentId].Packs.Single(value => value.PackId == packId);
            byPack[$"{documentId}:{packId}"] = (row.GetProperty("candidateUniverseFingerprint").GetString()!, row.GetProperty("semanticRequestHash").GetString()!);
            var parsed = PdfCandidateAuthorityQualificationAdapter.ParseCandidateDecision(pack, raw);
            foreach (var item in parsed.AcceptedBeforeOverlapQuarantine)
            {
                var binding = SemanticSourcePartBinder.Bind(plans[documentId].SourceAtoms, item.Candidate.Parts); Assert.True(binding.IsBound, binding.Reason);
                var decision = new Decision(documentId, packId, item.Candidate.Id, binding.Identity, binding.Parts.Select(value => new V5P6SFGlobalAssociationScoringTests.Part(value.Alias, value.Start, value.End)).ToArray(), item.Kind.ToString(), false);
                pre.Add(decision); if (item.Kind == V5CandidateDecisionKind.REPRESENTATION) reps.Add(Prediction(decision));
            }
            foreach (var item in parsed.Headings)
            {
                var binding = SemanticSourcePartBinder.Bind(plans[documentId].SourceAtoms, item.Candidate.Parts); Assert.True(binding.IsBound, binding.Reason);
                final.Add(new Decision(documentId, packId, item.Candidate.Id, binding.Identity, binding.Parts.Select(value => new V5P6SFGlobalAssociationScoringTests.Part(value.Alias, value.Start, value.End)).ToArray(), item.Kind.ToString(), true));
            }
        }
        Assert.Equal(pre.Count, pre.Select(Key).Distinct(StringComparer.Ordinal).Count()); Assert.Equal(final.Count, final.Select(Key).Distinct(StringComparer.Ordinal).Count());
        return new Parsed(pre, final, reps, byPack);
    }

    private static IReadOnlyList<V5P6SFGlobalAssociationScoringTests.DiagnosticRow> Diagnostics(Parsed parsed, IReadOnlyList<V5P6SFGlobalAssociationScoringTests.Gold> gold)
    {
        var model = parsed.PreDecisions.Where(value => value.Kind == nameof(V5CandidateDecisionKind.HEADING)).Select(Prediction).ToArray();
        var final = parsed.FinalHeadings.Select(Prediction).ToArray();
        var modelMatch = V5P6SFGlobalAssociationScoringTests.GlobalAssociation(model, gold); var finalMatch = V5P6SFGlobalAssociationScoringTests.GlobalAssociation(final, gold);
        var finalKeys = final.Select(V5P6SFGlobalAssociationScoringTests.CandidateKey).ToHashSet(StringComparer.Ordinal);
        return gold.Select((item, index) => V5P6SFGlobalAssociationScoringTests.Diagnostic(index, item, model, final, parsed.Representations, modelMatch, finalMatch, finalKeys)).ToArray();
    }

    private static object Metrics(Parsed parsed, IReadOnlyList<V5P6SFGlobalAssociationScoringTests.Gold> allGold, string? documentId = null)
    {
        var gold = documentId is null ? allGold : allGold.Where(value => value.DocumentId == documentId).ToArray();
        var headings = parsed.FinalHeadings.Where(value => documentId is null || value.DocumentId == documentId).Select(Prediction).ToArray();
        return V5P6SFGlobalAssociationScoringTests.ExactOccurrence(headings, gold);
    }

    private static V5P6SFGlobalAssociationScoringTests.Prediction Prediction(Decision value) => new(value.DocumentId, value.PackId, value.CandidateId, value.Identity, value.Parts);
    private static string Key(Decision value) => $"{value.DocumentId}:{value.PackId}:{value.CandidateId}";
    private static string Key(V5P6SFGlobalAssociationScoringTests.Prediction value) => V5P6SFGlobalAssociationScoringTests.CandidateKey(value);
    private static object? Candidate(object? value) => value;
    private static string GoldTransition(string off, string on) => (off == "EXACT", on == "EXACT") switch { (false, true) => "OFF_WRONG_TO_ON_CORRECT", (true, false) => "OFF_CORRECT_TO_ON_WRONG", (true, true) => "BOTH_CORRECT", _ => "BOTH_WRONG" };
    private static string SourceFamily(IReadOnlyList<ReviewFact> facts) => facts.Any(value => value.Verdict == "NON_HEADING" && value.Pattern == "CONTENTS_ENTRY") ? "REVIEWED_TOC_NAVIGATION_NONHEADING" : facts.Any(value => value.Verdict == "NON_HEADING") ? "REVIEWED_OTHER_NONHEADING" : "UNREVIEWED_STRICT_FP";
    private static Dictionary<string, IReadOnlyList<ReviewFact>> ReviewPatterns() => V5P6SFGlobalAssociationScoringTests.Documents.SelectMany(document =>
    {
        using var json = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"eval/a99-closed-loop/source-review-v1/{document.Id}/review-items.json")));
        return json.RootElement.GetProperty("items").EnumerateArray().SelectMany(item => item.GetProperty("parts").EnumerateArray().Select(part =>
            (Key: $"{document.Id}:{part.GetProperty("sourceAlias").GetString()}", Fact: new ReviewFact(item.GetProperty("verdict").GetString()!, item.GetProperty("pattern").GetString()!)))).ToArray();
    }).ToArray().GroupBy(value => value.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => (IReadOnlyList<ReviewFact>)group.Select(value => value.Fact).Distinct().ToArray(), StringComparer.Ordinal);
    private sealed record Parsed(IReadOnlyList<Decision> PreDecisions, IReadOnlyList<Decision> FinalHeadings, IReadOnlyList<V5P6SFGlobalAssociationScoringTests.Prediction> Representations, IReadOnlyDictionary<string, (string CandidateFingerprint, string SemanticRequestHash)> ByPack);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
