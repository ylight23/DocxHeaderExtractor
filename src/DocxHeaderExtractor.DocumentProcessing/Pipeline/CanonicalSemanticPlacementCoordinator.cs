using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// The bounded placement/recovery pass shared by the PDF and DOCX canonical lanes. The semantic
/// heading list is already settled when this stage runs; this coordinator may add a placement
/// relation only for an unresolved heading and never adds, removes, or rewrites a heading.
/// </summary>
internal static class CanonicalSemanticPlacementCoordinator
{
    /// <summary>
    /// Re-asks only about headings the first pass left unplaced. Provider authority remains with
    /// the classifier supplied by the caller; the PDF experiment caller supplies its gated
    /// classifier, so this stage has no alternate transport or budget path.
    /// </summary>
    public static async Task<IReadOnlyList<CanonicalSemanticBoundHeading>> PlaceUnresolvedHeadingsAsync(
        IReadOnlyList<CanonicalSemanticBoundHeading> bound,
        IHeaderClassifier classifier,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bound);
        ArgumentNullException.ThrowIfNull(classifier);

        var derived = ModelRelationHierarchyResolver.DeriveHierarchyFromModelRelations(bound);
        var unplaced = derived
            .Where(item => item.Resolution == ModelRelationHierarchyResolver.Unresolved)
            .Select(item => item.SourceId)
            .ToHashSet(StringComparer.Ordinal);
        if (unplaced.Count == 0) return bound;

        var ordered = bound.OrderBy(item => item.SourceOrdinal).ThenBy(item => item.Start).ToArray();
        var packet = JsonSerializer.Serialize(new
        {
            headings = ordered.Select(item => new { alias = item.Alias, text = item.Text }).ToArray(),
            toPlace = ordered.Where(item => unplaced.Contains(item.SourceId))
                .Select(item => item.Alias).ToArray(),
        });

        string raw;
        try
        {
            raw = await classifier.BoundaryCutAsync(
                CanonicalSemanticEngine.PlacementPrompt,
                packet,
                cancellationToken,
                expectedItemCount: unplaced.Count);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Placement is an improvement pass. If it cannot run, the headings stay unresolved,
            // which is exactly what they already were.
            return bound;
        }

        Dictionary<string, string> parentByAlias;
        try
        {
            using var document = JsonDocument.Parse(raw);
            parentByAlias = document.RootElement.TryGetProperty("placements", out var placements)
                ? placements.EnumerateArray()
                    .Where(item => item.TryGetProperty("alias", out _) && item.TryGetProperty("parent", out _))
                    .GroupBy(item => item.GetProperty("alias").GetString() ?? string.Empty, StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => group.First().GetProperty("parent").GetString() ?? string.Empty,
                        StringComparer.Ordinal)
                : [];
        }
        catch (JsonException)
        {
            return bound;
        }

        var aliasesToPlace = ordered.Where(item => unplaced.Contains(item.SourceId))
            .Select(item => item.Alias).ToHashSet(StringComparer.Ordinal);
        return bound.Select(item =>
        {
            // Only a heading that was actually unresolved may gain a relation here, so a second
            // pass can never overwrite what the semantic pass already decided.
            if (!aliasesToPlace.Contains(item.Alias)) return item;
            if (!parentByAlias.TryGetValue(item.Alias, out var parent) || string.IsNullOrWhiteSpace(parent))
                return item;
            return item with { RelationHints = [.. item.RelationHints, $"parent-node:{parent}"] };
        }).ToArray();
    }
}
