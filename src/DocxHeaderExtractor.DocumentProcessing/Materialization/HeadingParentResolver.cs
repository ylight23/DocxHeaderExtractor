using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Authority;

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
        CancellationToken cancellationToken) =>
        (await PlaceWithObservationAsync(bound, transport, cancellationToken).ConfigureAwait(false)).Headings;

    internal sealed record PlacementResult(
        IReadOnlyList<CanonicalSemanticBoundHeading> Headings,
        HeadingPlacementExecutionObservation Observation);

    internal static async Task<PlacementResult> PlaceWithObservationAsync(
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
        if (unplaced.Count == 0)
            return Observe(bound, unplaced, "placement-not-required");

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
            return Observe(bound, unplaced, "placement-transport-failed", failureClass: exception.GetType().Name);
        }

        Dictionary<string, string> parentByAlias;
        var responseEntryCount = 0;
        var malformedEntries = false;
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("placements", out var placements) ||
                placements.ValueKind != JsonValueKind.Array)
                throw new JsonException("placement-root-shape-invalid");
            responseEntryCount = placements.GetArrayLength();
            malformedEntries = placements.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("alias", out var alias) || alias.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("parent", out var parent) || parent.ValueKind != JsonValueKind.String);
            parentByAlias = placements.EnumerateArray()
                    .Where(item => item.TryGetProperty("alias", out _) && item.TryGetProperty("parent", out _))
                    .GroupBy(item => item.GetProperty("alias").GetString() ?? string.Empty, StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => group.First().GetProperty("parent").GetString() ?? string.Empty,
                        StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return Observe(bound, unplaced, "placement-invalid-response", responseEntryCount, exception.GetType().Name);
        }

        var aliasesToPlace = ordered.Where(item => unplaced.Contains(item.SourceId))
            .Select(item => item.Alias).ToHashSet(StringComparer.Ordinal);
        var placed = bound.Select(item =>
        {
            // Only a heading that was actually unresolved may gain a relation here, so a second
            // pass can never overwrite what the semantic pass already decided.
            if (!aliasesToPlace.Contains(item.Alias)) return item;
            if (!parentByAlias.TryGetValue(item.Alias, out var parent) || string.IsNullOrWhiteSpace(parent))
                return item;
            return item with { RelationHints = [.. item.RelationHints, $"parent-node:{parent}"] };
        }).ToArray();
        // Preserve the existing per-row admission behavior while observing malformed omitted rows.
        // A syntactically parseable response is not automatically a successful placement response.
        return Observe(placed, unplaced, malformedEntries ? "placement-invalid-response" : "placement-accepted",
            responseEntryCount, malformedEntries ? "placement-entry-schema-invalid" : null);
    }

    internal static PlacementResult Observe(
        IReadOnlyList<CanonicalSemanticBoundHeading> headings,
        IReadOnlySet<string> requested,
        string status,
        int responseEntryCount = 0,
        string? failureClass = null)
    {
        var hierarchy = HeadingHierarchyResolver.DeriveHierarchyFromModelRelations(headings)
            .ToDictionary(item => item.SourceId, StringComparer.Ordinal);
        var decisions = headings.Where(item => requested.Contains(item.SourceId))
            .GroupBy(item => item.SourceId, StringComparer.Ordinal).Select(group => group.First()).Select(item =>
            new HeadingPlacementDecisionObservation(item.SourceId, item.Alias, hierarchy[item.SourceId].Resolution)).ToArray();
        var placed = decisions.Count(item => item.Resolution is HeadingHierarchyResolver.ResolvedParent or HeadingHierarchyResolver.ResolvedRoot);
        var outside = decisions.Count(item => item.Resolution == HeadingHierarchyResolver.OutOfHierarchy);
        var unresolved = decisions.Count(item => item.Resolution == HeadingHierarchyResolver.Unresolved);
        if (status == "placement-accepted" && placed + outside == 0) status = "placement-unresolved";
        var requestedCount = status == "placement-not-requested" ? 0 : requested.Count;
        return new(headings, new(status, requestedCount, responseEntryCount, placed, outside, unresolved, failureClass, decisions));
    }
}
