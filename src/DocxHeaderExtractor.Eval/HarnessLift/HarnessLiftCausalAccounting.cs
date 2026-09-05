namespace DocxHeaderExtractor.Eval.HarnessLift;

public static class HarnessLiftCausalAccounting
{
    public static HarnessHl3CandidateLossDisposition ClassifyCandidateLoss(
        HarnessHl3LossClassificationInput input)
    {
        if (!input.SourceProven) return HarnessHl3CandidateLossDisposition.TraceNotObservable;
        if (input.DeterministicRouteProven)
            return input.DeterministicCorrect
                ? HarnessHl3CandidateLossDisposition.DeterministicBypassCorrect
                : input.DeterministicWrong
                    ? HarnessHl3CandidateLossDisposition.DeterministicBypassWrong
                    : HarnessHl3CandidateLossDisposition.TraceNotObservable;
        if (!input.RepresentationProven)
            return HarnessHl3CandidateLossDisposition.RepresentationMismatch;
        if (!input.CandidateRequired)
            return HarnessHl3CandidateLossDisposition.TraceNotObservable;
        if (!input.CandidateConstructedProven)
            return HarnessHl3CandidateLossDisposition.TrueCandidateNotConstructed;
        if (!input.CandidateSelectedProven)
            return input.RankingBudgetProven
                ? HarnessHl3CandidateLossDisposition.TrueRankingBudgetLoss
                : HarnessHl3CandidateLossDisposition.TrueCandidateNotSelected;
        if (!input.ModelExposureProven)
            return HarnessHl3CandidateLossDisposition.TraceNotObservable;
        if (input.ModelProposalWrong)
            return input.ProposalValidationRejected
                ? HarnessHl3CandidateLossDisposition.OtherProvenStage
                : HarnessHl3CandidateLossDisposition.TraceNotObservable;
        if (input.MarkerResolutionError || input.StructuralResolutionError || input.FinalProjectionError)
            return HarnessHl3CandidateLossDisposition.OtherProvenStage;
        if (input.FinalSourceLineageMismatch)
            return HarnessHl3CandidateLossDisposition.FinalSourceLineageMismatch;
        return HarnessHl3CandidateLossDisposition.TraceNotObservable;
    }

    public static HarnessHl3FirstLossStage ChooseFirstLoss(HarnessHl3FirstLossInput input)
    {
        if (!input.SourceProven) return HarnessHl3FirstLossStage.SourceNotVisible;
        if (!input.RepresentationProven) return HarnessHl3FirstLossStage.RepresentationNotBridged;
        if (input.DeterministicRouteProven)
            return input.DeterministicCorrect
                ? HarnessHl3FirstLossStage.DeterministicBypassCorrect
                : input.DeterministicWrong
                    ? HarnessHl3FirstLossStage.DeterministicBypassWrong
                    : HarnessHl3FirstLossStage.TraceNotObservable;
        if (input.CandidateRequired && !input.CandidateConstructedProven)
            return HarnessHl3FirstLossStage.CandidateNotConstructed;
        if (input.CandidateRequired && !input.CandidateSelectedProven)
            return input.RankingBudgetProven
                ? HarnessHl3FirstLossStage.SelectionRankingBudgetLoss
                : HarnessHl3FirstLossStage.CandidateConstructedNotSelected;
        if (!input.ModelExposureProven) return HarnessHl3FirstLossStage.TraceNotObservable;
        if (input.ModelProposalWrong && input.ProposalValidationRejected)
            return HarnessHl3FirstLossStage.ProposalValidationRejection;
        if (input.ModelProposalWrong && input.MarkerResolutionError)
            return HarnessHl3FirstLossStage.MarkerResolutionError;
        if (input.ModelProposalWrong && input.StructuralResolutionError)
            return HarnessHl3FirstLossStage.StructuralResolutionError;
        if (input.ModelProposalWrong) return HarnessHl3FirstLossStage.ModelProposalWrong;
        if (input.FinalProjectionError) return HarnessHl3FirstLossStage.FinalProjectionError;
        return HarnessHl3FirstLossStage.NoLoss;
    }

    public static HarnessHl3ModelExposureStatus ModelExposure(
        bool modelRunOccurredInDocument,
        bool retainedPerOccurrenceDecision,
        bool modelNotApplicable = false) =>
        modelNotApplicable ? HarnessHl3ModelExposureStatus.ModelNotApplicable :
        retainedPerOccurrenceDecision ? HarnessHl3ModelExposureStatus.ModelOccurrenceExposedProven :
        modelRunOccurredInDocument ? HarnessHl3ModelExposureStatus.ModelOccurrenceExposureUnknown :
        HarnessHl3ModelExposureStatus.ModelNotApplicable;

    public static bool IsFinalLineageWrong(string finalStatus) =>
        string.Equals(finalStatus, "FINAL_PRESENT_WRONG_FIELD", StringComparison.Ordinal);

    public static HarnessHl3NamespaceComparison Compare(
        string namespaceA,
        IReadOnlyCollection<string> valuesA,
        string namespaceB,
        IReadOnlyCollection<string> valuesB)
    {
        var a = valuesA.ToHashSet(StringComparer.Ordinal);
        var b = valuesB.ToHashSet(StringComparer.Ordinal);
        var intersection = a.Intersect(b, StringComparer.Ordinal).Count();
        return new(namespaceA, namespaceB, a.Count, b.Count, intersection, a.Count - intersection,
            b.Count - intersection, a.Count == 0 ? 0 : (double)intersection / a.Count);
    }
}
