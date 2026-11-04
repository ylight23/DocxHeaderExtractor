using System.Text;

namespace DocxHeaderExtractor.DocumentProcessing.Gather;

/// <summary>A verified target subsection and its direct content, ending before the next heading.</summary>
public sealed record GatherSubsectionTarget(string Heading, string Location, string? NextHeadingLocation, IReadOnlyList<string> DirectContent);

/// <summary>An exact duplicate retains the complete source fact for provenance and audit.</summary>
public sealed record GatherTargetDuplicate(GatheredFact SourceFact, GatherSubsectionTarget Target, string ComparedText);

public sealed record GatherSubsectionRouting(
    IReadOnlyDictionary<string, IReadOnlyList<GatheredFact>> ByLocation,
    IReadOnlyList<GatherTargetDuplicate> AlreadyInTarget,
    IReadOnlyList<GatheredFact> Unresolved);

/// <summary>
/// Routes a source-bound fact only when its leading label uniquely names one verified target subsection.
/// Similar names, missing labels and repeated names remain unresolved. No semantic match is inferred.
/// </summary>
public static class GatherSubsectionRouter
{
    static string Key(string text) => string.Join(' ', text.Normalize(NormalizationForm.FormC)
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static GatherSubsectionRouting Route(IReadOnlyList<GatheredFact> facts, IReadOnlyList<GatherSubsectionTarget> targets, bool singleTargetVerified = false)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Any(t => string.IsNullOrWhiteSpace(t.Location)) || targets.Select(t => t.Location).Distinct(StringComparer.Ordinal).Count() != targets.Count)
            throw new ArgumentException("Target subsection locations must be nonempty and unique.", nameof(targets));
        if (singleTargetVerified && targets.Count != 1) throw new ArgumentException("A verified direct scope must have exactly one target.", nameof(targets));
        var byLocation = targets.ToDictionary(t => t.Location, _ => new List<GatheredFact>(), StringComparer.Ordinal);
        var duplicates = new List<GatherTargetDuplicate>();
        var unresolved = new List<GatheredFact>();
        foreach (var fact in facts)
        {
            if (fact.Parts.Count == 0) { unresolved.Add(fact); continue; }
            var hasLabel = fact.Parts.Count > 1 && fact.Parts[0].Role == "label";
            var name = hasLabel ? GatherByHeading.Name(fact.Parts[0].Text) : "";
            var matching = hasLabel ? targets.Where(t => name.Length > 0 && GatherByHeading.Name(t.Heading) == name).ToArray() : [];
            if (matching.Length > 1 || matching.Length == 0 && !singleTargetVerified) { unresolved.Add(fact); continue; }
            if (matching.Length == 0) matching = [targets[0]];
            var target = matching[0];
            var prepared = hasLabel && GatherByHeading.Name(target.Heading) == name
                ? fact with { Parts = fact.Parts.Skip(1).ToArray(), AlsoFrom = fact.AlsoFrom.Append(fact.Parts[0]).ToArray() }
                : fact;
            var text = prepared.Render();
            if (target.DirectContent.Any(t => Key(t) == Key(text)))
                duplicates.Add(new(fact, target, text));
            else byLocation[target.Location].Add(prepared);
        }
        return new(byLocation.ToDictionary(x => x.Key, x => (IReadOnlyList<GatheredFact>)x.Value, StringComparer.Ordinal), duplicates, unresolved);
    }

    /// <summary>Build writeback sections only after every fact has an explicit destination or exact-duplicate record.</summary>
    public static IReadOnlyList<GatherSection> BuildSections(GatherSubsectionRouting routing,
        IReadOnlyList<GatherSubsectionTarget> targets, GatherRun reviewedRun)
    {
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(reviewedRun);
        if (routing.Unresolved.Count > 0) throw new InvalidOperationException("SUBSECTION_ROUTING_UNRESOLVED:" + routing.Unresolved.Count);
        var locations = targets.Select(t => t.Location).ToHashSet(StringComparer.Ordinal);
        if (routing.ByLocation.Keys.Any(k => !locations.Contains(k))) throw new InvalidOperationException("SUBSECTION_TARGET_MISMATCH");
        return targets.Where(t => routing.ByLocation.TryGetValue(t.Location, out var facts) && facts.Count > 0)
            .Select(t =>
            {
                var facts = routing.ByLocation[t.Location];
                return new GatherSection(t.Location, t.Heading, t.Heading, reviewedRun with { Facts = facts },
                    GatherDocument.Conflicts(facts), t.NextHeadingLocation);
            }).ToArray();
    }
}
