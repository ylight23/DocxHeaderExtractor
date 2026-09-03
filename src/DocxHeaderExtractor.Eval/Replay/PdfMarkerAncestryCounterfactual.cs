using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Eval;

/// <summary>
/// M8.1d-3 counterfactual audit. It answers one question offline: if the recovered marker
/// components were serialized into a hierarchy path, which ancestors would actually be present?
/// <para>
/// It reads a frozen facts artifact and nothing else — no model, no gold, no production resolver.
/// It writes no decision back: <c>MarkerPath</c>, <c>ResolvedLevel</c>,
/// <c>MarkerPrefixParentCandidate</c>, and the observed ancestor pool are left exactly as frozen.
/// Because gold is not joined here, outcomes are named structurally
/// (<c>supported</c>/<c>unsupported</c>/<c>ambiguous</c>) and never as true/false positives.
/// </para>
/// </summary>
public static class PdfMarkerAncestryCounterfactual
{
    public static PdfMarkerCounterfactualReport Evaluate(string artifactJson)
    {
        var facts = ReadFacts(artifactJson);
        var occurrences = facts
            .Select(fact =>
            {
                var (components, origin) = ResolveComponents(fact);
                return new Occurrence(fact, components, origin,
                    components.Count == 0 ? null : string.Join('.', components));
            })
            .OrderBy(item => item.Fact.SourceOrder)
            .ToArray();

        var items = new List<PdfMarkerCounterfactualItem>();
        foreach (var child in occurrences)
        {
            // Depth 1 has no ancestor to gain, and a null path has no hypothesis at all. Both are
            // still counted so the eligible denominator stays honest.
            if (child.Components.Count < 2) continue;

            var immediatePrefix = child.Components.Take(child.Components.Count - 1).ToArray();
            var immediateMatches = MatchesFor(occurrences, child, immediatePrefix);
            var immediateStatus = immediateMatches.Count switch
            {
                0 => "unsupported",
                1 => "supported",
                _ => "ambiguous",
            };

            var required = new List<string>();
            var resolved = new List<string>();
            var chainAmbiguous = false;
            for (var length = 1; length < child.Components.Count; length++)
            {
                var prefix = child.Components.Take(length).ToArray();
                required.Add(string.Join('.', prefix));
                var matches = MatchesFor(occurrences, child, prefix);
                if (matches.Count == 1) resolved.Add(string.Join('.', prefix));
                else if (matches.Count > 1) chainAmbiguous = true;
            }
            var chainStatus = chainAmbiguous
                ? "ambiguous"
                : resolved.Count == required.Count ? "supported"
                : resolved.Count == 0 ? "unsupported"
                : "partial";

            items.Add(new PdfMarkerCounterfactualItem(
                child.Fact.FactId.Length > 0 ? child.Fact.FactId : child.Fact.Id,
                child.Fact.Id,
                child.Fact.SourceOrder,
                child.Fact.Page,
                child.Fact.StructuralScope,
                child.Fact.DocumentRegime,
                child.Fact.MarkerFamily,
                child.ComponentsOrigin,
                new PdfMarkerCounterfactualCurrent(child.Fact.MarkerPath, child.Fact.ResolvedLevel,
                    child.Fact.MarkerPrefixParentCandidate),
                new PdfMarkerCounterfactualRecovered(child.Components, child.HypotheticalPath!),
                new PdfMarkerCounterfactualPrefix(string.Join('.', immediatePrefix),
                    immediateMatches.Count == 1 ? immediateMatches[0] : null, immediateMatches.Count, immediateStatus),
                new PdfMarkerCounterfactualChain(required, resolved, chainStatus)));
        }

        return new PdfMarkerCounterfactualReport(
            facts.Count,
            occurrences.Count(item => item.Components.Count > 0),
            items.Count,
            Tally(items, item => item.ImmediatePrefix.Status),
            Tally(items, item => item.FullChain.Status),
            BuildDelta(items, "immediate_prefix"),
            BuildDelta(items, "full_chain"),
            items.GroupBy(item => item.MarkerFamily ?? "none")
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new PdfMarkerCounterfactualBreakdown(group.Key,
                    group.Count(), Tally(group, item => item.ImmediatePrefix.Status),
                    Tally(group, item => item.FullChain.Status)))
                .ToArray(),
            occurrences.GroupBy(item => DepthBucket(item.Components.Count))
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new PdfMarkerDepthBreakdown(group.Key, group.Count(),
                    group.Count(item => item.Fact.ResolvedLevel is not null),
                    group.Count(item => item.Fact.MarkerPrefixParentCandidate is not null)))
                .ToArray(),
            "not_measured",
            items);
    }

    private static IReadOnlyList<string> MatchesFor(IReadOnlyList<Occurrence> occurrences, Occurrence child, int[] prefix)
    {
        var key = string.Join('.', prefix);
        return occurrences
            .Where(candidate => candidate.HypotheticalPath == key &&
                candidate.Fact.SourceOrder < child.Fact.SourceOrder &&
                string.Equals(candidate.Fact.StructuralScope, child.Fact.StructuralScope, StringComparison.Ordinal) &&
                string.Equals(candidate.Fact.DocumentRegime, child.Fact.DocumentRegime, StringComparison.Ordinal))
            .Select(candidate => candidate.Fact.FactId.Length > 0 ? candidate.Fact.FactId : candidate.Fact.Id)
            .ToArray();
    }

    /// <summary>
    /// Exactly what production would gain if this variant were promoted. A level that merely stays
    /// the same is not a delta; a level that would change from one confident value to another is.
    /// </summary>
    private static PdfMarkerAuthorityDelta BuildDelta(IReadOnlyList<PdfMarkerCounterfactualItem> items, string variant)
    {
        bool Supported(PdfMarkerCounterfactualItem item) => variant == "immediate_prefix"
            ? item.ImmediatePrefix.Status == "supported"
            : item.FullChain.Status == "supported";
        return new PdfMarkerAuthorityDelta(
            variant,
            items.Count(item => Supported(item) && item.Current.MarkerPrefixParentCandidate is null),
            items.Count(item => Supported(item) && item.Current.ResolvedLevel is null),
            items.Count(item => Supported(item) && item.Current.ResolvedLevel is { } level &&
                level != item.Recovered.Components.Count),
            items.Count(item => !Supported(item)));
    }

    private static IReadOnlyList<PdfMarkerStatusCount> Tally(
        IEnumerable<PdfMarkerCounterfactualItem> items,
        Func<PdfMarkerCounterfactualItem, string> selector) =>
        items.GroupBy(selector)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new PdfMarkerStatusCount(group.Key, group.Count()))
            .ToArray();

    private static string DepthBucket(int depth) => depth switch
    {
        0 => "depth_none",
        1 => "depth_1",
        2 => "depth_2",
        3 => "depth_3",
        _ => "depth_4_plus",
    };

    /// <summary>
    /// Components come from the artifact when it was frozen after they were preserved. Older frozen
    /// runs predate that field, so they are re-derived from the artifact's own immutable source
    /// text — a deterministic replay of the same parser, never a new observation.
    /// </summary>
    private static (IReadOnlyList<int> Components, string Origin) ResolveComponents(PdfHierarchyFactAudit fact)
    {
        if (fact.MarkerComponents.Count > 0) return (fact.MarkerComponents, "artifact");
        if (fact.SourceBlockText.Length == 0) return ([], "unavailable");
        var marker = PdfMarkerFactsParser.Parse(fact.SourceBlockText);
        if (marker is not { Components.IsDefaultOrEmpty: false } parsed) return ([], "reparsed_no_components");
        return (parsed.Components, "reparsed_frozen_source");
    }

    private static IReadOnlyList<PdfHierarchyFactAudit> ReadFacts(string artifactJson)
    {
        using var document = JsonDocument.Parse(artifactJson);
        var root = RootObject(document.RootElement);
        var items = root.TryGetProperty("hierarchyFacts", out var hierarchyFacts) &&
                    hierarchyFacts.TryGetProperty("items", out var nested)
            ? nested
            : root.TryGetProperty("items", out var direct) ? direct : default;
        if (items.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Frozen artifact không có hierarchy facts items.");
        return JsonSerializer.Deserialize<List<PdfHierarchyFactAudit>>(items.GetRawText(),
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException("Không đọc được hierarchy facts frozen.");
    }

    private static JsonElement RootObject(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("rows", out var rows) &&
            rows.ValueKind == JsonValueKind.Array)
        {
            if (rows.GetArrayLength() != 1)
                throw new InvalidOperationException("Counterfactual audit cần artifact có đúng một row.");
            return rows[0];
        }
        if (root.ValueKind == JsonValueKind.Object) return root;
        throw new InvalidOperationException("Artifact JSON phải là object hoặc wrapper có đúng một row.");
    }

    private sealed record Occurrence(
        PdfHierarchyFactAudit Fact,
        IReadOnlyList<int> Components,
        string ComponentsOrigin,
        string? HypotheticalPath);
}

public sealed record PdfMarkerCounterfactualReport(
    int FrozenFacts,
    int FactsWithComponents,
    int EligibleRecoveredPaths,
    IReadOnlyList<PdfMarkerStatusCount> ImmediatePrefix,
    IReadOnlyList<PdfMarkerStatusCount> FullChain,
    PdfMarkerAuthorityDelta ImmediatePrefixDelta,
    PdfMarkerAuthorityDelta FullChainDelta,
    IReadOnlyList<PdfMarkerCounterfactualBreakdown> ByFamily,
    IReadOnlyList<PdfMarkerDepthBreakdown> ByDepth,
    string GoldGrading,
    IReadOnlyList<PdfMarkerCounterfactualItem> Items);

public sealed record PdfMarkerStatusCount(string Status, int Count);

public sealed record PdfMarkerAuthorityDelta(
    string Variant,
    int WouldCreateNewParentCandidate,
    int WouldCreateNewLevelResolution,
    int WouldChangeExistingLevel,
    int WouldRemainUnsupported);

public sealed record PdfMarkerCounterfactualBreakdown(
    string MarkerFamily,
    int Eligible,
    IReadOnlyList<PdfMarkerStatusCount> ImmediatePrefix,
    IReadOnlyList<PdfMarkerStatusCount> FullChain);

public sealed record PdfMarkerDepthBreakdown(
    string DepthBucket,
    int Occurrences,
    int CurrentResolvedLevels,
    int CurrentParentCandidates);

public sealed record PdfMarkerCounterfactualItem(
    string FactId,
    string SourceFactId,
    int SourceOrder,
    int Page,
    string StructuralScope,
    string DocumentRegime,
    string? MarkerFamily,
    string ComponentsOrigin,
    PdfMarkerCounterfactualCurrent Current,
    PdfMarkerCounterfactualRecovered Recovered,
    PdfMarkerCounterfactualPrefix ImmediatePrefix,
    PdfMarkerCounterfactualChain FullChain);

public sealed record PdfMarkerCounterfactualCurrent(
    string? MarkerPath,
    int? ResolvedLevel,
    string? MarkerPrefixParentCandidate);

public sealed record PdfMarkerCounterfactualRecovered(
    IReadOnlyList<int> Components,
    string HypotheticalPath);

public sealed record PdfMarkerCounterfactualPrefix(
    string Prefix,
    string? CandidateId,
    int PrefixCandidateCount,
    string Status);

public sealed record PdfMarkerCounterfactualChain(
    IReadOnlyList<string> RequiredPrefixes,
    IReadOnlyList<string> ResolvedPrefixes,
    string Status);
