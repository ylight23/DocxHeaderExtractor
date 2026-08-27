using System.Text.Json;
using DocxHeaderExtractor.Core.Eval;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M8.1d-3 locks. The counterfactual answers "which ancestors would actually be present" and
/// nothing else: it must not choose between competing candidates, must not reach across a scope or
/// regime boundary, and must not report a decision the frozen artifact does not contain.
/// </summary>
public sealed class PdfMarkerAncestryCounterfactualTests
{
    [Fact]
    public void UniqueEarlierPrefixInSameScopeIsStructurallySupported()
    {
        var report = Evaluate(
            Fact("a", 0, "4 Constructing Responses"),
            Fact("b", 1, "4 3 Validation"));

        var item = Assert.Single(report.Items);
        Assert.Equal("4.3", item.Recovered.HypotheticalPath);
        Assert.Equal("supported", item.ImmediatePrefix.Status);
        Assert.Equal("a", item.ImmediatePrefix.CandidateId);
    }

    [Fact]
    public void MissingPrefixIsUnsupported()
    {
        var report = Evaluate(
            Fact("a", 0, "13 Something entirely different"),
            Fact("b", 1, "13 00 14 00 Lunch break"));

        var item = Assert.Single(report.Items);
        Assert.Equal("unsupported", item.ImmediatePrefix.Status);
        Assert.Null(item.ImmediatePrefix.CandidateId);
        // A shallow ancestor existing by coincidence is not chain support. Only "supported" is
        // support; "partial" must never be read as a licence to link.
        Assert.Equal("partial", item.FullChain.Status);
        Assert.NotEqual(item.FullChain.RequiredPrefixes.Count, item.FullChain.ResolvedPrefixes.Count);
    }

    /// <summary>Two identical prefixes are a collision, not an invitation to pick the nearest.</summary>
    [Fact]
    public void DuplicatePrefixIsAmbiguousAndSelectsNothing()
    {
        var report = Evaluate(
            Fact("a", 0, "4 Constructing Responses"),
            Fact("b", 1, "4 Constructing Responses again"),
            Fact("c", 2, "4 3 Validation"));

        var item = Assert.Single(report.Items);
        Assert.Equal("ambiguous", item.ImmediatePrefix.Status);
        Assert.Equal(2, item.ImmediatePrefix.PrefixCandidateCount);
        Assert.Null(item.ImmediatePrefix.CandidateId);
    }

    [Fact]
    public void PrefixInAnotherScopeDoesNotSupportAncestry()
    {
        var report = Evaluate(
            Fact("a", 0, "4 Constructing Responses", scope: "table_of_contents"),
            Fact("b", 1, "4 3 Validation"));

        var item = Assert.Single(report.Items);
        Assert.Equal("unsupported", item.ImmediatePrefix.Status);
    }

    [Fact]
    public void PrefixMustPrecedeChildInSourceOrder()
    {
        var report = Evaluate(
            Fact("a", 0, "4 3 Validation"),
            Fact("b", 1, "4 Constructing Responses"));

        var item = Assert.Single(report.Items);
        Assert.Equal("unsupported", item.ImmediatePrefix.Status);
    }

    /// <summary>Immediate prefix present but an intermediate ancestor missing is partial, not supported.</summary>
    [Fact]
    public void FullChainReportsPartialWhenAnIntermediateAncestorIsAbsent()
    {
        var report = Evaluate(
            Fact("a", 0, "4 3 Validation"),
            Fact("b", 1, "4 3 2 Handling a Received Validation Request"));

        // Both facts are multi-component, so both are eligible; the child is the one under test.
        var item = report.Items.Single(entry => entry.Recovered.HypotheticalPath == "4.3.2");
        Assert.Equal("supported", item.ImmediatePrefix.Status);
        Assert.Equal("partial", item.FullChain.Status);
        Assert.Equal(["4", "4.3"], item.FullChain.RequiredPrefixes);
        Assert.Equal(["4.3"], item.FullChain.ResolvedPrefixes);
    }

    [Fact]
    public void SingleComponentMarkersAreNotEligible()
    {
        var report = Evaluate(
            Fact("a", 0, "4 Constructing Responses"),
            Fact("b", 1, "5 Something else"));

        Assert.Empty(report.Items);
        Assert.Equal(0, report.EligibleRecoveredPaths);
    }

    /// <summary>The report mirrors frozen authority; it never substitutes the hypothesis for it.</summary>
    [Fact]
    public void CurrentAuthorityIsReportedExactlyAsFrozen()
    {
        var report = Evaluate(
            Fact("a", 0, "4 Constructing Responses"),
            Fact("b", 1, "4 3 Validation"));

        var item = Assert.Single(report.Items);
        Assert.Equal("4", item.Current.MarkerPath);
        Assert.Null(item.Current.MarkerPrefixParentCandidate);
        Assert.Equal("4.3", item.Recovered.HypotheticalPath);
        Assert.NotEqual(item.Current.MarkerPath, item.Recovered.HypotheticalPath);
    }

    private static PdfMarkerCounterfactualReport Evaluate(params object[] facts) =>
        PdfMarkerAncestryCounterfactual.Evaluate(JsonSerializer.Serialize(new
        {
            rows = new[] { new { hierarchyFacts = new { items = facts } } },
        }));

    /// <summary>
    /// Frozen-artifact shape. MarkerPath deliberately carries the strict truncated value so the
    /// fixture reproduces what a real pre-repair artifact contains.
    /// </summary>
    private static object Fact(string id, int order, string text, string scope = "document_body") => new
    {
        Id = id,
        FactId = id,
        SourceOrder = order,
        Page = 1,
        StructuralScope = scope,
        DocumentRegime = "document_body",
        MarkerFamily = "spaced_arabic",
        MarkerPath = text.Split(' ')[0],
        MarkerPrefixParentCandidate = (string?)null,
        ResolvedLevel = (int?)1,
        ParentResolution = "relationship_unresolved",
        Evidence = Array.Empty<string>(),
        SourceBlockText = text,
        MarkerComponents = Array.Empty<int>(),
    };
}
