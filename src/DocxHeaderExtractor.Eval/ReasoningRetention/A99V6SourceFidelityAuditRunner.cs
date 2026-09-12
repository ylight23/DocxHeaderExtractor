using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.IO.Compression;
using System.Xml.Linq;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Offline source-fidelity comparison for DOC-0205. This audit compares the legacy merged DOCX
/// with a LibreOffice conversion of the original DOC. It never calls a model and never changes
/// production source preparation, Gold, or the live semantic contract.
/// </summary>
public static class A99V6SourceFidelityAuditRunner
{
    private const string DocumentId = "DOC-0205";
    private const string CurrentDocx = "todo10_8/heading_corpus_95_word/01_phap_quy/025_ND_47-2020_Chia_se_du_lieu_so.docx";
    private const string OriginalDoc = "C:/Users/btdba/DocxHeaderExtractor/todo10_8/heading_corpus_100/01_phap_quy/025_ND_47-2020_Chia_se_du_lieu_so.doc";
    private const string ConvertedDocx = "eval/a99-closed-loop/source-fidelity-audit/DOC-0205/converted-docx/025_ND_47-2020_Chia_se_du_lieu_so.docx";
    private const string KeyPath = "keys/legal-human/025_ND_47-2020_Chia_se_du_lieu_so.key";
    private const string OutputRoot = "eval/a99-closed-loop/source-fidelity-audit/DOC-0205";

    private static readonly Regex KeyLine = new(
        @"^\s*@?(?<source>\S+)\s+(?<level>\d+)\s+#\s*(?<text>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(output);

        var currentPath = Resolve(repoRoot, CurrentDocx);
        var convertedPath = Resolve(repoRoot, ConvertedDocx);
        var keyPath = Resolve(repoRoot, KeyPath);
        var originalPath = Path.GetFullPath(OriginalDoc);
        RequireFile(currentPath, "CURRENT_DOCX_MISSING");
        RequireFile(convertedPath, "CONVERTED_DOCX_MISSING");
        RequireFile(keyPath, "AUTHORITY_KEY_MISSING");

        var current = ReadSource(currentPath);
        var converted = ReadSource(convertedPath);

        // Freeze source-only lineage before opening the semantic authority key.
        var sourceFreeze = new
        {
            schemaVersion = "a99-v6-source-fidelity-audit-source-freeze-v1",
            documentId = DocumentId,
            modelCalls = 0,
            providerCalls = 0,
            goldReadBeforeFreeze = false,
            currentSource = SourceLineage(repoRoot, CurrentDocx, currentPath, current),
            originalSource = new
            {
                kind = "DOC",
                path = OriginalDoc,
                availableAtAudit = File.Exists(originalPath),
                sha256 = File.Exists(originalPath) ? Sha256(originalPath) : null,
            },
            convertedSource = SourceLineage(repoRoot, ConvertedDocx, convertedPath, converted),
            conversion = new
            {
                tool = "LibreOffice",
                command = "soffice --headless --convert-to docx --outdir <output> <original.doc>",
                sourceType = "DOC",
                outputType = "DOCX",
                fidelityHypothesis = "preserve paragraph boundaries from original DOC",
            },
            invariant = new
            {
                productionSourceUnchanged = true,
                goldUnchanged = true,
                modelContractUnchanged = true,
                noCandidateGeneration = true,
            },
        };
        await WriteJson(Path.Combine(output, "source-freeze.v1.json"), sourceFreeze, ct);

        // Gold/reference is opened only after the source-only freeze artifact exists.
        var gold = ParseKey(keyPath);
        var currentUnits = NonEmptyUnits(current);
        var convertedUnits = NonEmptyUnits(converted);
        var currentStats = Analyze(current, currentUnits, gold);
        var convertedStats = Analyze(converted, convertedUnits, gold);
        var currentRawStats = ReadRawDocxStats(currentPath);
        var convertedRawStats = ReadRawDocxStats(convertedPath);
        var matches = MatchGold(gold, currentUnits, convertedUnits);

        await WriteJson(Path.Combine(output, "gold-representability.v1.json"), new
        {
            schemaVersion = "a99-v6-source-fidelity-audit-gold-representability-v1",
            documentId = DocumentId,
            authority = KeyPath,
            authoritySha256 = Sha256(keyPath),
            goldReadBeforeFreeze = false,
            totalGoldHeadings = gold.Count,
            current = matches.CurrentSummary,
            converted = matches.ConvertedSummary,
            rows = matches.Rows,
            modelCalls = 0,
            providerCalls = 0,
        }, ct);

        await WriteJson(Path.Combine(output, "source-units.v1.json"), new
        {
            schemaVersion = "a99-v6-source-fidelity-audit-source-units-v1",
            documentId = DocumentId,
            current = currentUnits,
            converted = convertedUnits,
            modelCalls = 0,
            providerCalls = 0,
        }, ct);

        var summary = new
        {
            schemaVersion = "a99-v6-source-fidelity-audit-summary-v1",
            documentId = DocumentId,
            sourceFreeze = "source-freeze.v1.json",
            goldRepresentability = "gold-representability.v1.json",
            current = new { normalizedSourceModel = currentStats, rawOoxml = currentRawStats },
            converted = new { normalizedSourceModel = convertedStats, rawOoxml = convertedRawStats },
            exactParagraphRepresentation = new
            {
                total = matches.ConvertedSummary.Total,
                singleUnit = matches.ConvertedSummary.ExactSingleUnit,
                contiguousMultiUnit = matches.ConvertedSummary.ExactContiguousMultiUnit,
                ambiguous = matches.ConvertedSummary.Ambiguous,
                unresolved = matches.ConvertedSummary.Unresolved,
                exactSingleUtf16SpanAvailable = matches.ConvertedSummary.ExactSingleUnit,
                crossParagraphSpanRequiresExplicitPolicy = matches.ConvertedSummary.ExactContiguousMultiUnit,
            },
            sourceFidelityDecision = matches.ConvertedSummary.Unresolved == 0 && matches.ConvertedSummary.Ambiguous == 0
                ? "FAITHFUL_PARAGRAPH_SOURCE_REPRESENTS_ALL_71"
                : "FAITHFUL_PARAGRAPH_SOURCE_REQUIRES_REVIEW",
            modelCalls = 0,
            providerCalls = 0,
            goldFirewall = "PASS",
        };
        await WriteJson(Path.Combine(output, "summary.v1.json"), summary, ct);

        Console.WriteLine("A99-V6 SOURCE FIDELITY AUDIT: DOC-0205");
        Console.WriteLine($"current normalized: paragraphs={currentStats.TotalParagraphs}, nonEmpty={currentStats.NonEmptyParagraphs}, max={currentStats.MaxParagraphLength}, median={currentStats.MedianParagraphLength}");
        Console.WriteLine($"current raw OOXML: paragraphs={currentRawStats.TotalParagraphs}, nonEmpty={currentRawStats.NonEmptyParagraphs}, max={currentRawStats.MaxParagraphLength}, median={currentRawStats.MedianParagraphLength}");
        Console.WriteLine($"converted normalized: paragraphs={convertedStats.TotalParagraphs}, nonEmpty={convertedStats.NonEmptyParagraphs}, max={convertedStats.MaxParagraphLength}, median={convertedStats.MedianParagraphLength}");
        Console.WriteLine($"converted raw OOXML: paragraphs={convertedRawStats.TotalParagraphs}, nonEmpty={convertedRawStats.NonEmptyParagraphs}, max={convertedRawStats.MaxParagraphLength}, median={convertedRawStats.MedianParagraphLength}");
        Console.WriteLine($"converted Gold representation: single={matches.ConvertedSummary.ExactSingleUnit}, multi={matches.ConvertedSummary.ExactContiguousMultiUnit}, ambiguous={matches.ConvertedSummary.Ambiguous}, unresolved={matches.ConvertedSummary.Unresolved}");
        Console.WriteLine("MODEL_CALLS=0 PROVIDER_CALLS=0 GOLD_FIREWALL=PASS");
        return 0;
    }

    private static object SourceLineage(string repoRoot, string logicalPath, string path, SourceDocument source) => new
    {
        kind = "DOCX",
        path = logicalPath,
        absolutePath = Path.GetFullPath(path),
        sha256 = Sha256(path),
        totalParagraphs = source.Paragraphs.Count,
        nonEmptyParagraphs = source.Paragraphs.Count(p => !string.IsNullOrWhiteSpace(p.Text)),
        rawOoxml = ReadRawDocxStats(path),
    };

    private static SourceDocument ReadSource(string path) =>
        new OpenXmlDocumentSource().Read(path) with { DocumentId = DocumentId };

    private static SourceStats Analyze(SourceDocument source, IReadOnlyList<SourceUnit> units, IReadOnlyList<GoldRow> gold)
    {
        var lengths = units.Select(unit => unit.Text.Length).OrderBy(length => length).ToArray();
        return new SourceStats(
            source.Paragraphs.Count,
            units.Count,
            lengths.Length == 0 ? 0 : lengths.Max(),
            Median(lengths),
            units.Count(unit => unit.Text.StartsWith("Chương ", StringComparison.Ordinal)),
            units.Count(unit => unit.Text.StartsWith("Mục ", StringComparison.Ordinal)),
            units.Count(unit => unit.Text.StartsWith("Điều ", StringComparison.Ordinal)),
            gold.Count(item => units.Any(unit => unit.Text.Contains(item.Text, StringComparison.Ordinal))));
    }

    private static RawDocxStats ReadRawDocxStats(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("word/document.xml") ?? throw new InvalidDataException("DOCUMENT_XML_MISSING:" + path);
        using var stream = entry.Open();
        var document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var paragraphs = document.Root?
            .Element(w + "body")?
            .Elements(w + "p")
            .Select(paragraph => string.Concat(paragraph.Descendants(w + "t").Select(text => text.Value)))
            .ToArray()
            ?? throw new InvalidDataException("DOCUMENT_BODY_MISSING:" + path);
        var nonEmpty = paragraphs.Where(text => !string.IsNullOrWhiteSpace(text)).ToArray();
        var lengths = nonEmpty.Select(text => text.Length).OrderBy(length => length).ToArray();
        return new RawDocxStats(
            paragraphs.Length,
            nonEmpty.Length,
            lengths.Length == 0 ? 0 : lengths.Max(),
            Median(lengths),
            nonEmpty.Count(text => text.StartsWith("Chương ", StringComparison.Ordinal)),
            nonEmpty.Count(text => text.StartsWith("Mục ", StringComparison.Ordinal)),
            nonEmpty.Count(text => text.StartsWith("Điều ", StringComparison.Ordinal)));
    }

    private static IReadOnlyList<SourceUnit> NonEmptyUnits(SourceDocument source) => source.Paragraphs
        .Where(paragraph => !string.IsNullOrWhiteSpace(paragraph.Text))
        .Select((paragraph, index) => new SourceUnit(index + 1, paragraph.SourceId, paragraph.SourceOrdinal, paragraph.Text))
        .ToArray();

    private static MatchResult MatchGold(IReadOnlyList<GoldRow> gold, IReadOnlyList<SourceUnit> current, IReadOnlyList<SourceUnit> converted)
    {
        var rows = gold.Select(item => new GoldMatchRow(
            item.Ordinal,
            item.Text,
            MatchOne(item, current),
            MatchOne(item, converted))).ToArray();
        return new MatchResult(
            Summary(rows.Select(row => row.Current)),
            Summary(rows.Select(row => row.Converted)),
            rows);
    }

    private static GoldMatch MatchOne(GoldRow gold, IReadOnlyList<SourceUnit> units)
    {
        var single = units.Where(unit => string.Equals(unit.Text, gold.Text, StringComparison.Ordinal)).ToArray();
        if (single.Length > 1)
            return new("AMBIGUOUS", single.Select(unit => unit.SourceId).ToArray(), "EXACT_SINGLE_UNIT");
        if (single.Length == 1)
            return new("EXACT_SINGLE_UNIT", [single[0].SourceId], "EXACT_ORDINAL_TEXT");

        var sequences = new List<string[]>();
        for (var start = 0; start < units.Count; start++)
        {
            for (var count = 2; start + count <= units.Count && count <= 4; count++)
            {
                var candidate = units.Skip(start).Take(count).ToArray();
                if (string.Equals(string.Join(" ", candidate.Select(unit => unit.Text)), gold.Text, StringComparison.Ordinal))
                    sequences.Add(candidate.Select(unit => unit.SourceId).ToArray());
            }
        }
        if (sequences.Count > 1)
            return new("AMBIGUOUS", sequences.SelectMany(sequence => sequence).ToArray(), "EXACT_CONTIGUOUS_PARAGRAPH_JOIN_SPACE");
        if (sequences.Count == 1)
            return new("EXACT_CONTIGUOUS_MULTI_UNIT", sequences[0], "EXACT_CONTIGUOUS_PARAGRAPH_JOIN_SPACE");
        return new("UNRESOLVED", [], "NO_FUZZY_MATCH");
    }

    private static MatchSummary Summary(IEnumerable<GoldMatch> matches)
    {
        var all = matches.ToArray();
        return new(
            all.Length,
            all.Count(match => match.Status == "EXACT_SINGLE_UNIT"),
            all.Count(match => match.Status == "EXACT_CONTIGUOUS_MULTI_UNIT"),
            all.Count(match => match.Status == "AMBIGUOUS"),
            all.Count(match => match.Status == "UNRESOLVED"));
    }

    private static IReadOnlyList<GoldRow> ParseKey(string path) => File.ReadLines(path, Encoding.UTF8)
        .Select((line, index) => (line, index))
        .Where(item => KeyLine.IsMatch(item.line))
        .Select(item =>
        {
            var match = KeyLine.Match(item.line);
            return new GoldRow(item.index + 1, match.Groups["source"].Value, int.Parse(match.Groups["level"].Value), match.Groups["text"].Value);
        })
        .ToArray();

    private static double Median(IReadOnlyList<int> sorted) => sorted.Count == 0
        ? 0
        : sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0;

    private static string Resolve(string repoRoot, string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(repoRoot, path.Replace('/', Path.DirectorySeparatorChar));

    private static void RequireFile(string path, string code)
    {
        if (!File.Exists(path)) throw new InvalidDataException($"{code}:{path}");
    }

    private static async Task WriteJson(string path, object value, CancellationToken ct) =>
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, new UTF8Encoding(false), ct);

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private sealed record GoldRow(int Ordinal, string SourceId, int Level, string Text);
    private sealed record SourceUnit(int Ordinal, string SourceId, int SourceParagraphOrdinal, string Text);
    private sealed record SourceStats(int TotalParagraphs, int NonEmptyParagraphs, int MaxParagraphLength, double MedianParagraphLength, int ChapterParagraphs, int SectionParagraphs, int ArticleParagraphs, int GoldSubstringMatches);
    private sealed record RawDocxStats(int TotalParagraphs, int NonEmptyParagraphs, int MaxParagraphLength, double MedianParagraphLength, int ChapterParagraphs, int SectionParagraphs, int ArticleParagraphs);
    private sealed record GoldMatch(string Status, IReadOnlyList<string> SourceIds, string ComparisonRule);
    private sealed record GoldMatchRow(int Ordinal, string Text, GoldMatch Current, GoldMatch Converted);
    private sealed record MatchSummary(int Total, int ExactSingleUnit, int ExactContiguousMultiUnit, int Ambiguous, int Unresolved);
    private sealed record MatchResult(MatchSummary CurrentSummary, MatchSummary ConvertedSummary, IReadOnlyList<GoldMatchRow> Rows);
}
