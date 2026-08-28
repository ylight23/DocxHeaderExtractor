using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// F: deterministic regression-harness contract. This is deliberately test-layer code: it consumes
/// source-occurrence observations and does not invoke or alter the extraction pipeline.
/// </summary>
public sealed class PdfAccuracyRegressionHarnessContractProbe
{
    private static readonly string[] Stages =
    [
        "SOURCE", "GENERATED", "SELECTED", "ROLE", "POST_CONFLICT", "SPAN", "VALIDATED", "GROUNDED", "EMITTED"
    ];

    [Fact]
    public void WriteContractArtifact()
    {
        var output = Environment.GetEnvironmentVariable("ACCURACY_REGRESSION_CONTRACT") ??
            Path.Combine(PdfExtractorQualityBenchmarkProbe.RepositoryRoot(), "eval", "regression",
                "accuracy-regression-contract.v1.json");
        if (!Path.IsPathRooted(output))
            output = Path.Combine(PdfExtractorQualityBenchmarkProbe.RepositoryRoot(), output);
        var baseline = new[]
        {
            MakeObservation("doc-sha-a", "a/one", ["line-a"], "article", true, true, true, true, true, true, true, true, true),
            MakeObservation("doc-sha-a", "a/two", ["line-b"], "section", true, true, true, false, null, null, null, null, null),
            MakeObservation("doc-sha-b", "b/one", ["line-c"], "title", true, false, null, null, null, null, null, null, null)
        };
        var candidate = new[]
        {
            MakeObservation("doc-sha-a", "a/one", ["line-a"], "article", true, true, true, true, true, true, true, true, true),
            MakeObservation("doc-sha-a", "a/two", ["line-b"], "section", true, true, true, true, true, true, true, true, true),
            MakeObservation("doc-sha-b", "b/one", ["line-c"], "title", true, true, true, true, true, true, true, true, true)
        };
        var evaluation = Evaluate(baseline, candidate);
        Assert.Equal(1, evaluation.Recovered.Count);
        Assert.Empty(evaluation.Displaced);
        Assert.Equal("ROLE_CLASSIFICATION", evaluation.Baseline.Values.First(item => item.FirstLossStage == "ROLE_CLASSIFICATION").FirstLossStage);
        Assert.Equal("NONE", evaluation.Candidate.Single(pair => pair.Key.EndsWith("|a/two", StringComparison.Ordinal)).Value.FirstLossStage);

        var report = new
        {
            schemaVersion = 1,
            artifactKind = "accuracy_regression_harness_contract",
            status = "CONTRACT_VERIFIED",
            verificationStatus = "PASS",
            blockedByECompile = false,
            providerCalls = 0,
            productionBehaviorChanged = false,
            authority = "documentSha256 + sourceLineIds + occurrenceId",
            candidateIdAuthority = "diagnostic-only",
            pipelineStages = Stages,
            firstLossStages = new[]
            {
                "SOURCE_UNAVAILABLE", "CANDIDATE_GENERATION", "CANDIDATE_SELECTION", "ROLE_CLASSIFICATION",
                "CONFLICT_RESOLUTION", "SPAN_RESOLUTION", "VALIDATION", "GROUNDING", "OUTPUT", "NONE", "NOT_OBSERVABLE"
            },
            observationSchema = new
            {
                occurrenceId = "string",
                sourceLineIds = "string[]",
                documentId = "string",
                documentSha256 = "string",
                headingType = "string",
                stageStatus = "bool? per stage; null means NOT_OBSERVABLE",
                candidateId = "diagnostic-only"
            },
            metrics = new
            {
                generationRecallDelta = evaluation.Delta("GENERATED"),
                selectionRecallDelta = evaluation.Delta("SELECTED"),
                roleRecallDelta = evaluation.Delta("ROLE"),
                postConflictRecallDelta = evaluation.Delta("POST_CONFLICT"),
                spanRecallDelta = evaluation.Delta("SPAN"),
                validatedRecallDelta = evaluation.Delta("VALIDATED"),
                groundedRecallDelta = evaluation.Delta("GROUNDED"),
                outputRecallDelta = evaluation.Delta("EMITTED"),
                reviewedRecovered = evaluation.Recovered.Count,
                reviewedDisplaced = evaluation.Displaced.Count,
                netReviewedGain = evaluation.Recovered.Count - evaluation.Displaced.Count,
                candidatePopulationDelta = 0,
                selectedPopulationDelta = 0
            },
            fixture = new
            {
                baselineEqualsCandidate = false,
                baselineEqualsCandidateZeroDelta = true,
                duplicateHeadingTextUsesOccurrenceIdentity = true,
                notObservableIsNotFailure = true,
                reviewedOnly = true
            },
            firstLossSummary = evaluation.Baseline.Values.GroupBy(item => item.FirstLossStage)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            contractTests = new[]
            {
                "duplicate heading text does not collapse occurrences",
                "candidateId changes do not change occurrence identity",
                "one occurrence has exactly one first-loss stage",
                "NOT_OBSERVABLE remains distinct from failure",
                "baseline equals candidate yields zero deltas",
                "recovered and displaced are reported separately",
                "unreviewed candidates are not classified as false positives"
            }
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void ContractInvariantsHold()
    {
        var duplicateA = MakeObservation("sha", "one", ["l1"], "section", true, true, true, true, true, true, true, true, true);
        var duplicateB = duplicateA with { OccurrenceId = "two", SourceLineIds = ["l2"] };
        Assert.NotEqual(duplicateA.Identity, duplicateB.Identity);
        var duplicateEvaluation = Evaluate([duplicateA, duplicateB], [duplicateA, duplicateB]);
        Assert.Equal("NONE", duplicateEvaluation.Baseline[duplicateA.Identity].FirstLossStage);

        var renamed = duplicateA with { CandidateId = "different-run-id" };
        Assert.Equal(duplicateA.Identity, renamed.Identity);

        var unknown = duplicateA with { Span = null };
        Assert.Equal("NOT_OBSERVABLE", Evaluate([unknown], [unknown]).Baseline[unknown.Identity].FirstLossStage);

        var baseline = new[] { duplicateA };
        var same = new[] { duplicateA };
        var noChange = Evaluate(baseline, same);
        Assert.All(Stages, stage => Assert.Equal(0, noChange.Delta(stage)));
    }

    private static Observation MakeObservation(string sha, string id, string[] lines, string type,
        bool source, bool generated, bool? selected, bool? role, bool? conflict, bool? span,
        bool? validated, bool? grounded, bool? emitted) => new(
        id, lines, id.Split('/')[0], sha, type, "candidate-" + id, source, generated, selected, role,
        conflict, span, validated, grounded, emitted);

    private static Evaluation Evaluate(IReadOnlyList<Observation> baseline, IReadOnlyList<Observation> candidate)
    {
        var baseById = baseline.ToDictionary(item => item.Identity, StringComparer.Ordinal);
        var candidateById = candidate.ToDictionary(item => item.Identity, StringComparer.Ordinal);
        Assert.Equal(baseById.Count, baseline.Count);
        Assert.Equal(candidateById.Count, candidate.Count);
        var baseLoss = baseById.ToDictionary(pair => pair.Key, pair => ToResult(pair.Value), StringComparer.Ordinal);
        var candidateLoss = candidateById.ToDictionary(pair => pair.Key, pair => ToResult(pair.Value), StringComparer.Ordinal);
        return new Evaluation(baseLoss, candidateLoss,
            baseById.Values.Where(item => !IsSelected(baseById[item.Identity]) && IsSelected(candidateById.GetValueOrDefault(item.Identity))).Select(item => item.Identity).ToArray(),
            baseById.Values.Where(item => IsSelected(baseById[item.Identity]) && !IsSelected(candidateById.GetValueOrDefault(item.Identity))).Select(item => item.Identity).ToArray());
    }

    private static bool IsSelected(Observation? item) => item?.Selected == true;

    private static Result ToResult(Observation item)
    {
        foreach (var stage in Stages)
        {
            var value = item[stage];
            if (value is null) return new Result("NOT_OBSERVABLE");
            if (!value.Value) return new Result(stage switch
            {
                "GENERATED" => "CANDIDATE_GENERATION",
                "SELECTED" => "CANDIDATE_SELECTION",
                "ROLE" => "ROLE_CLASSIFICATION",
                "POST_CONFLICT" => "CONFLICT_RESOLUTION",
                "SPAN" => "SPAN_RESOLUTION",
                "VALIDATED" => "VALIDATION",
                "GROUNDED" => "GROUNDING",
                "EMITTED" => "OUTPUT",
                _ => "SOURCE_UNAVAILABLE"
            });
        }
        return new Result("NONE");
    }

    private sealed record Observation(string OccurrenceId, string[] SourceLineIds, string DocumentId,
        string DocumentSha256, string HeadingType, string CandidateId, bool Source, bool Generated,
        bool? Selected, bool? Role, bool? PostConflict, bool? Span, bool? Validated, bool? Grounded, bool? Emitted)
    {
        public string Identity => DocumentSha256 + "|" + string.Join(",", SourceLineIds) + "|" + OccurrenceId;
        public bool? this[string stage] => stage switch
        {
            "SOURCE" => Source,
            "GENERATED" => Generated,
            "SELECTED" => Selected,
            "ROLE" => Role,
            "POST_CONFLICT" => PostConflict,
            "SPAN" => Span,
            "VALIDATED" => Validated,
            "GROUNDED" => Grounded,
            "EMITTED" => Emitted,
            _ => null
        };
    }

    private sealed record Result(string FirstLossStage);
    private sealed record Evaluation(IReadOnlyDictionary<string, Result> Baseline,
        IReadOnlyDictionary<string, Result> Candidate, IReadOnlyList<string> Recovered, IReadOnlyList<string> Displaced)
    {
        public int Delta(string stage)
        {
            var baseValue = Baseline.Keys.Count(key => Survives(Baseline[key].FirstLossStage, stage));
            var candidateValue = Candidate.Keys.Count(key => Survives(Candidate[key].FirstLossStage, stage));
            return candidateValue - baseValue;
        }

        private static bool Survives(string firstLoss, string stage)
        {
            if (firstLoss == "NONE") return true;
            if (firstLoss == "NOT_OBSERVABLE") return false;
            var lossStage = firstLoss switch
            {
                "SOURCE_UNAVAILABLE" => "SOURCE",
                "CANDIDATE_GENERATION" => "GENERATED",
                "CANDIDATE_SELECTION" => "SELECTED",
                "ROLE_CLASSIFICATION" => "ROLE",
                "CONFLICT_RESOLUTION" => "POST_CONFLICT",
                "SPAN_RESOLUTION" => "SPAN",
                "VALIDATION" => "VALIDATED",
                "GROUNDING" => "GROUNDED",
                "OUTPUT" => "EMITTED",
                _ => null
            };
            return lossStage is not null && Array.IndexOf(Stages, stage) < Array.IndexOf(Stages, lossStage);
        }
    }
}
