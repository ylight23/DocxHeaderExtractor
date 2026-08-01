using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Eval;

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

    public static ReviewBundle Create(DocumentOutline outline, SlimDocument document)
    {
        var headings = outline.Headings.ToDictionary(h => h.Index);
        return new ReviewBundle
        {
            SourceFile = outline.File,
            Rows = document.Paragraphs
                .Where(p => p.Role != ParagraphRole.Empty)
                .Select(p => headings.TryGetValue(p.Index, out var heading)
                    ? new ReviewRow
                    {
                        StableId = p.StableId,
                        Index = p.Index,
                        Text = p.Text,
                        PredictedLevel = heading.Level,
                        Source = heading.Source.ToString(),
                        Confidence = heading.Confidence,
                        DecisionStatus = heading.DecisionStatus.ToString(),
                        ConfidenceBasis = heading.ConfidenceBasis,
                        HeadingText = heading.Text,
                        InlineBody = heading.InlineBody,
                        HeadingSpan = heading.HeadingSpan,
                        InlineBodySpan = heading.InlineBodySpan,
                    }
                    : new ReviewRow
                    {
                        StableId = p.StableId,
                        Index = p.Index,
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

    public static ReviewBundle Load(string path) => Parse(File.ReadAllText(path));

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Chỉ sinh key khi người duyệt đã xác nhận từng paragraph.</summary>
    public string ToAnswerKeyText()
    {
        EnsureComplete();
        return AnswerKey.WriteStable(
            Rows.Where(r => r.CorrectedLevel > 0)
                .Select(r => (r.StableId, r.CorrectedLevel!.Value, r.Text)),
            Path.GetFileNameWithoutExtension(SourceFile));
    }

    /// <summary>
    /// JSONL nhãn vàng cho fine-tuning/evaluation. Giữ cả nhãn 0 để mô hình học phân biệt non-heading.
    /// </summary>
    public string ToTrainingJsonl()
    {
        EnsureComplete();
        var sb = new StringBuilder();
        foreach (var row in Rows.OrderBy(r => r.Index))
        {
            var item = new
            {
                document = SourceFile,
                stableId = row.StableId,
                index = row.Index,
                text = row.Text,
                predictedLevel = row.PredictedLevel,
                label = row.CorrectedLevel!.Value,
            };
            // JSONL phải đúng một object trên một dòng; không dùng serializer pretty-print của file review.
            sb.AppendLine(JsonSerializer.Serialize(item, JsonLineOptions));
        }
        return sb.ToString();
    }

    public void EnsureComplete()
    {
        var unreviewed = Rows.Where(r => r.CorrectedLevel is null).Take(8).ToList();
        if (unreviewed.Count == 0) return;

        var preview = string.Join(", ", unreviewed.Select(r => $"{r.Index} ({r.StableId})"));
        throw new InvalidOperationException(
            $"Chưa xác nhận tất cả paragraph. Còn ít nhất: {preview}. " +
            "Chọn 0 cho non-heading hoặc 1..9 cho heading trước khi tạo key.");
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

    private static readonly JsonSerializerOptions JsonLineOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

public sealed class ReviewRow
{
    public required string StableId { get; init; }
    public required int Index { get; init; }
    public required string Text { get; init; }
    public int PredictedLevel { get; init; }
    public int? CorrectedLevel { get; set; }
    public string? Source { get; init; }
    public double? Confidence { get; init; }
    public string? DecisionStatus { get; init; }
    public string? ConfidenceBasis { get; init; }
    public string? HeadingText { get; init; }
    public string? InlineBody { get; init; }
    public TextOffsetSpan? HeadingSpan { get; init; }
    public TextOffsetSpan? InlineBodySpan { get; init; }
}
