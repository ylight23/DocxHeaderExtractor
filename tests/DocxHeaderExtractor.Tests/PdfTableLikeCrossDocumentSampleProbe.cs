using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M10.5-B1. Freezes the review population before anyone reads a line, so the sample cannot drift
/// toward whatever happens to look like a heading.
/// <para>
/// The sample is stratified by whether the exposed line's block actually carries the `table_scope`
/// penalty. A uniform sample would not survive the corpus: 091 exposes 425 lines but penalises only
/// 50 blocks, so a small uniform draw would mostly miss the penalised stratum and could suggest that
/// exposure has no downstream cost when the question is precisely whether it does.
/// </para>
/// <para>
/// Order within each stratum is a SHA-256 of the occurrence identity, which is fixed by the document
/// and the line rather than by anything observed later. No manual picking, and the actual outcomes
/// are recorded here only so the trace in B3 reads them from the same frozen rows - the review in B2
/// assigns a role from the source, not from where a line landed.
/// </para>
/// </summary>
public sealed class PdfTableLikeCrossDocumentSampleProbe
{
    private const int SelectedBudget = 160;
    private const int PerStratum = 10;

    private static readonly string[] Documents =
    [
        @"02_hop_dong_mua_sam\032",
        @"03_tai_chinh_ke_toan\043",
        @"04_giao_trinh\063",
        @"07_system_generated\091",
    ];

    [Fact]
    public void Report()
    {
        var output = Environment.GetEnvironmentVariable("M10_5_B1_SAMPLE");
        if (string.IsNullOrWhiteSpace(output)) return;

        var corpus = Path.Combine(RepositoryRoot(), "todo10_8", "heading_corpus_95_word");
        var documents = new List<SampledDocument>();

        foreach (var prefix in Documents)
        {
            var directory = Path.Combine(corpus, Path.GetDirectoryName(prefix)!);
            var stem = Path.GetFileName(prefix);
            var path = Directory.Exists(directory)
                ? Directory.GetFiles(directory, stem + "_*.docx").FirstOrDefault()
                : null;
            if (path is null)
            {
                documents.Add(new SampledDocument(stem, "not_found", 0, 0, 0, []));
                continue;
            }
            documents.Add(Sample(stem, path));
        }

        var report = new SampleReport(
            Contract: "population_frozen_before_review; roles assigned from source, not from outcome",
            Stratification: "by whether the exposed line's best-ranked block carries the table_scope penalty",
            OrderWithinStratum: "sha256(document|page|readableText), ascending",
            PerStratum,
            SelectedBudget,
            documents);

        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        File.WriteAllText(output, JsonSerializer.Serialize(report, options));
    }

    private static SampledDocument Sample(string stem, string path)
    {
        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(path);
        var contexts = PdfCandidateContextBuilder.Build(snapshot.CandidateBlocks, snapshot.Annotations);
        var ranked = PdfCandidateRanker.Rank(snapshot.CandidateBlocks, contexts);
        var rankOf = ranked.Select((item, index) => (item.SourceId, Rank: index + 1))
            .ToDictionary(x => x.SourceId, x => x.Rank, StringComparer.Ordinal);
        var byId = ranked.ToDictionary(item => item.SourceId, StringComparer.Ordinal);
        var selected = ranked.Take(SelectedBudget).Select(item => item.SourceId).ToHashSet(StringComparer.Ordinal);

        var blocksByKey = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var block in snapshot.CandidateBlocks)
            foreach (var line in block.Lines)
            {
                var key = Key(line.Page, PdfTextUtilities.Readable(line.Text));
                if (!blocksByKey.TryGetValue(key, out var list)) blocksByKey[key] = list = [];
                list.Add(block.Id);
            }

        var exposed = snapshot.Annotations
            .Where(a => PdfLineBlockFilter.ClassifyTableLine(a.Line.Text) == "short_numbered" &&
                        PdfLineBlockAnnotation.HasStructuralMarker(a.Line.Text))
            .Select(a =>
            {
                var readable = PdfTextUtilities.Readable(a.Line.Text);
                var key = Key(a.Line.Page, readable);
                var block = blocksByKey.TryGetValue(key, out var list)
                    ? list.OrderBy(id => rankOf.GetValueOrDefault(id, int.MaxValue)).First()
                    : null;
                var candidate = block is null ? null : byId.GetValueOrDefault(block);
                return new SampledLine(
                    a.Line.Page,
                    readable,
                    Sha256(stem + "|" + key),
                    block,
                    block is null ? null : contexts[block].Source.StructuralScope,
                    candidate?.CandidateScore,
                    block is null ? null : rankOf.GetValueOrDefault(block, -1),
                    TableScopePenalty: candidate?.NegativeSignals.Contains("table_scope") ?? false,
                    Selected: block is not null && selected.Contains(block),
                    Role: null);
            })
            .GroupBy(line => line.Page + "|" + line.Readable)
            .Select(group => group.First())
            .ToArray();

        var penalised = exposed.Where(x => x.TableScopePenalty).OrderBy(x => x.IdentityHash, StringComparer.Ordinal)
            .Take(PerStratum).ToArray();
        var clean = exposed.Where(x => !x.TableScopePenalty).OrderBy(x => x.IdentityHash, StringComparer.Ordinal)
            .Take(PerStratum).ToArray();

        return new SampledDocument(
            stem,
            Path.GetFileNameWithoutExtension(path),
            exposed.Length,
            exposed.Count(x => x.TableScopePenalty),
            snapshot.CandidateBlocks.Count,
            penalised.Concat(clean).ToArray());
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..16];

    private static string Key(int page, string readable) =>
        $"{page}|{Regex.Replace(readable, @"\s+", " ").Trim()}";

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocxHeaderExtractor.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? AppContext.BaseDirectory;
    }

    private sealed record SampleReport(
        string Contract,
        string Stratification,
        string OrderWithinStratum,
        int PerStratum,
        int SelectedBudget,
        IReadOnlyList<SampledDocument> Documents);

    private sealed record SampledDocument(
        string Stem,
        string Document,
        int ExposedLines,
        int ExposedLinesWithPenalty,
        int CandidateBlocks,
        IReadOnlyList<SampledLine> Sample);

    private sealed record SampledLine(
        int Page,
        string Readable,
        string IdentityHash,
        string? BlockId,
        string? Scope,
        double? Score,
        int? Rank,
        bool TableScopePenalty,
        bool Selected,
        string? Role);
}
