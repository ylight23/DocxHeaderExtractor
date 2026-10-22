using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.Contracts.Inference;
using DocxHeaderExtractor.Contracts.Authority;
using DocxHeaderExtractor.DocumentProcessing.Audit;
using DocxHeaderExtractor.DocumentProcessing.Output;
using DocxHeaderExtractor.DocumentProcessing.Hierarchy;

namespace DocxHeaderExtractor.DocumentProcessing.Placement;

/// <summary>
/// The bounded placement/recovery pass shared by the PDF and DOCX canonical lanes. The semantic
/// heading list is already settled when this stage runs; this coordinator may add a placement
/// relation only for an unresolved heading and never adds, removes, or rewrites a heading.
/// </summary>
internal static class ParentResolver
{
    private const string RawPlacementPrompt = """
        You are the structural stage of the A99 canonical document pipeline. The heading list below
        is already settled: do not add, remove, rename or re-judge any entry. Decide one thing only
        — where each heading in "toPlace" sits relative to the others.

        Answer with a JSON object {"placements":[{"alias":"<alias>","parent":"<alias>|ROOT|NONE"}]}.
          "<alias>" - it belongs under that heading, which must appear earlier in the list.
          "ROOT"    - it is a top-level section of this document.
          "NONE"    - it is a heading but holds no position in the section tree: the document's own
                      title or subtitle, a meeting date or venue line, a running header, a table or
                      figure label, a form label, an annex label.
        Omit an alias entirely if the evidence still does not let you decide. Never return a level:
        the harness derives depth from the relations you give.
        """;

    // Added only when the caller hands over measurements: what they are, and that they decide nothing.
    private const string RawEvidenceSentence = """
        Each heading may also carry "style" (sizePt and bold of the line, rankInDocument: the place of that size and weight among the distinct
        styles of the whole document, 1 being the largest, and linesWithThisStyleInDocument), and "documentStyles" lists those distinct styles
        with the first lines set in each. They are measurements of the document, not labels and not decisions: you decide each relation.
        """;

    /// <summary>The placement prompt, line endings settled (a prompt is bytes on the wire).</summary>
    internal static string PlacementPrompt { get; } = RawPlacementPrompt.ReplaceLineEndings("\n");

    /// <summary>The placement prompt for a packet that carries measurements.</summary>
    internal static string PlacementPromptWithEvidence { get; } = PlacementPrompt + "\n" + RawEvidenceSentence.ReplaceLineEndings("\n");

    /// <summary>The placement prompt for a packet whose measurements the caller describes itself (a document that is not a PDF).</summary>
    internal static string PlacementPromptWithNote(string note) => PlacementPrompt + "\n" + note.ReplaceLineEndings("\n");

    /// <summary>The user message of the placement request. Without evidence it is exactly the packet the stage always sent.</summary>
    internal static string PlacementPacket(
        IReadOnlyList<CanonicalSemanticBoundHeading> ordered, IReadOnlySet<string> unplaced, HeadingPlacementEvidence? evidence)
    {
        if (evidence is null)
            return JsonSerializer.Serialize(new
            {
                headings = ordered.Select(item => new { alias = item.Alias, text = item.Text }).ToArray(),
                toPlace = ordered.Where(item => unplaced.Contains(item.SourceId)).Select(item => item.Alias).ToArray(),
            });
        var packet = new JsonObject
        {
            ["documentStyles"] = JsonNode.Parse(evidence.DocumentStylesJson),
            ["headings"] = new JsonArray(ordered.Select(item =>
            {
                var heading = new JsonObject { ["alias"] = item.Alias, ["text"] = item.Text };
                if (evidence.StyleJsonByAlias.TryGetValue(item.Alias, out var style)) heading["style"] = JsonNode.Parse(style);
                return (JsonNode)heading;
            }).ToArray()),
            ["toPlace"] = new JsonArray(ordered.Where(item => unplaced.Contains(item.SourceId))
                .Select(item => (JsonNode)JsonValue.Create(item.Alias)!).ToArray()),
        };
        return packet.ToJsonString();
    }

    internal sealed record PlacementResult(
        IReadOnlyList<CanonicalSemanticBoundHeading> Headings,
        PlacementAudit Observation);

    internal static async Task<PlacementResult> PlaceWithObservationAsync(
        IReadOnlyList<CanonicalSemanticBoundHeading> bound,
        IInferenceTransport transport,
        CancellationToken cancellationToken,
        HeadingPlacementEvidence? evidence = null)
    {
        ArgumentNullException.ThrowIfNull(bound);
        ArgumentNullException.ThrowIfNull(transport);

        if (evidence?.Source is not null)
            return await EvidencePlacement.PlaceAsync(bound, transport, cancellationToken, evidence).ConfigureAwait(false);

        var derived = HierarchyResolver.DeriveHierarchyFromModelRelations(bound);
        var unplaced = derived
            .Where(item => item.Resolution == HierarchyResolver.Unresolved)
            .Select(item => item.SourceId)
            .ToHashSet(StringComparer.Ordinal);
        if (unplaced.Count == 0)
            return Observe(bound, unplaced, "placement-not-required");

        var ordered = bound.OrderBy(item => item.SourceOrdinal).ThenBy(item => item.Start).ToArray();
        var packet = PlacementPacket(ordered, unplaced, evidence);

        string raw;
        try
        {
            raw = await transport.CompleteAsync(
                evidence is null ? PlacementPrompt : evidence.MeasurementsNote is { } note ? PlacementPromptWithNote(note) : PlacementPromptWithEvidence,
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
        var hierarchy = HierarchyResolver.DeriveHierarchyFromModelRelations(headings)
            .ToDictionary(item => item.SourceId, StringComparer.Ordinal);
        var decisions = headings.Where(item => requested.Contains(item.SourceId))
            .GroupBy(item => item.SourceId, StringComparer.Ordinal).Select(group => group.First()).Select(item =>
            new HeadingPlacementDecisionObservation(item.SourceId, item.Alias, hierarchy[item.SourceId].Resolution)).ToArray();
        var placed = decisions.Count(item => item.Resolution is HierarchyResolver.ResolvedParent or HierarchyResolver.ResolvedRoot);
        var outside = decisions.Count(item => item.Resolution == HierarchyResolver.OutOfHierarchy);
        var unresolved = decisions.Count(item => item.Resolution == HierarchyResolver.Unresolved);
        if (status == "placement-accepted" && placed + outside == 0) status = "placement-unresolved";
        var requestedCount = status == "placement-not-requested" ? 0 : requested.Count;
        return new(headings, new(status, requestedCount, responseEntryCount, placed, outside, unresolved, failureClass, decisions));
    }
}
