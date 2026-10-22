using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.Contracts.Models.Inference;

namespace DocxHeaderExtractor.Sources.Semantics.Identity;

/// <summary>
/// Global semantic resolution after binding. Repeated display headings remain occurrences even
/// when they share one semantic node; the outline projection is the only collapsing step.
/// </summary>
/// <summary>
/// Resolves semantic identity after exact source binding. Explicit same-node and continuation
/// hints are semantic identity decisions; physical occurrences remain distinct without them.
/// Hierarchy consumes the resulting identity key but does not redefine this rule.
/// </summary>
public static class CanonicalSemanticIdentityResolver
{
    /// <summary>Stable identity key shared by text graph resolution and hierarchy diagnostics.</summary>
    public static string CreateNodeKey(CanonicalSemanticBoundHeading item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var sameNodeHint = item.RelationHints.FirstOrDefault(hint =>
            hint.StartsWith("same-node:", StringComparison.Ordinal) ||
            hint.StartsWith("continuation-node:", StringComparison.Ordinal));
        return sameNodeHint is not null
            ? $"explicit:{sameNodeHint}"
            : $"physical:{item.SourceId}:{item.Start}:{item.End}:{item.Alias}";
    }

}
