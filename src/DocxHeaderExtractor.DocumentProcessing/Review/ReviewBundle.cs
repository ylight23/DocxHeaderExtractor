using DocxHeaderExtractor.DocumentProcessing.Review;
using System.Text.Encodings.Web;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Review;

/// <summary>
/// Gói gán nhãn có thể đem đi review. Mỗi paragraph không rỗng có đúng một dòng;
/// <see cref="ReviewRow.CorrectedLevel"/> để null cho đến khi người duyệt xác nhận, 0 là non-heading.
/// </summary>
public sealed class ReviewBundle
{
    public const string Format = "dhx-review/v1";

    public string FormatVersion { get; init; } = Format;
    public required string SourceFile { get; init; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public required IReadOnlyList<ReviewRow> Rows { get; init; }

    public static ReviewBundle Create(DocumentOutline outline, SourceDocument document)
    {
        var headings = outline.Headings.ToDictionary(h => h.Index);
        return new ReviewBundle
        {
            SourceFile = outline.File,
            Rows = document.Paragraphs
                .Where(p => !string.IsNullOrWhiteSpace(p.Text))
                .Select(p => headings.TryGetValue(p.SourceOrdinal, out var heading)
                    ? new ReviewRow
                    {
                        StableId = p.SourceId,
                        Index = p.SourceOrdinal,
                        Text = p.Text,
                        PredictedLevel = heading.Level,
                        Source = heading.Source.ToString(),
                        DecisionStatus = heading.DecisionStatus.ToString(),
                        ConfidenceBasis = heading.ConfidenceBasis,
                        HeadingText = heading.Text,
                        InlineBody = heading.InlineBody,
                        HeadingSpan = heading.HeadingSpan,
                        InlineBodySpan = heading.InlineBodySpan,
                    }
                    : new ReviewRow
                    {
                        StableId = p.SourceId,
                        Index = p.SourceOrdinal,
                        Text = p.Text,
                        PredictedLevel = 0,
                    })
                .ToList(),
        };
    }

    public static ReviewBundle Parse(string json)
    {
        var bundle = JsonSerializer.Deserialize<ReviewBundle>(json, JsonOptions)
            ?? throw new FormatException("File review rỗng hoặc không phải JSON hợp lệ.");
        Validate(bundle);
        return bundle;
    }

    private static void Validate(ReviewBundle bundle)
    {
        if (!string.Equals(bundle.FormatVersion, Format, StringComparison.Ordinal))
            throw new FormatException($"Không hỗ trợ review format '{bundle.FormatVersion}'. Cần '{Format}'.");
        if (string.IsNullOrWhiteSpace(bundle.SourceFile))
            throw new FormatException("Review bundle thiếu sourceFile.");
        if (bundle.Rows is null || bundle.Rows.Count == 0)
            throw new FormatException("Review bundle không có paragraph nào.");

        var stableIds = new HashSet<string>(StringComparer.Ordinal);
        var indexes = new HashSet<int>();
        foreach (var row in bundle.Rows)
        {
            if (string.IsNullOrWhiteSpace(row.StableId) || !stableIds.Add(row.StableId))
                throw new FormatException($"stableId rỗng hoặc trùng: '{row.StableId}'.");
            if (!indexes.Add(row.Index)) throw new FormatException($"Index paragraph trùng: {row.Index}.");
            if (row.PredictedLevel is < 0 or > 9)
                throw new FormatException($"predictedLevel phải thuộc 0..9 tại {row.StableId}.");
            if (row.CorrectedLevel is < 0 or > 9)
                throw new FormatException($"correctedLevel phải thuộc 0..9 tại {row.StableId}.");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

public sealed class ReviewRow
{
    public required string StableId { get; init; }
    public required int Index { get; init; }
    public required string Text { get; init; }
    /// <summary>0 = not a heading; null = a heading whose level the route could not resolve; 1..9 = predicted level.</summary>
    public int? PredictedLevel { get; init; }
    public int? CorrectedLevel { get; set; }
    public string? Source { get; init; }
    public string? DecisionStatus { get; init; }
    public string? ConfidenceBasis { get; init; }
    public string? HeadingText { get; init; }
    public string? InlineBody { get; init; }
    public TextOffsetSpan? HeadingSpan { get; init; }
    public TextOffsetSpan? InlineBodySpan { get; init; }
}
