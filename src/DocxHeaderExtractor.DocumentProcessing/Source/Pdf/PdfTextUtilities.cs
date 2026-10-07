namespace DocxHeaderExtractor.DocumentProcessing.Source.Pdf;

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
