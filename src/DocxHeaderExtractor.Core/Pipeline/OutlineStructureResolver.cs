using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Khôi phục cây đề mục hành chính có dạng La Mã → số Ả Rập → chữ cái. Chỉ kích hoạt khi
/// tài liệu có ít nhất hai mục La Mã và trong một phần có một dãy số bắt đầu từ 1; nhờ vậy
/// một con số hoặc chữ cái đứng riêng trong văn bản không bị biến thành heading.
/// </summary>
public static partial class OutlineStructureResolver
{
    public sealed record Result(int Recovered, int Removed, int LevelsFixed);

    public static Result Apply(
        IReadOnlyList<SlimParagraph> reviewed,
        IDictionary<int, HeadingRecord> accepted)
    {
        var byIndex = reviewed.ToDictionary(p => p.Index);

        var numbered = reviewed
            .Select(p => (Paragraph: p, Token: NumberingAudit.Parse(p.Text)))
            .Where(x => x.Token is not null)
            .Select(x => (x.Paragraph, Token: x.Token!.Value))
            .OrderBy(x => x.Paragraph.Index)
            .ToList();

        var romans = numbered.Where(x => x.Token.Kind == NumberKind.Roman).ToList();
        if (romans.Count < 2) return new Result(0, 0, 0);

        var removed = 0;
        // Khi đã xác nhận đây là cây La Mã → số → chữ, dấu gạch là nội dung của mục gần nhất,
        // không phải một tầng mới. Ngoài loại tài liệu này vẫn để model quyết định.
        foreach (var index in accepted.Keys.ToList())
        {
            if (!byIndex.TryGetValue(index, out var p) || p.HasBuiltInHeadingStyle) continue;
            if (!BulletRx().IsMatch(p.Text)) continue;
            accepted.Remove(index);
            removed++;
        }

        var recovered = 0;
        var fixedLevels = 0;

        for (var r = 0; r < romans.Count; r++)
        {
            var roman = romans[r];
            var scopeEnd = r + 1 < romans.Count ? romans[r + 1].Paragraph.Index : int.MaxValue;

            // Chỉ nhận đề mục La Mã có bằng chứng trình bày mạnh; tránh chữ I/V/X trong câu.
            if (roman.Paragraph.Bold || roman.Paragraph.AllCaps || accepted.ContainsKey(roman.Paragraph.Index))
                Upsert(roman.Paragraph, 1);

            var arabics = numbered.Where(x =>
                    x.Paragraph.Index > roman.Paragraph.Index && x.Paragraph.Index < scopeEnd &&
                    x.Token.Kind == NumberKind.Arabic && x.Token.Depth == 1)
                .ToList();

            // Một dãy đề mục phải bắt đầu từ 1 và có ít nhất hai phần tử. Đây là chốt chống
            // biến các dòng số liệu rời rạc thành heading.
            if (arabics.Count < 2 || arabics.All(x => x.Token.Value != 1)) continue;

            for (var a = 0; a < arabics.Count; a++)
            {
                var arabic = arabics[a];
                Upsert(arabic.Paragraph, 2);

                var childEnd = a + 1 < arabics.Count ? arabics[a + 1].Paragraph.Index : scopeEnd;
                var letters = numbered.Where(x =>
                        x.Paragraph.Index > arabic.Paragraph.Index && x.Paragraph.Index < childEnd &&
                        x.Token.Kind == NumberKind.Letter)
                    .ToList();

                // Chữ cái đơn chỉ là cấu trúc khi tạo thành một dãy a,b,…; một dòng đơn lẻ
                // có thể là ký hiệu hoặc câu văn nên vẫn để model quyết định.
                if (letters.Count < 2 || letters.All(x => x.Token.Value != 1)) continue;
                foreach (var letter in letters) Upsert(letter.Paragraph, 3);
            }
        }

        return new Result(recovered, removed, fixedLevels);

        void Upsert(SlimParagraph paragraph, int level)
        {
            if (accepted.TryGetValue(paragraph.Index, out var existing))
            {
                if (existing.Level == level) return;
                existing.Level = level;
                existing.Disputed = true;
                fixedLevels++;
                return;
            }

            accepted[paragraph.Index] = new HeadingRecord
            {
                Index = paragraph.Index,
                StableId = paragraph.StableId,
                Level = level,
                Text = paragraph.Text,
                StyleId = paragraph.StyleId,
                Source = HeadingSource.Structure,
                Confidence = 0.75,
                Disputed = true,
            };
            recovered++;
        }
    }

    [GeneratedRegex(@"^\s*[-–—•*▪+]\s+\S", RegexOptions.CultureInvariant)]
    private static partial Regex BulletRx();
}
