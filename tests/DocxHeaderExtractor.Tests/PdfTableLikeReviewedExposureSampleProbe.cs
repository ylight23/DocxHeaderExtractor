using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M10.5-B1 freezes a review sample selected solely from pre-existing source facts and current
/// downstream state. It intentionally assigns no role: human review happens in B2, after this
/// artifact is immutable. No key, model, counterfactual, or production rule participates.
/// </summary>
public sealed class PdfTableLikeReviewedExposureSampleProbe
{
    private const int CandidateBudget = 160;
    private const int PerStratumBudget = 10;
    private static readonly string[] ExcludedScopes =
        ["embedded_amendment", "quoted_replacement", "appendix_table", "table", "table_of_contents",
         "running_page_artifact", "code_or_grammar", "reference_list", "index_terms"];

    private static readonly (string Id, string RelativePath)[] Documents =
    [
        ("032", "02_hop_dong_mua_sam/032_WB_Plant_TwoStage_2020.docx"),
        ("043", "03_tai_chinh_ke_toan/043_IBRD_Financial_Statements_June_2024.docx"),
        ("063", "04_giao_trinh/063_Advanced_Linear_Algebra.docx"),
        ("091", "07_system_generated/091_RFC9110_HTTP_Semantics.docx"),
    ];

    [Fact]
    public void Report()
    {
        var output = Environment.GetEnvironmentVariable("M10_5_B1_REPORT");
        if (string.IsNullOrWhiteSpace(output)) return;

        var corpus = Path.Combine(RepositoryRoot(), "todo10_8", "heading_corpus_95_word");
        var documents = Documents.Select(document => Measure(corpus, document)).ToArray();
        var report = new SampleReport(
            Contract: "gold_free_model_free_no_counterfactual",
            CandidateBudget,
            PerStratumBudget,
            Selection: "SHA-256 order of exact source-line identity within each document and stratum; role is intentionally unreviewed.",
            Documents: documents);
        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        File.WriteAllText(output, JsonSerializer.Serialize(report, options));
    }

    private static SampleDocument Measure(string corpus, (string Id, string RelativePath) document)
    {
        var path = Path.Combine(corpus, document.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(path);
        var contexts = PdfCandidateContextBuilder.Build(snapshot.CandidateBlocks, snapshot.Annotations);
        var ranked = PdfCandidateRanker.Rank(snapshot.CandidateBlocks, contexts);
        var rankedById = ranked.ToDictionary(candidate => candidate.SourceId, StringComparer.Ordinal);
        var rankById = ranked.Select((candidate, index) => (candidate.SourceId, Rank: index + 1))
            .ToDictionary(item => item.SourceId, item => item.Rank, StringComparer.Ordinal);
        var blocksByLine = snapshot.CandidateBlocks
            .SelectMany(block => block.Lines.Select(line => (Line: LineIdentity(line), Block: block)))
            .GroupBy(item => item.Line, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Block).ToArray(), StringComparer.Ordinal);

        var population = snapshot.Annotations
            .Where(annotation => PdfLineBlockFilter.ClassifyTableLine(annotation.Line.Text) == "short_numbered" &&
                                 PdfLineBlockAnnotation.HasStructuralMarker(annotation.Line.Text))
            .Select(annotation => BuildOccurrence(document, annotation.Line, blocksByLine, contexts, rankedById, rankById))
            .Where(occurrence => occurrence is not null)
            .Cast<SampleOccurrence>()
            .ToArray();

        var penalized = population.Where(item => item.TableScopePenalty).ToArray();
        var nonPenalized = population.Where(item => !item.TableScopePenalty).ToArray();
        var selected = Select(penalized, "table_scope_penalized")
            .Concat(Select(nonPenalized, "not_table_scope_penalized"))
            .ToArray();

        return new SampleDocument(
            document.Id,
            document.RelativePath,
            Sha256(File.ReadAllBytes(path)),
            CandidateCount: snapshot.CandidateBlocks.Count,
            PenalizedPopulation: penalized.Length,
            NonPenalizedPopulation: nonPenalized.Length,
            Samples: selected);
    }

    private static SampleOccurrence? BuildOccurrence(
        (string Id, string RelativePath) document,
        PdfLine line,
        IReadOnlyDictionary<string, PdfSemanticBlock[]> blocksByLine,
        IReadOnlyDictionary<string, PdfCandidateContext> contexts,
        IReadOnlyDictionary<string, RankedCandidate> ranked,
        IReadOnlyDictionary<string, int> rankById)
    {
        var sourceLineId = $"{document.RelativePath}|{LineIdentity(line)}";
        if (!blocksByLine.TryGetValue(LineIdentity(line), out var blocks)) return null;

        // One exact source occurrence can be carried by several blocks. The current downstream
        // chain is represented by the best-ranked carrier, while all carrier IDs stay visible.
        var primary = blocks.OrderBy(block => rankById.GetValueOrDefault(block.Id, int.MaxValue))
            .ThenBy(block => block.Id, StringComparer.Ordinal).First();
        var context = contexts[primary.Id];
        var candidate = ranked[primary.Id];
        var rank = rankById[primary.Id];
        var selected = rank <= CandidateBudget;
        var emittable = selected && !ExcludedScopes.Contains(context.Source.StructuralScope, StringComparer.Ordinal);
        var tablePenalty = candidate.NegativeSignals.Contains("table_scope");

        return new SampleOccurrence(
            Stratum: string.Empty,
            SourceLineId: sourceLineId,
            SourceLineIdSha256: Sha256(Encoding.UTF8.GetBytes(sourceLineId)),
            Page: line.Page,
            TopY: line.Y,
            Left: line.Left,
            Right: line.Right,
            ObservedText: line.Text,
            ReadableText: PdfTextUtilities.Readable(line.Text),
            TableLike: true,
            TableLikeRule: "short_numbered",
            HasStructuralMarker: true,
            CarrierBlockIds: blocks.Select(block => block.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            PrimaryBlockId: primary.Id,
            StructuralScope: context.Source.StructuralScope,
            TableScopePenalty: tablePenalty,
            CandidateScore: candidate.CandidateScore,
            Rank: rank,
            SelectedAtBudget: selected,
            EmittableAtBudget: emittable,
            ReviewRole: null);
    }

    private static IEnumerable<SampleOccurrence> Select(IEnumerable<SampleOccurrence> population, string stratum) => population
        .OrderBy(item => item.SourceLineIdSha256, StringComparer.Ordinal)
        .Take(PerStratumBudget)
        .Select(item => item with { Stratum = stratum });

    private static string LineIdentity(PdfLine line) => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"{line.Page}|{line.Y:R}|{line.Left:R}|{line.Right:R}|{line.Text}");

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocxHeaderExtractor.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? AppContext.BaseDirectory;
    }

    private sealed record SampleReport(
        string Contract,
        int CandidateBudget,
        int PerStratumBudget,
        string Selection,
        IReadOnlyList<SampleDocument> Documents);

    private sealed record SampleDocument(
        string DocumentId,
        string RelativePath,
        string SourceDocumentSha256,
        int CandidateCount,
        int PenalizedPopulation,
        int NonPenalizedPopulation,
        IReadOnlyList<SampleOccurrence> Samples);

    private sealed record SampleOccurrence(
        string Stratum,
        string SourceLineId,
        string SourceLineIdSha256,
        int Page,
        double TopY,
        double Left,
        double Right,
        string ObservedText,
        string ReadableText,
        bool TableLike,
        string TableLikeRule,
        bool HasStructuralMarker,
        IReadOnlyList<string> CarrierBlockIds,
        string PrimaryBlockId,
        string StructuralScope,
        bool TableScopePenalty,
        double CandidateScore,
        int Rank,
        bool SelectedAtBudget,
        bool EmittableAtBudget,
        string? ReviewRole);
}
