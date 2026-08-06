using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.OpenXmlLayer;

/// <summary>
/// View trung lập dành riêng cho LLM. OOXML/SlimDocument vẫn là nguồn chuẩn; view này chỉ là
/// phép chiếu đọc được, không dùng dấu #/## hay style Markdown có thể gợi sẵn đáp án heading.
/// Metadata là JSON một dòng để ID và tín hiệu cấu trúc không bị lẫn với nội dung tài liệu.
/// </summary>
public static class NeutralDocumentViewSerializer
{
    public static IReadOnlyList<XmlLine> BuildLines(
        SlimDocument doc,
        ExtractionOptions options,
        IReadOnlySet<int>? reviewIndexes)
    {
        var lines = new List<XmlLine>();
        var normalRun = 0;

        void FlushNormal()
        {
            if (normalRun == 0) return;
            if (options.CollapseNormalRuns)
            {
                var omitted = JsonSerializer.Serialize(new { count = normalRun }, JsonOptions);
                lines.Add(new XmlLine($"OMITTED_NORMAL_BLOCKS {omitted}", null, false));
            }
            normalRun = 0;
        }

        var paragraphs = doc.Paragraphs;
        for (var i = 0; i < paragraphs.Count; i++)
        {
            var paragraph = paragraphs[i];
            if (paragraph.Role == ParagraphRole.Empty) continue;

            var review = reviewIndexes?.Contains(paragraph.Index) ?? paragraph.IsCandidate;
            var preserveEveryParagraph = reviewIndexes is not null;
            if (!paragraph.IsCandidate && !preserveEveryParagraph)
            {
                normalRun++;
                continue;
            }

            FlushNormal();
            lines.Add(new XmlLine(Block(paragraph, options.MaxTextLength, review), paragraph.Index, review));

            if (options.IncludeFollowingContext && !preserveEveryParagraph)
            {
                var next = paragraphs.Skip(i + 1).FirstOrDefault(x => x.Role != ParagraphRole.Empty);
                if (next is not null && !next.IsCandidate && next.Text.Length > 0)
                {
                    var metadata = JsonSerializer.Serialize(new
                    {
                        sourceIndex = next.Index,
                        stableId = EmptyToNull(next.StableId),
                    }, JsonOptions);
                    lines.Add(new XmlLine(
                        $"CONTEXT\nmetadata: {metadata}\ncontent:\n    {Indent(SlimXmlSerializer.Truncate(next.Text, options.ContextTextLength))}\nEND_CONTEXT",
                        null,
                        false));
                }
            }
        }

        FlushNormal();
        return lines;
    }

    public static string WrapChunk(IEnumerable<XmlLine> lines, int chunkNo, int chunkTotal)
    {
        var sb = new StringBuilder();
        sb.Append("DOCUMENT_VIEW ")
            .Append(JsonSerializer.Serialize(new { part = chunkNo, totalParts = chunkTotal }, JsonOptions))
            .AppendLine();
        foreach (var line in lines) sb.AppendLine(line.Text);
        sb.Append("END_DOCUMENT_VIEW");
        return sb.ToString();
    }

    /// <summary>
    /// Style Heading built-in ĐÃ nói cấp; gửi kèm <c>w:outlineLvl</c> thô chỉ tạo mâu thuẫn.
    /// <para>
    /// ĐO ĐƯỢC: một báo cáo thật (chuyển từ PDF) khai <c>Heading1 → w:outlineLvl=1</c> trong
    /// styles.xml, lệch quy ước 0-based — cả 73/73 đoạn mang style Heading đều lệch. Metadata khi
    /// đó chở <c>outlineLevel:1</c> cạnh <c>guessedLevel:1</c>, còn system prompt thì dạy
    /// "outlineLevel: 0 = cấp 1". Mô hình mạnh chọn guessedLevel, mô hình yếu chọn outlineLevel và
    /// đẩy MỌI mục cấp 1 xuống cấp 2 — 6 trong 10 lỗi cấp của Haiku đúng là ca này.
    /// </para>
    /// <para>
    /// Với đoạn KHÔNG mang style Heading built-in thì <c>outlineLvl</c> vẫn là bằng chứng thật và
    /// vẫn được gửi: ở đó nó là nguồn duy nhất nói về cấp.
    /// </para>
    /// </summary>
    private static int? OutlineLevelForModel(SlimParagraph p) =>
        p.HasBuiltInHeadingStyle ? null : p.OutlineLevel;

    private static string Block(SlimParagraph p, int maxText, bool requested)
    {
        var boldRanges = p.TextSpans.Where(x => x.Bold)
            .Select(x => new OffsetRange(x.Start, x.End)).ToArray();
        OffsetRange? headingSpan = p.VerifiedHeadingEnd is { } headingEnd
            ? new OffsetRange(0, headingEnd)
            : null;
        OffsetRange? bodySpan = p.VerifiedBodyStart is { } bodyStart
            ? new OffsetRange(bodyStart, p.Text.Length)
            : null;

        var metadata = new BlockMetadata(
            p.Index,
            requested,
            EmptyToNull(p.StableId),
            p.TableDepth > 0 ? "table_cell" : "paragraph",
            p.TableDepth > 0 ? p.TableDepth : null,
            EmptyToNull(p.StyleId),
            EmptyToNull(p.StyleName),
            OutlineLevelForModel(p),
            p.GuessedLevel,
            p.Bold ? true : null,
            p.AllCaps ? true : null,
            p.Italic ? true : null,
            p.Underline ? true : null,
            p.FontSizePt,
            EmptyToNull(p.Alignment),
            p.NumberingId,
            p.NumberingLevel,
            EmptyToNull(p.NumberLabel),
            p.KeepNext ? true : null,
            p.PageBreakBefore ? true : null,
            p.SectionIndex > 0 ? p.SectionIndex : null,
            p.InTableOfContents ? true : null,
            boldRanges.Length == 0 ? null : boldRanges,
            headingSpan,
            bodySpan,
            EmptyToNull(p.VerifiedBoundarySource));

        return $"BLOCK\nmetadata: {JsonSerializer.Serialize(metadata, JsonOptions)}\ncontent:\n    {Indent(SlimXmlSerializer.Truncate(p.Text, maxText))}\nEND_BLOCK";
    }

    private static string Indent(string text) => text.ReplaceLineEndings("\n    ");
    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed record OffsetRange(int Start, int End);

    private sealed record BlockMetadata(
        int I,
        bool Requested,
        string? StableId,
        string Source,
        int? TableDepth,
        string? StyleId,
        string? StyleName,
        int? OutlineLevel,
        int? GuessedLevel,
        bool? Bold,
        bool? AllCaps,
        bool? Italic,
        bool? Underline,
        double? FontSizePt,
        string? Alignment,
        int? NumberingId,
        int? NumberingLevel,
        string? NumberLabel,
        bool? KeepNext,
        bool? PageBreakBefore,
        int? SectionIndex,
        bool? InTableOfContents,
        IReadOnlyList<OffsetRange>? BoldSpans,
        OffsetRange? VerifiedHeadingSpan,
        OffsetRange? VerifiedBodySpan,
        string? BoundarySource);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
