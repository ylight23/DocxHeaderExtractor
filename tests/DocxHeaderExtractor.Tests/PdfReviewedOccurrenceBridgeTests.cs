using System.Security.Cryptography;
using DocxHeaderExtractor.Eval;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using UglyToad.PdfPig;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M10.1e-0b-2a locks. The bridge says where a reviewed gold heading occurs in the rendered PDF. It
/// is reviewed data: nothing may be inferred from it at evaluation time, and it may not be used
/// against inputs it was not reviewed against.
/// </summary>
public sealed class PdfReviewedOccurrenceBridgeTests
{
    private const string Docx = @"todo10_8\heading_corpus_95_word\03_tai_chinh_ke_toan\054_IBRD_Information_Statement_FY25.docx";
    private const string Pdf = @"todo10_8\heading_corpus_100\03_tai_chinh_ke_toan\054_IBRD_Information_Statement_FY25.pdf";
    private const string Key = @"keys\rebased\054_IBRD_Information_Statement_FY25.v3-occurrence-reviewed.key";
    private const string Bridge = @"keys\occurrence-bridge\054_IBRD_Information_Statement_FY25.occurrence-bridge.json";

    /// <summary>
    /// The committed bridge must still describe the extraction it was reviewed against. This is what
    /// makes a reviewed line index safe to use: if the extractor, the PDF or the gold changes, the
    /// binding fails here rather than silently pointing at shifted lines.
    /// </summary>
    [Fact]
    public void CommittedBridgeStillBindsToItsReviewedInputs()
    {
        if (!TryLoad(out var bridge, out var lines, out var shas)) return;

        bridge.EnsureCurrent(shas.Docx, shas.Pdf, shas.Key,
            PdfOccurrenceBridgeProposal.ExtractionFingerprint(lines));
    }

    [Fact]
    public void EveryCommittedOccurrenceIsReviewed()
    {
        if (!TryLoad(out var bridge, out _, out _)) return;

        Assert.NotEmpty(bridge.Occurrences);
        Assert.All(bridge.Occurrences, item => Assert.Equal("reviewed", item.ReviewStatus));
        Assert.All(bridge.Occurrences, item => Assert.NotEmpty(item.Lines));
    }

    /// <summary>
    /// Where the renderer emitted a heading's punctuation as its own line, a producer that drops it
    /// is still representing the heading. Requiring that line would reject the only candidate that
    /// gets the occurrence right, so it stays evidence and is not required coverage.
    /// </summary>
    [Fact]
    public void PunctuationOnlyLinesAreEvidenceButNotRequiredCoverage()
    {
        if (!TryLoad(out var bridge, out _, out _)) return;

        var split = bridge.Occurrences.Single(item => item.GoldText.StartsWith("SECTION XIX", StringComparison.Ordinal));

        Assert.Equal(3, split.Lines.Count);
        Assert.Contains(split.Lines, line => line.Text.Trim() == ", ,");
        Assert.Equal(2, split.RequiredLines.Count);
        Assert.DoesNotContain(split.RequiredLines, line => line.Text.Trim() == ", ,");
    }

    /// <summary>A heading broken across lines is one occurrence made of several lines, not several.</summary>
    [Fact]
    public void AHeadingBrokenAcrossLinesIsOneOccurrence()
    {
        if (!TryLoad(out var bridge, out _, out _)) return;

        var split = bridge.Occurrences.Single(item => item.GoldText.StartsWith("SECTION XIV", StringComparison.Ordinal));

        Assert.Equal(["SECTION XIV: RECONCILIATIONS OF COMPONENTS OF ALLOCABLE", "INCOME"],
            split.Lines.Select(line => line.Text));
        Assert.Equal("reviewed_multiline_occurrence", split.ReviewMethod);
    }

    [Fact]
    public void StaleInputsAreRefusedRatherThanReused()
    {
        if (!TryLoad(out var bridge, out var lines, out var shas)) return;

        var error = Assert.Throws<InvalidOperationException>(() => bridge.EnsureCurrent(
            shas.Docx, shas.Pdf, shas.Key, "a-different-extraction-fingerprint"));

        Assert.Contains("stale_occurrence_bridge", error.Message, StringComparison.Ordinal);
        Assert.Contains("pdfLineExtractionFingerprint", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyReviewedOccurrencesAreVisibleToLookup()
    {
        var proposed = new PdfReviewedOccurrence("body[1]/p[1]", "A heading", 1,
            [new PdfReviewedOccurrenceLine(0, "1|0|0|0|A heading", "A heading")], "proposed", "unique_exact_whole_line", 1);
        var bridge = new PdfReviewedOccurrenceBridge("doc", "a", "b", "c", "d", [proposed]);

        Assert.Null(bridge.Find("body[1]/p[1]"));
    }

    private static bool TryLoad(
        out PdfReviewedOccurrenceBridge bridge,
        out IReadOnlyList<PdfLine> lines,
        out (string Docx, string Pdf, string Key) shas)
    {
        bridge = null!;
        lines = [];
        shas = default;
        var root = RepositoryRoot();
        string Full(string relative) => Path.Combine(root, relative);
        if (!File.Exists(Full(Bridge)) || !File.Exists(Full(Pdf)) || !File.Exists(Full(Docx)) || !File.Exists(Full(Key)))
            return false;

        bridge = PdfReviewedOccurrenceBridge.Load(File.ReadAllText(Full(Bridge)));
        using var document = PdfDocument.Open(Full(Pdf));
        lines = PdfLineExtraction.ExtractLines(document);
        shas = (Sha(Full(Docx)), Sha(Full(Pdf)), Sha(Full(Key)));
        return true;
    }

    private static string Sha(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocxHeaderExtractor.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
