using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Semantics.Canonical;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;

namespace DocxHeaderExtractor.DocumentProcessing.Materialization;

/// <summary>
/// The bounded placement/recovery pass shared by the PDF and DOCX canonical lanes. The semantic
/// heading list is already settled when this stage runs; this coordinator may add a placement
/// relation only for an unresolved heading and never adds, removes, or rewrites a heading.
/// </summary>
internal static class HeadingParentResolver
{
    private const string RawPlacementPrompt = """
        You are the structural stage of the A99 canonical document pipeline. The heading list below
        is already settled: do not add, remove, rename or re-judge any entry. Decide one thing only
        — where each heading in "toPlace" sits relative to the others.

        Answer with {"placements":[{"alias":"<alias>","parent":"<alias>|ROOT|NONE"}]}.
          "<alias>" - it belongs under that heading, which must appear earlier in the list.
          "ROOT"    - it is a top-level section of this document.
          "NONE"    - it is a heading but holds no position in the section tree: the document's own
                      title or subtitle, a meeting date or venue line, a running header, a table or
                      figure label, a form label, an annex label.
        Omit an alias entirely if the evidence still does not let you decide. Never return a level:
        the harness derives depth from the relations you give.
        """;

    /// <summary>The placement prompt, line endings settled (a prompt is bytes on the wire).</summary>
    internal static string PlacementPrompt { get; } = RawPlacementPrompt.ReplaceLineEndings("\n");

    /// <summary>
    /// Re-asks only about headings the first pass left unplaced. Provider authority remains with
    /// the transport supplied by the caller; the PDF experiment caller supplies its gated
    /// transport, so this stage has no alternate transport or budget path.
    /// </summary>
    public static async Task<IReadOnlyList<CanonicalSemanticBoundHeading>> PlaceUnresolvedHeadingsAsync(
        IReadOnlyList<CanonicalSemanticBoundHeading> bound,
        IInferenceTransport transport,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bound);
        ArgumentNullException.ThrowIfNull(transport);

        var derived = HeadingHierarchyResolver.DeriveHierarchyFromModelRelations(bound);
        var unplaced = derived
            .Where(item => item.Resolution == HeadingHierarchyResolver.Unresolved)
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
            raw = await transport.BoundaryCutAsync(
                PlacementPrompt,
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
