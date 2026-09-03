namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Learns the visual style baseline of a PDF from the PDF itself. The body baseline is the
/// style cluster that carries the most readable characters; heading candidates are styles that
/// consistently differ from that baseline. Callers provide document-specific semantic predicates,
/// but the style/baseline measurement is shared across PDF routes.
/// </summary>
internal sealed record PdfStyleClusterProfile(
    PdfStyleKey BodyStyle,
    IReadOnlyList<PdfStyleClusterStats> Clusters,
    IReadOnlySet<PdfStyleKey> CandidateStyles,
    IReadOnlySet<PdfStyleKey> TitleStyles,
    IReadOnlySet<PdfStyleKey> GroupStyles)
{
    public static PdfStyleClusterProfile Learn(
        IReadOnlyList<PdfLine> lines,
        Func<PdfLine, bool>? titleLike = null,
        Func<PdfLine, bool>? groupLike = null,
        double fontSizeBucket = 0.5)
    {
        var readable = lines
            .Where(l => !string.IsNullOrWhiteSpace(l.Text))
            .ToList();

        var styleGroups = readable
            .GroupBy(l => StyleOf(l, fontSizeBucket))
            .Select(g => new PdfStyleClusterStats(
                g.Key,
                g.Sum(l => PdfTextUtilities.Readable(l.Text).Length),
                g.Count(),
                g.Select(l => l.Page).Distinct().Count(),
                titleLike is null ? 0 : g.Count(titleLike),
                groupLike is null ? 0 : g.Count(groupLike),
                g.Average(l => l.FontSize),
                g.Average(l => l.BoldRatio)))
            .OrderByDescending(g => g.Characters)
            .ToList();

        var bodyStyle = styleGroups.FirstOrDefault()?.Style ?? new PdfStyleKey(0, "", "");
        var pages = readable.Select(l => l.Page).Distinct().Count();
        var minimumClusterLines = Math.Max(3, (int)Math.Ceiling(pages * 0.10));

        var candidates = styleGroups
            .Where(s => s.Style != bodyStyle && s.Lines >= minimumClusterLines)
            .Select(s => s.Style)
            .ToHashSet();

        var groupStyles = styleGroups
            .Where(s => candidates.Contains(s.Style) &&
                        s.GroupLikeLines >= Math.Max(2, s.Lines / 4) &&
                        s.TitleLikeLines >= s.GroupLikeLines)
            .Select(s => s.Style)
            .ToHashSet();

        var titleStyles = styleGroups
            .Where(s => candidates.Contains(s.Style) &&
                        s.TitleLikeLines >= Math.Max(3, s.Lines / 3) &&
                        !groupStyles.Contains(s.Style))
            .Select(s => s.Style)
            .ToHashSet();

        return new PdfStyleClusterProfile(bodyStyle, styleGroups, candidates, titleStyles, groupStyles);
    }

    public bool HasHeadingStyles => TitleStyles.Count > 0 || GroupStyles.Count > 0;

    public bool IsCandidateStyle(PdfLine line) => CandidateStyles.Contains(StyleOf(line));

    public bool IsLikelyGroupStyle(PdfLine line)
    {
        var style = StyleOf(line);
        return GroupStyles.Contains(style) ||
               (CandidateStyles.Contains(style) && !TitleStyles.Contains(style));
    }

    public bool IsLikelyTitleStyle(PdfLine line) => TitleStyles.Contains(StyleOf(line));

    public PdfStyleClusterStats? ClusterOf(PdfLine line)
    {
        var style = StyleOf(line);
        return Clusters.FirstOrDefault(c => c.Style == style);
    }

    public static PdfStyleKey StyleOf(PdfLine line, double fontSizeBucket = 0.5)
    {
        var bucket = fontSizeBucket <= 0 ? line.FontSize : Math.Round(line.FontSize / fontSizeBucket) * fontSizeBucket;
        return new PdfStyleKey(bucket, line.FontName, line.FillColorKey);
    }
}

internal sealed record PdfStyleKey(double FontSizeBucket, string FontName, string FillColorKey);

internal sealed record PdfStyleClusterStats(
    PdfStyleKey Style,
    int Characters,
    int Lines,
    int Pages,
    int TitleLikeLines,
    int GroupLikeLines,
    double AverageFontSize,
    double AverageBoldRatio);

internal static class PdfTextUtilities
{
    private static readonly HashSet<string> ShortWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "as", "at", "by", "for", "from", "in", "into", "is", "of", "on", "or",
        "must", "not", "should", "that", "the", "these", "this", "those", "to", "vs", "with"
    };

    public static string Readable(string text)
    {
        var spaced = System.Text.RegularExpressions.Regex.Replace(text, @"(?<=[a-z])(?=[A-Z])", " ");
        spaced = System.Text.RegularExpressions.Regex.Replace(spaced, @"(?<=[A-Za-z])(?=\d)", " ");
        spaced = System.Text.RegularExpressions.Regex.Replace(spaced, @"(?<=\d)(?=[A-Za-z])", " ");
        spaced = System.Text.RegularExpressions.Regex.Replace(spaced, @"\s+", " ");
        return spaced.Trim();
    }

    public static string HeadingReadable(string text)
    {
        var readable = Readable(text);
        if (readable.Length == 0) return readable;

        var tokens = System.Text.RegularExpressions.Regex.Matches(readable, @"\p{L}+|\d+|[^\p{L}\d\s]+")
            .Select(m => m.Value)
            .ToList();
        if (tokens.Count == 0) return readable;

        var result = new List<string>();
        var i = 0;
        while (i < tokens.Count)
        {
            if (!IsWord(tokens[i]))
            {
                result.Add(tokens[i++]);
                continue;
            }

            var run = TakeFragmentRun(tokens, ref i);
            result.Add(run.Count == 1 ? run[0] : string.Concat(run));
        }

        return PolishPdfPunctuationSpacing(NormalizeTokenSpacing(result));
    }

    public static string CanonicalForMatch(string text)
    {
        var readable = Readable(text);
        readable = System.Text.RegularExpressions.Regex.Replace(readable, @"[^\p{L}\p{Nd}_]+", "");
        return readable.ToLowerInvariant();
    }

    private static List<string> TakeFragmentRun(IReadOnlyList<string> tokens, ref int index)
    {
        var first = tokens[index++];
        var run = new List<string> { first };
        if (index >= tokens.Count || !IsWord(tokens[index])) return run;

        if (first.Equals("an", StringComparison.OrdinalIgnoreCase) &&
            tokens[index].Equals("d", StringComparison.OrdinalIgnoreCase))
        {
            run.Add(tokens[index++]);
            return run;
        }

        if (ShortWords.Contains(first)) return run;

        if (first.Length == 1 && first.All(char.IsUpper))
        {
            if (tokens[index].All(char.IsUpper) && tokens[index].Length is >= 2 and <= 4)
            {
                run.Add(tokens[index++]);
                return run;
            }

            if (CountLowerFragments(tokens, index) >= 2)
            {
                while (index < tokens.Count && IsLowerFragment(tokens[index]))
                    run.Add(tokens[index++]);
            }

            return run;
        }

        if (IsLowerFragment(tokens[index]) &&
            (first.Length <= 5 ||
             CountLowerFragments(tokens, index) >= 2 ||
             (first.Length <= 9 && tokens[index].Length <= 2)))
        {
            while (index < tokens.Count && IsLowerFragment(tokens[index]))
                run.Add(tokens[index++]);
        }

        return run;
    }

    private static bool IsWord(string token) => token.All(char.IsLetter);

    private static bool IsLowerFragment(string token) =>
        token.Length <= 5 &&
        token.All(char.IsLower) &&
        !ShortWords.Contains(token);

    private static int CountLowerFragments(IReadOnlyList<string> tokens, int start)
    {
        var count = 0;
        for (var i = start; i < tokens.Count && IsLowerFragment(tokens[i]); i++) count++;
        return count;
    }

    private static string NormalizeTokenSpacing(IReadOnlyList<string> tokens)
    {
        var result = new System.Text.StringBuilder();
        foreach (var token in tokens)
        {
            if (result.Length == 0)
            {
                result.Append(token);
            }
            else if (IsClosingPunctuation(token))
            {
                result.Append(token);
            }
            else if (IsOpeningPunctuation(result[^1].ToString()))
            {
                result.Append(token);
            }
            else
            {
                result.Append(' ').Append(token);
            }
        }

        return result.ToString();
    }

    private static bool IsClosingPunctuation(string token) => token is "." or "," or ";" or ":" or ")" or "]" or "}";

    private static bool IsOpeningPunctuation(string token) => token is "(" or "[" or "{";

    private static string PolishPdfPunctuationSpacing(string text)
    {
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+([’'])\s+", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?<=\p{L})\s*-\s*(?=\p{L})", "-");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\b([A-Z]{2,})[’']S\b", "$1’s");
        return text;
    }
}
