using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Tách heading và nội dung cùng paragraph khi ranh giới được OOXML chứng minh rõ.
/// Không cắt chỉ vì gặp dấu hai chấm: ranh giới phải trùng chuyển tiếp bold → non-bold, hoặc phần
/// sau dấu phân cách phải là payload thuần số/ký hiệu có thể kiểm chứng mà không cần hiểu từ khoá.
/// </summary>
public static class InlineHeadingSplitter
{
    public static int Apply(ICollection<HeadingRecord> headings, SlimDocument document)
    {
        var split = 0;
        foreach (var heading in headings)
        {
            var paragraph = document.ByIndex(heading.Index);
            if (paragraph is null || !TryFindBoundary(paragraph, out var headingEnd, out var bodyStart, out var source))
                continue;

            heading.OriginalText = paragraph.Text;
            heading.Text = paragraph.Text[..headingEnd];
            heading.HeadingSpan = new TextOffsetSpan(0, headingEnd);
            heading.InlineBody = paragraph.Text[bodyStart..];
            heading.InlineBodySpan = new TextOffsetSpan(bodyStart, paragraph.Text.Length);
            heading.BoundarySource = source;
            paragraph.VerifiedHeadingEnd = headingEnd;
            paragraph.VerifiedBodyStart = bodyStart;
            paragraph.VerifiedBoundarySource = source;
            split++;
        }
        return split;
    }

    public static bool TryFindBoundary(SlimParagraph paragraph, out int headingEnd, out int bodyStart)
        => TryFindBoundary(paragraph, out headingEnd, out bodyStart, out _);

    private static bool TryFindBoundary(
        SlimParagraph paragraph,
        out int headingEnd,
        out int bodyStart,
        out string source)
    {
        headingEnd = bodyStart = 0;
        source = "";
        if (NumberingAudit.Parse(paragraph.Text) is null) return false;

        if (TryRunBoundary(paragraph, out headingEnd, out bodyStart))
        {
            source = "OpenXmlRunFormatting";
            return true;
        }

        if (TryNumericPayloadBoundary(paragraph.Text, out headingEnd, out bodyStart))
        {
            source = "NumericPayloadAfterSeparator";
            return true;
        }
        return false;
    }

    private static bool TryRunBoundary(SlimParagraph paragraph, out int headingEnd, out int bodyStart)
    {
        headingEnd = bodyStart = 0;
        if (paragraph.TextSpans.Count < 2) return false;

        var first = paragraph.TextSpans[0];
        if (first.Start != 0 || !first.Bold) return false;

        var boundary = paragraph.TextSpans.FirstOrDefault(s => !s.Bold && s.Start > 0);
        if (boundary is null) return false;

        var cursor = boundary.Start;
        while (cursor < paragraph.Text.Length && char.IsWhiteSpace(paragraph.Text[cursor])) cursor++;
        if (cursor >= paragraph.Text.Length || paragraph.Text[cursor] is not (':' or ';')) return false;

        headingEnd = boundary.Start;
        while (headingEnd > 0 && char.IsWhiteSpace(paragraph.Text[headingEnd - 1])) headingEnd--;
        bodyStart = cursor + 1;
        while (bodyStart < paragraph.Text.Length && char.IsWhiteSpace(paragraph.Text[bodyStart])) bodyStart++;

        if (headingEnd <= 0 || bodyStart >= paragraph.Text.Length) return false;
        var headingText = paragraph.Text[..headingEnd];
        var bodyText = paragraph.Text[bodyStart..];
        return headingText.Count(char.IsLetter) >= 2 && bodyText.Any(char.IsLetter);
    }

    private static bool TryNumericPayloadBoundary(string text, out int headingEnd, out int bodyStart)
    {
        headingEnd = bodyStart = 0;
        // Duyệt từ phải sang trái: dấu ':' bên trong tên mục vẫn được giữ nếu suffix còn từ ngữ.
        for (var separator = text.Length - 1; separator > 0; separator--)
        {
            if (text[separator] is not (':' or ';')) continue;
            var start = separator + 1;
            while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
            if (start >= text.Length) continue;
            var payload = text[start..].Trim();
            if (!payload.Any(char.IsDigit) || payload.Any(char.IsLetter)) continue;

            var end = separator;
            while (end > 0 && char.IsWhiteSpace(text[end - 1])) end--;
            if (end <= 0 || text[..end].Count(char.IsLetter) < 2) continue;
            headingEnd = end;
            bodyStart = start;
            return true;
        }
        return false;
    }
}
