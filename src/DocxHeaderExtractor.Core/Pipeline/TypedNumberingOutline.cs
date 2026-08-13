using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Builder tất định cho tài liệu dùng số gõ tay kiểu <c>1.</c>, <c>1.1.</c>,
/// <c>1.1.1.</c>. Khác văn bản hành chính, cấp của nhóm này nằm ngay trong marker:
/// đếm độ sâu số, không suy theo thứ tự chữ ký xuất hiện.
/// </summary>
public static class TypedNumberingOutline
{
    public static List<HeadingRecord> Build(SlimDocument document, bool splitMergedParagraphs = true)
    {
        List<HeadingRecord> result = [];

        foreach (var p in document.Paragraphs.OrderBy(x => x.Index))
        {
            if (p.Corrupt || p.TableDepth > 0 || string.IsNullOrWhiteSpace(p.Text)) continue;
            var segments = splitMergedParagraphs
                ? ParagraphHeadingSplitter.Segments(p.Text)
                : [p.Text];

            foreach (var seg in segments)
            {
                if (NumberingAudit.Parse(seg) is not { } token) continue;

                var (heading, body) = AdministrativeOutline.SplitHeadingBody(seg);
                result.Add(new HeadingRecord
                {
                    Index = p.Index,
                    StableId = p.StableId,
                    Level = Math.Clamp(token.Depth, 1, 9),
                    Text = heading,
                    StyleId = p.StyleId,
                    Source = HeadingSource.Structure,
                    Confidence = 1.0,
                    ConfidenceBasis = "typed_number_depth",
                    DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence,
                    InlineBody = body,
                    OriginalText = body is null ? null : seg,
                });
            }
        }

        return result;
    }
}
