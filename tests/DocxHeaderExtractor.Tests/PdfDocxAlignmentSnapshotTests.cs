using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M10.2 parity locks. The grounding snapshot exists to observe the alignment production runs, so the
/// only thing it is allowed to add is the record of how each block reached its paragraph. If a trace
/// could disagree with the headings beside it, an audit built on it would be describing a run that
/// never happened.
/// </summary>
public sealed class PdfDocxAlignmentSnapshotTests
{
    /// <summary>Every accepted trace names exactly the paragraph and span its heading was grounded to.</summary>
    [Fact]
    public void AcceptedTracesReconcileOneToOneWithTheAlignedHeadings()
    {
        var snapshot = Snapshot();
        if (snapshot is null) return;

        var accepted = snapshot.Trace.Where(entry => entry.Accepted).ToArray();
        Assert.Equal(snapshot.Headings.Count, accepted.Length);
        foreach (var heading in snapshot.Headings)
        {
            var entry = Assert.Single(accepted, item => item.SourceBlockId == heading.SourceId);
            Assert.Equal(heading.Index, entry.ParagraphIndex);
            Assert.Equal(heading.HeadingSpan?.Start, entry.Start);
        }
    }

    /// <summary>
    /// A block the matcher rejected still appears, marked unmatched. Silence would read as "every
    /// candidate was grounded", which is the reading this audit is meant to test.
    /// </summary>
    [Fact]
    public void EveryCandidateConsideredIsAccountedFor()
    {
        var snapshot = Snapshot();
        if (snapshot is null) return;

        Assert.Equal(
            snapshot.Trace.Select(entry => entry.SourceBlockId).Distinct().Count(),
            snapshot.Trace.Count);
        Assert.All(snapshot.Trace, entry => Assert.False(
            entry.Branch == PdfDocxMatchBranch.Unmatched && entry.ParagraphIndex is not null));
    }

    /// <summary>The observation is passive: running it twice must describe the same run.</summary>
    [Fact]
    public void TheSnapshotIsDeterministic()
    {
        var snapshot = Snapshot();
        if (snapshot is null) return;
        var again = Snapshot()!;

        Assert.Equal(snapshot.Status, again.Status);
        Assert.Equal(
            snapshot.Trace.Select(entry => (entry.SourceBlockId, entry.ParagraphIndex, entry.Start, entry.Branch)),
            again.Trace.Select(entry => (entry.SourceBlockId, entry.ParagraphIndex, entry.Start, entry.Branch)));
    }

    private static PdfDocxAlignmentSnapshot? Snapshot()
    {
        var docx = Path.Combine(RepositoryRoot(),
            @"todo10_8\heading_corpus_95_word\01_phap_quy\010_Luat_An_ninh_mang_24-2018-QH14.docx");
        if (!File.Exists(docx)) return null;
        return PdfLayoutEvidenceOutline.BuildDocxAlignmentSnapshot(docx, new DocxSlimExtractor().Extract(docx));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocxHeaderExtractor.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
