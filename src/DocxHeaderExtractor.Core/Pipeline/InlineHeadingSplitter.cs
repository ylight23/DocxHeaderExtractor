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

    /// <summary>
    /// Nội dung bắt đầu bằng một TOKEN DỮ LIỆU: số, có thể kèm <c>/</c> <c>.</c> <c>,</c> <c>%</c>
    /// (<c>0/0</c>, <c>01</c>, <c>4.722</c>, <c>13/01</c>).
    /// <para>
    /// <b>Vì sao đổi từ "payload thuần số".</b> Điều kiện cũ đòi phần sau dấu ngắt KHÔNG có một chữ
    /// cái nào, nên nó chỉ bắt được <c>b. KQ Mỹ: 0/0 (0/0).</c> và bỏ qua
    /// <c>a) Trong dự báo: 01 tốp (như ngày 13/01).</c> — cùng một tài liệu, cùng một dãy, cùng một
    /// hình dạng "nhãn : số liệu", chỉ khác ở chỗ số liệu có kèm đơn vị.
    /// </para>
    /// <para>
    /// <b>Vì sao KHÔNG dùng danh sách đơn vị.</b> Cách hiển nhiên là liệt kê
    /// <c>tốp|tàu|chiếc|lượt|…</c>, nhưng đó là danh sách từ khoá tiếng Việt: nó đúng trên đúng
    /// tài liệu đã xem và im lặng trên mọi tài liệu dùng đơn vị khác, kể cả tiếng Việt. Luật ở đây
    /// chỉ hỏi <i>"chỗ này có bắt đầu bằng một con số không"</i> — đọc được ở mọi ngôn ngữ.
    /// </para>
    /// <para>
    /// Ràng buộc giữ nó hẹp: token đầu phải là SỐ. <c>3.1. Kết quả 2024</c> không bị chẻ vì sau dấu
    /// ngắt là chữ, còn <c>Ghi chú: xem phụ lục</c> cũng vậy.
    /// </para>
    /// </summary>
    private static bool StartsWithDataToken(string payload)
    {
        if (payload.Length == 0 || !char.IsDigit(payload[0])) return false;

        var i = 0;
        while (i < payload.Length && (char.IsDigit(payload[i]) || payload[i] is '/' or '.' or ',' or '%' or '-')) i++;
        if (i == 0) return false;

        // Phải kết thúc token ở ranh giới thật, để "13/01" tách được mà "2024abc" thì không.
        return i >= payload.Length || payload[i] is ' ' or '	' or ')' or '(' or ';' or ':' or '.';
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
            if (!StartsWithDataToken(payload)) continue;

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
