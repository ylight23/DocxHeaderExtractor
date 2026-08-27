using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfProductionOccurrenceResolverTests
{
    [Fact]
    public void PrefersOnlyEligibleBodyOccurrenceOverTocAndTableCopies()
    {
        var report = PdfProductionOccurrenceResolver.Resolve([
            Candidate("toc", 2, "Overview", "table_of_contents"),
            Candidate("table", 6, "Overview", "table", negative: ["table_scope"]),
            Candidate("body", 10, "Overview", "document_body", positive: ["standalone", "layout_prominence"]),
        ]);

        var decision = Assert.Single(report.Families);
        Assert.Equal(PdfOccurrenceResolution.Unique, decision.Resolution);
        Assert.Equal("body", decision.PreferredCandidateId);
        Assert.True(report.FindCandidate("body")!.Preferred);
        Assert.False(report.FindCandidate("toc")!.Eligible);
    }

    [Fact]
    public void LeavesEquivalentBodyOccurrencesAmbiguous()
    {
        var report = PdfProductionOccurrenceResolver.Resolve([
            Candidate("first", 5, "Financial Highlights", "document_body", positive: ["standalone"]),
            Candidate("second", 12, "Financial Highlights", "document_body", positive: ["standalone"]),
        ]);

        var decision = Assert.Single(report.Families);
        Assert.Equal(PdfOccurrenceResolution.Ambiguous, decision.Resolution);
        Assert.Null(decision.PreferredCandidateId);
    }

    [Fact]
    public void IsDeterministicWithoutAnyGoldInput()
    {
        var candidates = new[]
        {
            Candidate("one", 1, "Appendix", "document_body", positive: ["standalone"]),
            Candidate("two", 2, "Appendix", "document_body", positive: ["standalone"]),
        };

        var first = PdfProductionOccurrenceResolver.Resolve(candidates);
        var second = PdfProductionOccurrenceResolver.Resolve(candidates);

        Assert.Equal(first.CandidateCount, second.CandidateCount);
        Assert.Equal(first.AmbiguousCount, second.AmbiguousCount);
        Assert.Equal(Assert.Single(first.Families).FamilyKey, Assert.Single(second.Families).FamilyKey);
        Assert.Equal(PdfOccurrenceResolution.Ambiguous, Assert.Single(first.Families).Resolution);
    }

    [Fact]
    public void PublicResolverContractAcceptsOnlyCandidateFactsAndDoesNotMutateScores()
    {
        var candidate = Candidate("heading", 3, "Section I Overview", "document_body",
            positive: ["standalone", "layout_prominence"]);
        var score = candidate.CandidateScore;
        var escalation = candidate.EscalationScore;

        var report = PdfProductionOccurrenceResolver.Resolve([candidate]);
        var resolve = typeof(PdfProductionOccurrenceResolver).GetMethod(nameof(PdfProductionOccurrenceResolver.Resolve))!;

        Assert.Single(resolve.GetParameters());
        Assert.Equal(typeof(IReadOnlyList<RankedCandidate>), resolve.GetParameters()[0].ParameterType);
        Assert.Equal(typeof(PdfProductionOccurrenceReport), resolve.ReturnType);
        Assert.Equal(score, candidate.CandidateScore);
        Assert.Equal(escalation, candidate.EscalationScore);
        Assert.Equal(PdfOccurrenceResolution.Unique, Assert.Single(report.Families).Resolution);
    }

    private static RankedCandidate Candidate(string id, int page, string text, string scope,
        IReadOnlyList<string>? positive = null, IReadOnlyList<string>? negative = null) =>
        new(id, page, text, .5, .5, ModelTier.Medium, positive ?? [], negative ?? [], [], scope);
}
