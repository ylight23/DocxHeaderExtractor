using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using UglyToad.PdfPig;

namespace DocxHeaderExtractor.Eval.Accuracy99;

public enum IdentityGoldTextMatch
{
    [JsonStringEnumMemberName("VERBATIM_EXACT")] VerbatimExact,
    [JsonStringEnumMemberName("PDF_LAYOUT_CANONICAL_EQUIVALENT")] PdfLayoutCanonicalEquivalent,
}

public sealed record PdfSourceTextComparison(
    bool Matches,
    IdentityGoldTextMatch? MatchKind,
    string RawVerbatimText,
    string CanonicalComparisonText,
    IReadOnlyList<string> NormalizationsApplied);

/// <summary>
/// A deliberately narrow PDF comparison layer. It preserves raw parser text and
/// only accepts whitespace/run-fragmentation equivalence backed by parser geometry.
/// It does not remove punctuation, use edit distance, or infer semantic similarity.
/// </summary>
public static class PdfSourceTextCanonicalizer
{
    public static PdfSourceTextComparison Compare(string expectedText, string rawText, string? parserCanonicalText = null)
    {
        expectedText ??= string.Empty;
        rawText ??= string.Empty;
        if (string.Equals(expectedText, rawText, StringComparison.Ordinal))
            return new(true, IdentityGoldTextMatch.VerbatimExact, rawText, rawText, []);

        var expected = CollapsePdfWhitespace(expectedText);
        var raw = CollapsePdfWhitespace(rawText);
        var parser = CollapsePdfWhitespace(parserCanonicalText ?? string.Empty);
        if (parser.Length == 0) parser = raw;

        if (!string.Equals(expected, parser, StringComparison.Ordinal))
            return new(false, null, rawText, parser, []);

        var normalizations = new List<string>();
        if (!string.Equals(raw, parser, StringComparison.Ordinal))
            normalizations.Add("REJOIN_PDF_TEXT_RUN_FRAGMENTATION");
        if (!string.Equals(rawText, raw, StringComparison.Ordinal))
            normalizations.Add("COLLAPSE_PDF_LAYOUT_WHITESPACE");
        if (normalizations.Count == 0)
            normalizations.Add("COLLAPSE_PDF_LAYOUT_WHITESPACE");

        return new(true, IdentityGoldTextMatch.PdfLayoutCanonicalEquivalent, rawText, parser, normalizations);
    }

    public static string CollapsePdfWhitespace(string value)
    {
        var result = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character) || character is '\u00A0' or '\u202F')
            {
                pendingSpace = result.Length > 0;
                continue;
            }

            if (pendingSpace) result.Append(' ');
            result.Append(character);
            pendingSpace = false;
        }
        return result.ToString().Trim();
    }
}

public sealed record PdfRecoveredBoundingBox(
    double Left,
    double Bottom,
    double Right,
    double Top);

public sealed record PdfRecoveredOccurrence(
    string SourceSha256,
    int Page,
    int ReadingOrder,
    int PageLineOrdinal,
    IReadOnlyList<string> NativeRunIds,
    string RawVerbatimText,
    string CanonicalComparisonText,
    PdfRecoveredBoundingBox? BoundingBox,
    string TextSha256,
    string StableOccurrenceId,
    IReadOnlyList<string> NormalizationsApplied);

internal sealed record PdfLocatedLine(
    PdfLine Line,
    int ReadingOrder,
    int PageLineOrdinal,
    PdfSourceTextComparison Comparison);

/// <summary>Locates only parser-backed line matches; it does not infer a Gold occurrence.</summary>
internal static class PdfOccurrenceLocator
{
    public static IReadOnlyList<PdfLocatedLine> Locate(
        IReadOnlyList<PdfLine> lines,
        string expectedText)
    {
        var pageLineOrdinals = new Dictionary<int, int>();
        var located = new List<PdfLocatedLine>();

        for (var readingOrder = 0; readingOrder < lines.Count; readingOrder++)
        {
            var line = lines[readingOrder];
            pageLineOrdinals.TryGetValue(line.Page, out var pageLineOrdinal);
            pageLineOrdinals[line.Page] = pageLineOrdinal + 1;
            var comparison = PdfSourceTextCanonicalizer.Compare(expectedText, line.Text, line.MatchText);
            if (!comparison.Matches) continue;
            located.Add(new(line, readingOrder, pageLineOrdinal, comparison));
        }

        return located;
    }
}

/// <summary>Creates IDs from verified PDF source facts only; no Gold or semantic label enters.</summary>
public static class StablePdfOccurrenceIdFactory
{
    public static string Create(
        string sourceSha256,
        int page,
        int readingOrder,
        int pageLineOrdinal,
        double y,
        double left,
        double right,
        string rawText)
    {
        var stableKey = string.Join("|", [
            sourceSha256,
            page.ToString(CultureInfo.InvariantCulture),
            readingOrder.ToString(CultureInfo.InvariantCulture),
            pageLineOrdinal.ToString(CultureInfo.InvariantCulture),
            y.ToString("R", CultureInfo.InvariantCulture),
            left.ToString("R", CultureInfo.InvariantCulture),
            right.ToString("R", CultureInfo.InvariantCulture),
            Sha256(rawText),
        ]);
        return "pdf-derived:" + Sha256(stableKey);
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

/// <summary>
/// Reconstructs source-backed PDF line occurrences from PdfPig's lower-level
/// letter/layout-derived lines. Stable IDs contain source facts only.
/// </summary>
public static class PdfRawOccurrenceRecovery
{
    public static IReadOnlyList<PdfRecoveredOccurrence> Recover(string pdfPath, string expectedText)
    {
        if (!File.Exists(pdfPath)) throw new FileNotFoundException("PDF source not found.", pdfPath);

        var sourceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pdfPath))).ToLowerInvariant();
        using var document = PdfDocument.Open(pdfPath);
        var lines = PdfLineExtraction.ExtractLines(document);
        var recovered = new List<PdfRecoveredOccurrence>();

        foreach (var located in PdfOccurrenceLocator.Locate(lines, expectedText))
        {
            var line = located.Line;
            if (line.Bottom is not { } bottom || line.Top is not { } top)
                continue;
            var bbox = new PdfRecoveredBoundingBox(line.Left, bottom, line.Right, top);
            var rawTextHash = Sha256(line.Text);
            recovered.Add(new PdfRecoveredOccurrence(
                sourceSha256,
                line.Page,
                located.ReadingOrder,
                located.PageLineOrdinal,
                [],
                line.Text,
                located.Comparison.CanonicalComparisonText,
                bbox,
                rawTextHash,
                StablePdfOccurrenceIdFactory.Create(sourceSha256, line.Page, located.ReadingOrder,
                    located.PageLineOrdinal, line.Y, line.Left, line.Right, line.Text),
                located.Comparison.NormalizationsApplied));
        }

        return recovered;
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed record PdfIdentityForensicCandidate(
    string TargetText,
    int Page,
    int ReadingOrder,
    int PageLineOrdinal,
    string RawText,
    string? ParserCanonicalText,
    string ComparisonText,
    string MatchClassification,
    IReadOnlyList<string> NormalizationsApplied,
    string TextSha256,
    PdfRecoveredBoundingBox? BoundingBox,
    string? StableOccurrenceId,
    string? PreviousRawText,
    string? NextRawText)
{
    public string RawTextEscaped => Escape(RawText);

    public IReadOnlyList<string> RawTextCodePoints => RawText
        .EnumerateRunes()
        .Select(rune => $"U+{rune.Value:X4}")
        .ToArray();

    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal);
}

public sealed record PdfIdentityForensicReport(
    string SourcePath,
    string SourceSha256,
    IReadOnlyList<PdfIdentityForensicCandidate> Candidates,
    string ParserOrdering,
    bool GoldUsed,
    bool ModelOutputsUsed,
    bool ResidualHintsUsed);

/// <summary>Produces source-only forensic evidence; candidates are diagnostic and never bindings.</summary>
public static class PdfIdentityForensicBuilder
{
    public static PdfIdentityForensicReport Build(string pdfPath, IReadOnlyList<string> targetTexts)
    {
        if (!File.Exists(pdfPath)) throw new FileNotFoundException("PDF source not found.", pdfPath);
        var sourceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pdfPath))).ToLowerInvariant();
        using var document = PdfDocument.Open(pdfPath);
        var lines = PdfLineExtraction.ExtractLines(document);
        var pageLineOrdinals = new Dictionary<int, int>();
        var candidates = new List<PdfIdentityForensicCandidate>();

        for (var readingOrder = 0; readingOrder < lines.Count; readingOrder++)
        {
            var line = lines[readingOrder];
            pageLineOrdinals.TryGetValue(line.Page, out var pageLineOrdinal);
            pageLineOrdinals[line.Page] = pageLineOrdinal + 1;
            foreach (var target in targetTexts)
            {
                var comparison = PdfSourceTextCanonicalizer.Compare(target, line.Text, line.MatchText);
                var diagnosticContains = PdfTextUtilities.CanonicalForMatch(line.Text)
                    .Contains(PdfTextUtilities.CanonicalForMatch(target), StringComparison.Ordinal);
                if (!comparison.Matches && !diagnosticContains) continue;

                var classification = comparison.Matches
                    ? comparison.MatchKind == IdentityGoldTextMatch.VerbatimExact ? "VERBATIM_EXACT" : "PDF_LAYOUT_CANONICAL_EQUIVALENT"
                    : "DIAGNOSTIC_CANONICAL_CONTAINS_ONLY";
                var comparisonText = comparison.Matches
                    ? comparison.CanonicalComparisonText
                    : PdfTextUtilities.CanonicalForMatch(line.Text);
                candidates.Add(new PdfIdentityForensicCandidate(
                    target,
                    line.Page,
                    readingOrder,
                    pageLineOrdinal,
                    line.Text,
                    line.MatchText,
                    comparisonText,
                    classification,
                    comparison.NormalizationsApplied,
                    Sha256(line.Text),
                    line.Bottom is { } bottom && line.Top is { } top
                        ? new PdfRecoveredBoundingBox(line.Left, bottom, line.Right, top)
                        : null,
                    comparison.Matches
                        ? StablePdfOccurrenceIdFactory.Create(sourceSha256, line.Page, readingOrder,
                            pageLineOrdinal, line.Y, line.Left, line.Right, line.Text)
                        : null,
                    readingOrder == 0 ? null : lines[readingOrder - 1].Text,
                    readingOrder + 1 >= lines.Count ? null : lines[readingOrder + 1].Text));
            }
        }

        return new PdfIdentityForensicReport(
            Path.GetFullPath(pdfPath),
            sourceSha256,
            candidates,
            "page ascending, line Y descending, line left ascending",
            false,
            false,
            false);
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
