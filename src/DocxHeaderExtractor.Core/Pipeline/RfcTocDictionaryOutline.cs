using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Application.Policy;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Dựng outline cho RFC/PDF text-layout bằng chính mục lục text của tài liệu.
/// <para>
/// Các RFC chuyển từ PDF thường gom cả trang vào một paragraph, làm tầng ứng viên chỉ thấy vài
/// đoạn dài. Mục lục lại giữ đầy đủ số mục + tiêu đề trong vài paragraph mật độ cao. Bộ dựng này
/// chỉ chạy khi có cụm mật độ rõ: đọc dictionary từ TOC, rồi neo mỗi số mục vào occurrence đầu tiên
/// ở thân bài theo thứ tự tài liệu.
/// </para>
/// </summary>
public static class RfcTocDictionaryOutline
{
    public const string Basis = "rfc_toc_dictionary";

    private const int MinimumDictionaryEntries = 20;
    private const double MinimumBodyMatchRatio = 0.90;
    private const int MinimumDenseParagraphMarks = 6;
    private const int MinimumDensityGap = 3;

    private static readonly Regex NumberMarkerRx = new(
        // RFC TOC text extracted from some generated DOCX files omits the space after the marker.
        // Keep marker parsing unchanged otherwise; this is the candidate-generation boundary.
        @"(?<![\w.])(?<num>\d{1,2}(?:\.\d{1,2}){0,3})\.\s*(?=[A-Za-z])",
        RegexOptions.Compiled);

    private static readonly Regex AppendixMarkerRx = new(
        @"(?<![\w.])Appendix\s+(?<num>[A-Z])\.\s*(?=[A-Z])",
        RegexOptions.Compiled);

    private static readonly Regex AppendixChildMarkerRx = new(
        @"(?<![\w.])(?<app>[A-Z])(?<tail>(?:\.\d{1,2}){1,3})\.\s*(?=[A-Za-z])",
        RegexOptions.Compiled);

    private static readonly Regex CrossReferencePrefixRx = new(
        @"(\b(?:Section|Appendix|see|per|in|of|and)|,)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RepeatedHeaderPrefixRx = new(@"^(.{10,60}?)\s+(?=[A-Z0-9])", RegexOptions.Compiled);
    private static readonly Regex RfcFooterRx = new(@"\s*\S[^.]{0,40}?Standards Track Page\s*\d*\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TocTailRx = new(
        @"\s+(Acknowledgements|Index|Authors.{0,3} Addresses|Contributors).*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TocBackMatterTailRx = new(
        @"\s+Acknowledgments\s+(?=Index|Authors.{0,3} Addresses|Contributors).*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TocBodyBoundaryRx = new(
        @"(?:(?:Acknowledg(?:e)?ments?|Index|Contributors)\s+){1,4}Authors.{0,3}\s+Addresses\s+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static List<HeadingRecord> Build(SlimDocument document)
    {
        var result = Analyze(document);
        return result.Accepted ? result.Headings.ToList() : [];
    }

    public static RfcTocDictionaryResult Analyze(SlimDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return AnalyzeCore(document.Paragraphs.Cast<IPolicyParagraph>().ToArray());
    }

    public static RfcTocDictionaryResult Analyze(IReadOnlyList<IPolicyParagraph> paragraphs) =>
        AnalyzeCore(paragraphs);

    private static RfcTocDictionaryResult AnalyzeCore(IReadOnlyList<IPolicyParagraph> paragraphs)
    {
        ArgumentNullException.ThrowIfNull(paragraphs);
        var orderedParagraphs = paragraphs
            .Where(p => !p.Corrupt && !string.IsNullOrWhiteSpace(p.Text))
            .OrderBy(p => p.Index)
            .ToList();
        if (orderedParagraphs.Count == 0)
            return Reject([], new HashSet<int>(), null, 0, 0, "không có paragraph text hợp lệ");

        var marksByIndex = orderedParagraphs.ToDictionary(p => p.Index, p => Marks(p.Text));
        var tocParagraphs = orderedParagraphs.Where(p => p.TableDepth == 0).ToList();
        var tocMarksByIndex = tocParagraphs.ToDictionary(p => p.Index, p => marksByIndex[p.Index]);
        var tocCluster = FindTocCluster(tocParagraphs, tocMarksByIndex);
        if (tocCluster is not null)
        {
            // Some generated RFC files place individual TOC rows in tables. Discovery stays
            // top-level; once its source window is known, include only marked rows in that window.
            var first = tocCluster.Indexes.Min();
            var last = tocCluster.Indexes.Max();
            var expandedIndexes = tocCluster.Indexes
                .Union(marksByIndex
                    .Where(kv => kv.Key >= first && kv.Key <= last && kv.Value.Count > 0)
                    .Select(kv => kv.Key))
                .ToHashSet();
            tocCluster = tocCluster with { Indexes = expandedIndexes };
        }
        if (tocCluster is null)
            return Reject(orderedParagraphs, new HashSet<int>(), null, 0, 0, "không có cụm TOC dày, sớm và gọn");

        var titles = BuildDictionary(orderedParagraphs, marksByIndex, tocCluster.Indexes);
        if (titles.Count < MinimumDictionaryEntries)
            return Reject(orderedParagraphs, tocCluster.Indexes, tocCluster, titles.Count, 0, "dictionary TOC quá ít mục");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var headings = new List<HeadingRecord>();
        foreach (var p in orderedParagraphs)
        {
            var bodyStart = tocCluster.Indexes.Contains(p.Index) ? BodyStartInTocParagraph(p.Text, marksByIndex[p.Index]) : 0;
            if (tocCluster.Indexes.Contains(p.Index) && bodyStart is null) continue;

            foreach (var mark in marksByIndex[p.Index])
            {
                if (bodyStart is { } start && mark.Start < start) continue;
                if (!titles.TryGetValue(mark.Key, out var entry)) continue;
                if (!seen.Add(mark.Key)) continue;

                headings.Add(new HeadingRecord
                {
                    Index = p.Index,
                    StableId = p.StableId,
                    Level = entry.Level,
                    Text = entry.FullTitle,
                    StyleId = p.StyleId,
                    Source = HeadingSource.Structure,
                    Confidence = 0.95,
                    ConfidenceBasis = Basis,
                    BoundarySource = "rfc_text_toc_dictionary",
                    DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence,
                });
            }
        }

        var matchRatio = titles.Count == 0 ? 0 : (double)headings.Count / titles.Count;
        var tocOnly = Math.Max(0, titles.Count - headings.Count);
        var accepted = matchRatio >= MinimumBodyMatchRatio;
        return new RfcTocDictionaryResult(
            headings,
            BuildDiagnostics(
                orderedParagraphs,
                tocCluster.Indexes,
                tocCluster,
                titles.Count,
                headings.Count,
                tocOnly,
                matchRatio,
                accepted,
                accepted ? "accepted" : "body anchor ratio thấp"));
    }

    private static RfcTocCluster? FindTocCluster(
        IReadOnlyList<IPolicyParagraph> paragraphs,
        IReadOnlyDictionary<int, List<Mark>> marksByIndex)
    {
        var explicitToc = paragraphs.FirstOrDefault(p =>
            p.Text.Contains("Table of Contents", StringComparison.OrdinalIgnoreCase)
            && marksByIndex.TryGetValue(p.Index, out var marks)
            && marks.Count > 0);
        if (explicitToc is not null)
        {
            // Generated RFC DOCX files may split one TOC across paragraphs with different
            // marker densities. The explicit TOC label identifies the candidate window; later
            // dictionary and body-anchor checks remain the acceptance gate.
            var firstTocMark = marksByIndex[explicitToc.Index][0].Key;
            var repeatedFirstMark = marksByIndex
                .Where(kv => kv.Key > explicitToc.Index && kv.Value.Any(mark => mark.Key == firstTocMark))
                .Select(kv => (int?)kv.Key)
                .FirstOrDefault();
            var lastIndex = repeatedFirstMark
                ?? explicitToc.Index + Math.Max(40, paragraphs.Count / 8);
            var explicitIndexes = marksByIndex
                .Where(kv => kv.Key >= explicitToc.Index && kv.Key < lastIndex && kv.Value.Count > 0)
                .Select(kv => kv.Key)
                .ToHashSet();
            var markerCount = explicitIndexes.Sum(index => marksByIndex[index].Count);
            if (explicitIndexes.Count > 0 && markerCount >= MinimumDictionaryEntries)
                return new RfcTocCluster(explicitIndexes, explicitIndexes.Min(index => marksByIndex[index].Count), 0);
        }

        var paragraphCount = paragraphs.Count;
        var densities = marksByIndex
            .Select(kv => (kv.Key, Count: kv.Value.Count))
            .Where(x => x.Count > 0)
            .ToList();
        if (densities.Count == 0) return null;

        // Slim indexes include paragraphs inside tables that this analyzer filtered out.
        // Use the eligible sequence ordinal for front-matter locality, while retaining raw
        // paragraph indexes for identity and all downstream matching.
        var eligibleOrdinalByIndex = marksByIndex.Keys
            .OrderBy(index => index)
            .Select((index, ordinal) => (index, ordinal))
            .ToDictionary(x => x.index, x => x.ordinal);

        var distinct = densities
            .Select(x => x.Count)
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        var maxDenseParagraphs = Math.Max(2, paragraphCount / 5);
        var best = (Gap: 0, Threshold: 0, MarkerCount: 0);
        for (var i = 0; i + 1 < distinct.Count; i++)
        {
            var low = distinct[i];
            var high = distinct[i + 1];
            var gap = high - low;
            if (gap < MinimumDensityGap || high < MinimumDenseParagraphMarks) continue;

            var dense = densities.Where(x => x.Count >= high).ToList();
            var markerCount = dense.Sum(x => x.Count);
            if (dense.Count > maxDenseParagraphs || markerCount < MinimumDictionaryEntries) continue;
            if (!IsCompactFrontMatterCluster(paragraphCount, dense.Select(x => eligibleOrdinalByIndex[x.Key]))) continue;

            if (markerCount > best.MarkerCount || (markerCount == best.MarkerCount && gap > best.Gap))
                best = (gap, high, markerCount);
        }

        if (best.Threshold == 0) return null;
        var indexes = densities
            .Where(x => x.Count >= best.Threshold)
            .Select(x => x.Key)
            .ToHashSet();
        return new RfcTocCluster(indexes, best.Threshold, best.Gap);
    }

    private static bool IsCompactFrontMatterCluster(int paragraphCount, IEnumerable<int> indexes)
    {
        var ordered = indexes.OrderBy(x => x).ToList();
        if (ordered.Count == 0) return false;

        var maxStart = Math.Max(20, paragraphCount / 5);
        if (ordered[0] > maxStart) return false;

        var maxSpan = Math.Max(12, paragraphCount / 8);
        return ordered[^1] - ordered[0] <= maxSpan;
    }

    private static Dictionary<string, TocTitle> BuildDictionary(
        IReadOnlyList<IPolicyParagraph> paragraphs,
        IReadOnlyDictionary<int, List<Mark>> marksByIndex,
        IReadOnlySet<int> tocIndexes)
    {
        var repeatedHeader = RepeatedHeaderPattern(paragraphs);
        var result = new Dictionary<string, TocTitle>(StringComparer.Ordinal);

        foreach (var p in paragraphs)
        {
            if (!tocIndexes.Contains(p.Index)) continue;
            var marks = marksByIndex[p.Index];
            for (var i = 0; i < marks.Count; i++)
            {
                var mark = marks[i];
                var next = i + 1 < marks.Count ? marks[i + 1].Start : p.Text.Length;
                var title = p.Text[mark.End..next];
                if (repeatedHeader is not null) title = repeatedHeader.Replace(title, " ");
                title = Regex.Replace(title, @"\s+", " ").Trim(' ', '.');
                title = TocBackMatterTailRx.Replace(title, "").Trim(' ', '.');
                title = TocTailRx.Replace(title, "").Trim(' ', '.');
                title = RfcFooterRx.Replace(title, "").Trim(' ', '.');
                if (title.Length is <= 2 or >= 80) continue;

                result.TryAdd(mark.Key, new TocTitle(FullTitle(mark, title), LevelOf(mark)));
            }
        }

        return result;
    }

    private static Regex? RepeatedHeaderPattern(IReadOnlyList<IPolicyParagraph> paragraphs)
    {
        var candidates = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in paragraphs)
        {
            var match = RepeatedHeaderPrefixRx.Match(p.Text);
            if (match.Success)
                candidates[match.Groups[1].Value] = candidates.GetValueOrDefault(match.Groups[1].Value) + 1;
        }

        var top = candidates.OrderByDescending(kv => kv.Value).FirstOrDefault();
        return top.Value >= 5 ? new Regex(@"\s*" + Regex.Escape(top.Key) + @"\s*", RegexOptions.Compiled) : null;
    }

    private static int? BodyStartInTocParagraph(string text, IReadOnlyList<Mark> marks)
    {
        var restart = BodyStartFromMarkerRestart(marks);
        if (restart is not null) return restart;

        var matches = TocBodyBoundaryRx.Matches(text);
        return matches.Count == 0 ? null : matches[^1].Index + matches[^1].Length;
    }

    private static int? BodyStartFromMarkerRestart(IReadOnlyList<Mark> marks)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Mark? previous = null;
        for (var i = 0; i < marks.Count; i++)
        {
            var mark = marks[i];
            if (i >= 3 && (seen.Contains(mark.Key) || (previous is not null && CompareKeys(mark.Key, previous.Key) < 0)))
                return mark.Start;

            seen.Add(mark.Key);
            previous = mark;
        }

        return null;
    }

    private static List<Mark> Marks(string text)
    {
        var marks = new List<Mark>();
        foreach (Match match in NumberMarkerRx.Matches(text))
        {
            var lookback = text[Math.Max(0, match.Index - 12)..match.Index];
            if (!CrossReferencePrefixRx.IsMatch(lookback))
                marks.Add(new Mark(match.Groups["num"].Value, false, match.Index, match.Index + match.Length));
        }

        foreach (Match match in AppendixMarkerRx.Matches(text))
            marks.Add(new Mark("APP:" + match.Groups["num"].Value, true, match.Index, match.Index + match.Length));

        foreach (Match match in AppendixChildMarkerRx.Matches(text))
        {
            var lookback = text[Math.Max(0, match.Index - 12)..match.Index];
            if (CrossReferencePrefixRx.IsMatch(lookback)) continue;

            marks.Add(new Mark(
                "APP:" + match.Groups["app"].Value + match.Groups["tail"].Value,
                true,
                match.Index,
                match.Index + match.Length));
        }

        return marks.OrderBy(m => m.Start).ToList();
    }

    private static string FullTitle(Mark mark, string title)
    {
        if (!mark.Appendix) return $"{mark.Key}. {title}";

        var label = mark.Key[4..];
        return label.Contains('.', StringComparison.Ordinal)
            ? $"{label}. {title}"
            : $"Appendix {label} {title}";
    }

    private static int LevelOf(Mark mark)
    {
        if (!mark.Appendix)
            return mark.Key.Split('.', StringSplitOptions.RemoveEmptyEntries).Length;

        return mark.Key[4..].Split('.', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static int CompareKeys(string left, string right)
    {
        var leftParts = SortParts(left);
        var rightParts = SortParts(right);
        var count = Math.Max(leftParts.Length, rightParts.Length);
        for (var i = 0; i < count; i++)
        {
            var a = i < leftParts.Length ? leftParts[i] : -1;
            var b = i < rightParts.Length ? rightParts[i] : -1;
            var cmp = a.CompareTo(b);
            if (cmp != 0) return cmp;
        }

        return 0;
    }

    private static int[] SortParts(string key)
    {
        if (key.StartsWith("APP:", StringComparison.Ordinal))
        {
            var label = key[4..];
            var parts = new List<int> { 1000, label[0] };
            parts.AddRange(label
                .Split('.', StringSplitOptions.RemoveEmptyEntries)
                .Skip(1)
                .Select(int.Parse));
            return parts.ToArray();
        }

        return key
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(int.Parse)
            .ToArray();
    }

    private static RfcTocDictionaryResult Reject(
        IReadOnlyList<IPolicyParagraph> paragraphs,
        IReadOnlySet<int> tocIndexes,
        RfcTocCluster? cluster,
        int dictionaryEntries,
        int bodyAnchors,
        string reason) =>
        new(
            [],
            BuildDiagnostics(
                paragraphs,
                tocIndexes,
                cluster,
                dictionaryEntries,
                bodyAnchors,
                Math.Max(0, dictionaryEntries - bodyAnchors),
                dictionaryEntries == 0 ? 0 : (double)bodyAnchors / dictionaryEntries,
                accepted: false,
                reason));

    private static RfcTocDictionaryDiagnostics BuildDiagnostics(
        IReadOnlyList<IPolicyParagraph> paragraphs,
        IReadOnlySet<int> tocIndexes,
        RfcTocCluster? cluster,
        int dictionaryEntries,
        int bodyAnchors,
        int tocOnlyEntries,
        double bodyAnchorRatio,
        bool accepted,
        string reason)
    {
        var densityByIndex = paragraphs.ToDictionary(p => p.Index, p => Marks(p.Text).Count);
        var tocDensities = tocIndexes.Select(i => densityByIndex.GetValueOrDefault(i)).Where(v => v > 0).ToList();
        var bodyDensities = densityByIndex
            .Where(kv => !tocIndexes.Contains(kv.Key))
            .Select(kv => kv.Value)
            .Where(v => v > 0)
            .ToList();

        return new RfcTocDictionaryDiagnostics(
            TextParagraphs: paragraphs.Count,
            TocParagraphs: tocIndexes.Count,
            TocThreshold: cluster?.Threshold ?? 0,
            DensityGap: cluster?.Gap ?? 0,
            MinTocDensity: tocDensities.Count == 0 ? 0 : tocDensities.Min(),
            MaxNonTocDensity: bodyDensities.Count == 0 ? 0 : bodyDensities.Max(),
            DictionaryEntries: dictionaryEntries,
            BodyAnchors: bodyAnchors,
            TocOnlyEntries: tocOnlyEntries,
            BodyAnchorRatio: bodyAnchorRatio,
            Accepted: accepted,
            Reason: reason);
    }

    private sealed record Mark(string Key, bool Appendix, int Start, int End);
    private sealed record TocTitle(string FullTitle, int Level);
    private sealed record RfcTocCluster(IReadOnlySet<int> Indexes, int Threshold, int Gap);
}

public sealed record RfcTocDictionaryResult(
    IReadOnlyList<HeadingRecord> Headings,
    RfcTocDictionaryDiagnostics Diagnostics)
{
    public bool Accepted => Diagnostics.Accepted;
}

public sealed record RfcTocDictionaryDiagnostics(
    int TextParagraphs,
    int TocParagraphs,
    int TocThreshold,
    int DensityGap,
    int MinTocDensity,
    int MaxNonTocDensity,
    int DictionaryEntries,
    int BodyAnchors,
    int TocOnlyEntries,
    double BodyAnchorRatio,
    bool Accepted,
    string Reason);
