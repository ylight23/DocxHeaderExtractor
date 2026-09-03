using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.Core.OpenXmlLayer;

/// <summary>Pure Word built-in heading style identity derived from style metadata.</summary>
public static class BuiltInHeadingStyleIdentity
{
    private static readonly Regex HeadingStyle = new(
        @"^(heading\s*([1-9])|title|subtitle|toc\s*heading)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static int? LevelFromStyleIdentity(string? styleIdentity)
    {
        if (string.IsNullOrWhiteSpace(styleIdentity)) return null;
        var match = HeadingStyle.Match(styleIdentity.Trim());
        if (!match.Success) return null;
        if (match.Groups[2].Success && int.TryParse(match.Groups[2].Value, out var level))
            return level;
        return match.Value.StartsWith("subtitle", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
    }

    public static int? LevelFromResolvedStyle(string? styleName, string? styleId) =>
        LevelFromStyleIdentity(styleName) ?? LevelFromStyleIdentity(styleId);
}
