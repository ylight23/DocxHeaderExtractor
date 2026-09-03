using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfBookmarkOutlineTests
{
    [Fact]
    public void Raw_bookmark_probe_reads_author_declared_destinations_without_enabling_route()
    {
        var root = RepositoryRoot();
        var pdf = Path.Combine(
            root, "todo10_8", "heading_corpus_100", "05_bien_ban_hop",
            "077_ICP_TAG_Minutes_Nov_2023.pdf");
        if (!File.Exists(pdf)) return;

        var report = PdfBookmarkProbe.Analyze(pdf);

        Assert.Equal("ok", report.Status);
        Assert.Collection(report.Candidates,
            first => { Assert.Equal("Welcome and meeting objectives", first.Title); Assert.Equal(1, first.Page); },
            second => { Assert.Equal("Session I: Update on the ICP 2021 Cycle", second.Title); Assert.Equal(1, second.Page); },
            third => Assert.Equal(3, third.Page),
            fourth => Assert.Equal(4, fourth.Page),
            fifth => Assert.Equal(7, fifth.Page),
            sixth => Assert.Equal(9, sixth.Page),
            seventh => Assert.Equal(9, seventh.Page));
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DocxHeaderExtractor.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Cannot find repository root.");
    }
}
