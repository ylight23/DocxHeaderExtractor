using DocxHeaderExtractor.Eval;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M10.1e-0b-2b locks. A gold heading is ranked by the candidates built from the source lines it
/// occupies, never by a candidate that merely contains its words.
/// </summary>
public sealed class PdfOccurrenceRankTests
{
    /// <summary>
    /// The heading reads across three lines because the renderer emitted its commas separately. The
    /// candidate that represents it does not carry that punctuation line, so requiring it would
    /// reject the only candidate that gets the occurrence right.
    /// </summary>
    [Fact]
    public void ACandidateMissingOnlyThePunctuationLineStillCoversTheOccurrence()
    {
        var occurrence = Occurrence(
            (5730, "SECTION XIX: FISCAL YEAR ANNOUNCEMENTS AND ALLOCATION OF NET"),
            (5731, ", ,"),
            (5732, "INCOME"));
        var candidate = Provenance("s-window-6683", [5730, 5732], PdfCandidateRepresentationKind.WindowFragment);

        Assert.Equal([5730, 5732], occurrence.RequiredLines.Select(line => line.Index));
        Assert.True(candidate.Covers(occurrence.RequiredLines.Select(line => line.Index)));
    }

    /// <summary>
    /// The section's subheadings repeat its words. Covering them is not covering the heading, and a
    /// rule that compared text rather than lines could not tell the difference.
    /// </summary>
    [Fact]
    public void ACandidateBuiltFromTheSubheadingsDoesNotCoverTheHeading()
    {
        var occurrence = Occurrence(
            (5730, "SECTION XIX: FISCAL YEAR ANNOUNCEMENTS AND ALLOCATION OF NET"),
            (5731, ", ,"),
            (5732, "INCOME"));
        var candidate = Provenance("s-window-other", [5733, 5737, 5742], PdfCandidateRepresentationKind.WindowFragment);

        Assert.False(candidate.Covers(occurrence.RequiredLines.Select(line => line.Index)));
    }

    /// <summary>A candidate may carry more than the heading and still represent it.</summary>
    [Fact]
    public void ACandidateCarryingTrailingBodyLinesStillCoversTheOccurrence()
    {
        var occurrence = Occurrence((5295, "SECTION XIV: RECONCILIATIONS OF COMPONENTS OF ALLOCABLE"), (5296, "INCOME"));
        var candidate = Provenance("s-window-6095", [5295, 5296, 5297, 5298], PdfCandidateRepresentationKind.WindowFragment);

        Assert.True(candidate.Covers(occurrence.RequiredLines.Select(line => line.Index)));
    }

    [Fact]
    public void PartialCoverageIsNotCoverage()
    {
        var occurrence = Occurrence((5295, "SECTION XIV: RECONCILIATIONS OF COMPONENTS OF ALLOCABLE"), (5296, "INCOME"));
        var candidate = Provenance("b217", [5295], PdfCandidateRepresentationKind.StandardBlock);

        Assert.False(candidate.Covers(occurrence.RequiredLines.Select(line => line.Index)));
    }

    /// <summary>
    /// The diagnostic snapshot must not perturb the computation it observes: production calls the
    /// audit, evaluation calls the snapshot, and both have to describe the same ranking.
    /// </summary>
    [Fact]
    public void TheSnapshotReportsExactlyTheRankingTheAuditReports()
    {
        var docx = Path.Combine(RepositoryRoot(),
            @"todo10_8\heading_corpus_95_word\03_tai_chinh_ke_toan\054_IBRD_Information_Statement_FY25.docx");
        if (!File.Exists(docx)) return;

        var audit = PdfLayoutEvidenceOutline.BuildCandidateRankingAudit(docx);
        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docx);

        Assert.Equal(audit.Status, snapshot.Audit.Status);
        Assert.Equal(audit.CandidateCount, snapshot.Audit.CandidateCount);
        Assert.Equal(
            audit.Candidates.Select(candidate => candidate.SourceId),
            snapshot.Audit.Candidates.Select(candidate => candidate.SourceId));
        Assert.All(snapshot.Audit.Candidates, candidate =>
            Assert.True(snapshot.Provenance.ContainsKey(candidate.SourceId)));
    }

    private static PdfReviewedOccurrence Occurrence(params (int Index, string Text)[] lines) =>
        new("body[1]/p[1]", "gold", 84,
            lines.Select(line => new PdfReviewedOccurrenceLine(line.Index, $"84|0|0|0|{line.Text}", line.Text)).ToArray(),
            "reviewed", "reviewed_multiline_occurrence", 0);

    private static PdfCandidateProvenance Provenance(string id, int[] indexes, PdfCandidateRepresentationKind kind) =>
        new(id, indexes, indexes.Select(index => $"line-{index}").ToArray(), kind);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocxHeaderExtractor.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
