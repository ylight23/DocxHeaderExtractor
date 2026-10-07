namespace DocxHeaderExtractor.Core.Models;

public sealed record CanonicalSemanticBindingValidation(
    bool IsValid,
    IReadOnlyList<string> Errors);

/// <summary>Post-binder guard: coordinates are accepted only when they reproduce source bytes.</summary>
public static class CanonicalSemanticHardBindingValidator
{
    public static CanonicalSemanticBindingValidation Validate(
        IReadOnlyList<CanonicalSemanticBoundHeading> bound,
        IReadOnlyList<SemanticSourceAlias> aliases,
        string actualSourceSha256,
        string expectedSourceSha256)
    {
        ArgumentNullException.ThrowIfNull(bound);
        ArgumentNullException.ThrowIfNull(aliases);
        ArgumentException.ThrowIfNullOrWhiteSpace(actualSourceSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSourceSha256);
        var errors = new List<string>();
        if (!string.Equals(actualSourceSha256, expectedSourceSha256, StringComparison.OrdinalIgnoreCase))
            errors.Add("SOURCE_HASH_MISMATCH");
        var byAlias = aliases.ToDictionary(item => item.Alias, StringComparer.Ordinal);
        foreach (var heading in bound)
        {
            var parts = heading.Parts.Count == 0
                ? [new CanonicalSemanticBoundPart(heading.Alias, heading.SourceId, heading.SourceOrdinal, heading.Text, heading.Start, heading.End)]
                : heading.Parts;
            foreach (var part in parts)
            {
                if (!byAlias.TryGetValue(part.Alias, out var alias))
                {
                    errors.Add("UNKNOWN_ALIAS");
                    continue;
                }
                var localStart = part.Start - alias.SourceSpan.Start;
                var localEnd = part.End - alias.SourceSpan.Start;
                if (!alias.Contains(new StructuralSpan(localStart, localEnd)) ||
                    !string.Equals(alias.Text[localStart..localEnd], part.Text, StringComparison.Ordinal))
                    errors.Add("NON_VERBATIM_BINDING");
            }
        }
        return new(errors.Count == 0, errors);
    }
}
