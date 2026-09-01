using Accuracy99Baseline;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

public sealed class Accuracy99PhaseBContractTests
{
    [Fact]
    public void SourceSpanMustBeEndExclusiveAndInsideRawText()
    {
        Assert.True(PhaseBContracts.IsValidSourceSpan("prefix Figure 3 caption suffix", new StructuralSpan(7, 23)));
        Assert.False(PhaseBContracts.IsValidSourceSpan("short", new StructuralSpan(0, 8)));
        Assert.False(PhaseBContracts.IsValidSourceSpan("short", new StructuralSpan(3, 3)));
    }

    [Fact]
    public void HeadingSpanMustMatchFrozenRawText()
    {
        var raw = "prefix Figure 3 caption suffix";
        var span = new StructuralSpan(7, 23);
        Assert.True(PhaseBContracts.IsHeadingSpanTextConsistent(raw, span, raw[span.Start..span.End]));
        Assert.False(PhaseBContracts.IsHeadingSpanTextConsistent(raw, span, "Figure 4 caption"));
    }

    [Fact]
    public void ExhaustiveReviewRejectsUnlabeledOccurrences()
    {
        Assert.True(PhaseBContracts.IsExhaustive(["HEADING", "NON_HEADING", "UNCERTAIN", "EXCLUDED"]));
        Assert.False(PhaseBContracts.IsExhaustive(["HEADING", null]));
        Assert.False(PhaseBContracts.IsExhaustive([]));
    }

    [Fact]
    public void DuplicateGoldIdentityIsDetected()
    {
        var duplicate = ("doc-1", "source-1", 4, 12);
        Assert.True(PhaseBContracts.HasDuplicateGoldIdentity([duplicate, duplicate]));
        Assert.False(PhaseBContracts.HasDuplicateGoldIdentity([duplicate, ("doc-1", "source-1", 5, 12)]));
    }

    [Fact]
    public void MetricsRequireEligiblePositiveDenominator()
    {
        Assert.True(PhaseBContracts.IsMetricMeasurable(10, eligible: true));
        Assert.False(PhaseBContracts.IsMetricMeasurable(0, eligible: true));
        Assert.False(PhaseBContracts.IsMetricMeasurable(10, eligible: false));
    }

    [Fact]
    public void HoldoutMustBeFrozenBlindAndHashed()
    {
        Assert.True(PhaseBContracts.IsBlindHoldoutFrozen("FROZEN", true, 2, true, true));
        Assert.False(PhaseBContracts.IsBlindHoldoutFrozen("NOT_AVAILABLE", false, 0, false, false));
        Assert.False(PhaseBContracts.IsBlindHoldoutFrozen("FROZEN", true, 2, true, false));
    }
}
