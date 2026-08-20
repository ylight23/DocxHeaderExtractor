using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Tính năm nguồn evidence cho Model và Structure. Chỉ Structure bị chấm lại confidence tại đây;
/// Model dùng evidence này ở cổng precision cùng với critic ngữ nghĩa độc lập.
/// </summary>
public static class EvidenceConfidenceCalibrator
{
    public static int Apply(
        IList<HeadingRecord> headings,
        SlimDocument document,
        IReadOnlySet<int>? auditConflicts = null)
    {
        var ordered = headings.OrderBy(x => x.Index).ToList();
        var structure = ordered.Where(x => x.Source == HeadingSource.Structure).ToList();
        var eligible = ordered.Where(x => x.Source is HeadingSource.Structure or HeadingSource.Model).ToList();
        if (eligible.Count == 0) return 0;

        // ParseParagraph chứ không Parse(text): heading do Word tự đánh số qua w:numPr không có con
        // số trong text, và đọc trần thì numberingValid/siblingsValid/formattingConsistent cùng trượt
        // — mất 3/5 kiểm tra cho đúng nhóm tài liệu đánh số bài bản nhất, đẩy chúng xuống cần duyệt.
        var siblingGroups = ordered
            .Select((h, i) => (
                Heading: h,
                Token: NumberingAudit.ParseParagraph(document.ByIndex(h.Index), h.Text),
                Parent: ParentIndex(ordered, i)))
            .Where(x => x.Token is not null)
            .GroupBy(x => (x.Parent, x.Heading.Level, x.Token!.Value.Signature))
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Heading.Index).ToList());

        foreach (var heading in eligible)
        {
            var at = ordered.IndexOf(heading);
            var token = NumberingAudit.ParseParagraph(document.ByIndex(heading.Index), heading.Text);
            var parent = ParentIndex(ordered, at);
            var numberingValid = token is not null;
            var siblingsValid = false;
            var formattingConsistent = false;

            if (token is { } t && siblingGroups.TryGetValue((parent, heading.Level, t.Signature), out var siblings))
            {
                var values = siblings.Select(x => x.Token!.Value.Value).Distinct().Order().ToList();
                siblingsValid = values.Count >= 2 && values.Zip(values.Skip(1)).All(x => x.Second == x.First + 1);
                formattingConsistent = siblings.Count >= 2 && SameFormatting(
                    siblings.Select(x => document.ByIndex(x.Heading.Index)).Where(x => x is not null).Cast<SlimParagraph>());
            }

            var treeValid = (heading.Level == 1 || parent is not null) &&
                !(auditConflicts?.Contains(heading.Index) ?? false);
            var passed = new[] { numberingValid, siblingsValid, formattingConsistent, heading.ModelConfirmed, treeValid }.Count(x => x);
            var verified = passed == 5;
            heading.Evidence = new HeadingEvidence(numberingValid, siblingsValid, formattingConsistent,
                heading.ModelConfirmed, treeValid, verified ? "verified_by_multiple_checks" :
                heading.Source == HeadingSource.Structure ? "requires_review" : "supporting_checks");
            if (heading.Source == HeadingSource.Structure)
            {
                heading.Confidence = ConfidenceForChecks(passed);
                heading.Disputed = !verified &&
                    !PrecisionAcceptanceGate.IsDeterministicDeclaredBasis(heading.ConfidenceBasis ?? "");
            }
        }
        return structure.Count;
    }

    /// <summary>
    /// Tier evidence dễ hiểu trên UI: 3/5 = 80%, 4/5 = 85%, 5/5 = 95%. Hai kiểm tra trở
    /// xuống vẫn giữ thấp vì chưa đủ nguồn độc lập để coi là heading đáng tin.
    /// </summary>
    public static double ConfidenceForChecks(int passed) => Math.Clamp(passed, 0, 5) switch
    {
        5 => 0.95,
        4 => 0.85,
        3 => 0.80,
        2 => 0.70,
        1 => 0.60,
        _ => 0.50,
    };

    private static int? ParentIndex(IReadOnlyList<HeadingRecord> ordered, int at)
    {
        var level = ordered[at].Level;
        if (level <= 1) return null;
        for (var i = at - 1; i >= 0; i--)
            if (ordered[i].Level == level - 1) return ordered[i].Index;
            else if (ordered[i].Level < level - 1) return null;
        return null;
    }

    private static bool SameFormatting(IEnumerable<SlimParagraph> paragraphs)
    {
        var formats = paragraphs.Select(PrefixFormat).ToList();
        if (formats.Count < 2) return false;
        var first = formats[0];
        return formats.All(x => x.Bold == first.Bold && x.Alignment == first.Alignment &&
            (x.Size is null || first.Size is null || Math.Abs(x.Size.Value - first.Size.Value) <= 0.5));
    }

    private static (bool Bold, string Alignment, double? Size) PrefixFormat(SlimParagraph p)
    {
        var first = p.TextSpans.FirstOrDefault(x => x.End > x.Start);
        return first is null
            ? (p.Bold, p.Alignment ?? "left", p.FontSizePt)
            : (first.Bold, p.Alignment ?? "left", first.FontSizePt ?? p.FontSizePt);
    }
}
