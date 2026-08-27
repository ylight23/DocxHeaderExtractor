using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Restores hierarchy from numbering observed in PDF-grounded heading text when visual styles do
/// not carry a hierarchy. The rule is document-local: it learns marker signatures in first-seen
/// order and never depends on a language-specific list of heading labels.
/// </summary>
internal static class PdfMarkerHierarchyResolver
{
    public static int Apply(IReadOnlyList<HeadingRecord> headings)
    {
        var ordered = headings
            .OrderBy(heading => heading.Index)
            .ThenBy(heading => heading.HeadingSpan?.Start ?? 0)
            .ToArray();
        var tokens = ordered
            .Select(heading =>
            {
                var token = NumberingAudit.Parse(heading.Text);
                var looseMarker = token is null
                    ? PdfLayoutEvidenceOutline.ParseLooseLabelledMarkerForAudit(heading.Text)
                    : null;
                var signature = token?.Signature ?? LooseMarkerSignature(looseMarker);
                return (Heading: heading, Token: token, Signature: signature);
            })
            .Where(item => item.Signature is not null)
            .ToArray();

        // A lone marker family does not prove an absolute level. Keep the existing visual level
        // in that case and let the audit report it as unresolved rather than inventing a root.
        var tiers = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, _, signature) in tokens)
            if (!tiers.ContainsKey(signature!)) tiers[signature!] = tiers.Count + 1;

        var changed = 0;
        foreach (var (heading, token, signature) in tokens)
        {
            var level = token is { Kind: NumberKind.Arabic, Depth: > 1 }
                ? token.Value.Depth
                : tiers.Count >= 2 ? tiers[signature!] : heading.Level ?? 1;
            level = Math.Clamp(level, 1, 9);
            if (heading.Level == level) continue;

            heading.Level = level;
            changed++;
        }

        return changed;
    }

    private static string? LooseMarkerSignature(string? canonicalMarker)
    {
        var separator = canonicalMarker?.IndexOf(':') ?? -1;
        return separator is > 0 ? $"loose-label:{canonicalMarker![..separator]}" : null;
    }
}
