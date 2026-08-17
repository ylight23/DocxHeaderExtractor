using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Dòng PDF dựng lại từ letter theo toạ độ Y (không phải dòng logic OOXML). <see cref="BoldRatio"/>
/// và <see cref="LeadingBoldPrefix"/> chỉ có ý nghĩa khi bộ gọi cần tín hiệu bold — hai bộ dựng
/// heading khác nhau (font-size cho textbook, bold-run-in cho biên bản/minutes) đọc chung một lượt
/// quét letter vì cùng một thuật toán bucket-theo-Y/khoảng-cách, chỉ khác tín hiệu dùng để quyết heading.
/// </summary>
internal sealed record PdfLine(
    int Page, double Y, double FontSize, string Text, double BoldRatio, string LeadingBoldPrefix,
    double ItalicRatio);

internal static class PdfLineExtraction
{
    public static IReadOnlyList<PdfLine> ExtractLines(PdfDocument doc)
    {
        var lines = new List<PdfLine>();
        foreach (var page in doc.GetPages())
        {
            var letters = page.Letters
                .Where(l => !string.IsNullOrWhiteSpace(l.Value))
                .OrderByDescending(MidY)
                .ThenBy(l => l.BoundingBox.Left)
                .ToList();

            var buckets = new List<List<Letter>>();
            List<Letter>? current = null;
            double currentY = 0;
            foreach (var letter in letters)
            {
                var y = MidY(letter);
                var tolerance = Math.Max(1.5, Math.Max(letter.FontSize, letter.BoundingBox.Height) * 0.30);
                if (current is null || Math.Abs(currentY - y) > tolerance)
                {
                    current = [];
                    buckets.Add(current);
                    currentY = y;
                }
                else
                {
                    currentY = ((currentY * current.Count) + y) / (current.Count + 1);
                }
                current.Add(letter);
            }

            foreach (var bucket in buckets)
            {
                var ordered = bucket.OrderBy(l => l.BoundingBox.Left).ToList();
                var pieces = new List<string>();
                var boldFlags = new List<bool>();
                var italicFlags = new List<bool>();
                Letter? previous = null;
                foreach (var letter in ordered)
                {
                    if (previous is not null)
                    {
                        var gap = letter.BoundingBox.Left - previous.BoundingBox.Right;
                        if (gap > Math.Max(1.2, Math.Max(previous.FontSize, previous.BoundingBox.Height) * 0.18))
                        {
                            pieces.Add(" ");
                            boldFlags.Add(boldFlags.Count > 0 && boldFlags[^1]);
                            italicFlags.Add(italicFlags.Count > 0 && italicFlags[^1]);
                        }
                    }
                    pieces.Add(letter.Value);
                    foreach (var _ in letter.Value)
                    {
                        boldFlags.Add(letter.FontDetails?.IsBold ?? false);
                        italicFlags.Add(letter.FontDetails?.IsItalic ?? false);
                    }
                    previous = letter;
                }

                var raw = string.Concat(pieces);
                var text = NormalizeSpace(raw);
                if (text.Length == 0) continue;

                var boldRatio = boldFlags.Count == 0 ? 0.0 : boldFlags.Count(b => b) / (double)boldFlags.Count;
                var italicRatio = italicFlags.Count == 0 ? 0.0 : italicFlags.Count(b => b) / (double)italicFlags.Count;
                var leadingBoldLen = 0;
                while (leadingBoldLen < boldFlags.Count && boldFlags[leadingBoldLen]) leadingBoldLen++;
                var leadingBoldPrefix = leadingBoldLen > 0 && leadingBoldLen < raw.Length
                    ? NormalizeSpace(raw[..leadingBoldLen])
                    : "";

                lines.Add(new PdfLine(
                    page.Number,
                    ordered.Average(MidY),
                    ordered.Average(l => l.FontSize),
                    text,
                    boldRatio,
                    leadingBoldPrefix,
                    italicRatio));
            }
        }
        return lines;
    }

    private static double MidY(Letter l) => (l.BoundingBox.Bottom + l.BoundingBox.Top) / 2.0;

    private static string NormalizeSpace(string text)
    {
        var trimmed = text.Trim();
        var result = new System.Text.StringBuilder(trimmed.Length);
        var lastWasSpace = false;
        foreach (var c in trimmed)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace) result.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                result.Append(c);
                lastWasSpace = false;
            }
        }
        return result.ToString();
    }
}
