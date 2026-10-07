using System.Text.Json;
using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The family of a claim a model proposed that the Gold does not hold, decided from the source (which
/// page band the atom sits in, what its text is shaped like) and from the Gold's own source review -
/// never from a hand-written list of identities.
/// </summary>
internal static partial class ResidualFamilies
{
    [GeneratedRegex(@"^(Bishop Standards Track Page|RFC 9114 HTTP/3 June)")] private static partial Regex Rfc9114Furniture();
    [GeneratedRegex(@"^(Table|Figure|Chart|Exhibit) [A-Z]?\d")] private static partial Regex Caption();

    /// <summary>
    /// The source review these frozen audits read: the vintage over the universe they were produced against when the
    /// authority has migrated (SRC-089), otherwise the active review.
    /// </summary>
    internal static string FrozenReview(string id, string file)
    {
        var vintage = $"eval/a99-closed-loop/gold-history/pdf-universe-v1/source-review/{id}/{file}";
        return File.Exists(TestRepository.Path(vintage)) ? vintage : $"eval/a99-closed-loop/source-review-v1/{id}/{file}";
    }

    internal static Dictionary<string, int> PageOfAlias(string id)
    {
        var rows = File.ReadAllLines(TestRepository.Path(FrozenReview(id, "atom-glyph-facts.tsv")))
            .Select(l => l.Split('\t'));
        return rows.ToDictionary(r => r[0], r => int.Parse(r[1]), StringComparer.Ordinal);
    }

    internal static int PageOf(IReadOnlyDictionary<string, int> pages, string identity)
    {
        var first = identity.Split('|')[0];
        return pages.GetValueOrDefault(first[..first.LastIndexOf(':')], -1);
    }

    internal static IEnumerable<string> AliasesOf(string identity) =>
        identity.Split('|').Select(part => part[..part.LastIndexOf(':')]);

    /// <summary>
    /// The reviewer's own reading of the source decides first - a Gold claim's occurrence, or a non-heading
    /// the review named and why - then the page band, then the shape of the text. The model's role is the
    /// last resort, and says so in the name.
    /// </summary>
    internal static string FalsePositiveFamily(string identity, string text, string role, int page, (int From, int To) contents, int indexFrom,
        IReadOnlySet<string> goldAliases, IReadOnlyDictionary<string, string> reviewedNonHeadings)
    {
        // The same occurrence as a Gold heading, claimed with a different extent - not another heading.
        if (AliasesOf(identity).Any(goldAliases.Contains)) return "GOLD_HEADING_WRONG_EXTENT";
        if (AliasesOf(identity).Select(a => reviewedNonHeadings.GetValueOrDefault(a)).FirstOrDefault(p => p is not null) is { } pattern)
            return $"REVIEWED_NON_HEADING:{pattern}";
        if (Rfc9114Furniture().IsMatch(text)) return "PAGE_FURNITURE";
        if (contents.From > 0 && page >= contents.From && page <= contents.To) return "CONTENTS_ENTRY";
        if (indexFrom > 0 && page >= indexFrom) return "INDEX_ENTRY";
        if (Caption().IsMatch(text)) return "CAPTION";
        return $"MODEL_ROLE_{role.ToUpperInvariant().Replace('-', '_')}";
    }

    /// <summary>What the source-only review named as a set-apart non-heading, by alias: its pattern.</summary>
    internal static Dictionary<string, string> ReviewedNonHeadings(string id)
    {
        using var items = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(FrozenReview(id, "review-items.json"))));
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in items.RootElement.GetProperty("items").EnumerateArray()
                     .Where(i => i.GetProperty("verdict").GetString() == "NON_HEADING"))
        foreach (var part in item.GetProperty("parts").EnumerateArray())
            map[part.GetProperty("sourceAlias").GetString()!] = item.GetProperty("pattern").GetString()!;
        return map;
    }
}
