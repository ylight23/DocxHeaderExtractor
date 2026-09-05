namespace DocxHeaderExtractor.Eval.HarnessLift;

public static class HarnessLiftAccounting
{
    public static bool IsOfficial(HarnessReferenceAuthority authority) =>
        authority is HarnessReferenceAuthority.HumanGold or
            HarnessReferenceAuthority.HumanKey or
            HarnessReferenceAuthority.SourceStructuralReference;

    public static bool Supports(HarnessReferenceRecord reference, HarnessMetric metric) =>
        IsOfficial(reference.Authority) && reference.SupportedMetrics.Contains(metric switch
        {
            HarnessMetric.HeadingExistence => "headingExistence",
            HarnessMetric.Role => "role",
            HarnessMetric.Span => "span",
            HarnessMetric.Level => "level",
            HarnessMetric.Parent => "parent",
            HarnessMetric.Hierarchy => "hierarchy",
            _ => throw new ArgumentOutOfRangeException(nameof(metric)),
        }, StringComparer.OrdinalIgnoreCase);

    public static HarnessKnowledge Knowledge(HarnessReferenceRecord? reference, HarnessMetric metric) =>
        reference is null ? HarnessKnowledge.Unknown :
        Supports(reference, metric) ? reference.Coverage switch
        {
            HarnessCoverage.Full => HarnessKnowledge.Proven,
            HarnessCoverage.Partial => HarnessKnowledge.Partial,
            _ => HarnessKnowledge.Unknown,
        } : HarnessKnowledge.NotApplicable;

    public static HarnessRepeatedStatistic Summarize(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return new(0, null, null, null, null);
        var mean = values.Average();
        var variance = values.Sum(value => Math.Pow(value - mean, 2)) / values.Count;
        return new(values.Count, mean, values.Min(), values.Max(), Math.Sqrt(variance));
    }

    public static HarnessLossStage ChooseFirstLoss(
        bool sourceSeen,
        bool candidateSelected,
        bool? modelCalled,
        bool? modelCorrect,
        bool? finalCorrect,
        bool? validatorRejected,
        bool? markerChanged,
        bool? structuralChanged) =>
        !sourceSeen ? HarnessLossStage.SourceLoss :
        !candidateSelected ? HarnessLossStage.CandidateLoss :
        modelCalled != true ? HarnessLossStage.Unknown :
        modelCorrect == false && validatorRejected == true ? HarnessLossStage.ProposalValidation :
        modelCorrect == false && markerChanged == true ? HarnessLossStage.MarkerResolution :
        modelCorrect == false && structuralChanged == true ? HarnessLossStage.StructuralResolution :
        modelCorrect == false && finalCorrect == false ? HarnessLossStage.ModelRole :
        finalCorrect == false ? HarnessLossStage.FinalProjection :
        HarnessLossStage.Unknown;

    public static (double? Precision, double? Recall, double? F1) BinaryMetrics(int truePositives, int falsePositives, int falseNegatives)
    {
        var precision = truePositives + falsePositives == 0 ? (double?)null :
            (double)truePositives / (truePositives + falsePositives);
        var recall = truePositives + falseNegatives == 0 ? (double?)null :
            (double)truePositives / (truePositives + falseNegatives);
        var f1 = precision is null || recall is null || precision + recall == 0 ? null :
            2 * precision * recall / (precision + recall);
        return (precision, recall, f1);
    }

    public static double? Lift(int finalCorrect, int modelCorrect, int population) =>
        population == 0 ? null : (double)(finalCorrect - modelCorrect) / population;

    public static (int Evaluated, int Correct, double? Accuracy) ConditionalAccuracy(
        IEnumerable<(bool Exposed, bool Correct)> observations)
    {
        var exposed = observations.Where(item => item.Exposed).ToArray();
        return (exposed.Length, exposed.Count(item => item.Correct),
            exposed.Length == 0 ? null : (double)exposed.Count(item => item.Correct) / exposed.Length);
    }

    public static string ClassifyValidatorRejection(bool? proposalCorrect, bool? validatorRejected) =>
        proposalCorrect == true && validatorRejected == true ? "REJECTED_CORRECT_PROPOSAL" :
        proposalCorrect == false && validatorRejected == true ? "REJECTED_WRONG_PROPOSAL" :
        "NOT_OBSERVABLE";

    public static string ClassifyDeterministicStageEffect(bool modelCorrect, bool finalCorrect, bool stageChanged) =>
        stageChanged && modelCorrect && !finalCorrect ? "INTRODUCED_BY_DETERMINISTIC_STAGE" : "NOT_OBSERVABLE";

    public static IReadOnlySet<string> JoinReferenceIds(
        IEnumerable<string> observedIds,
        IEnumerable<string> referenceIds) =>
        observedIds.Intersect(referenceIds, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);

    public static bool GroupsDoNotCrossSplits(IEnumerable<(string GroupId, string Split)> assignments) =>
        assignments.GroupBy(item => item.GroupId, StringComparer.Ordinal)
            .All(group => group.Select(item => item.Split).Distinct(StringComparer.Ordinal).Count() <= 1);
}
