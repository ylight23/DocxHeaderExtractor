using System.Collections.Frozen;

namespace DocxHeaderExtractor.DocumentProcessing.Output.Outline;

/// <summary>Projection-only source identity, scoped to one structural element/source pair.</summary>
public readonly record struct HeadingProjectionSourceKey(string ElementId, string SourceId);

/// <summary>
/// Runtime sidecar, never structural authority or model evidence. Carries the producer's output
/// compatibility facts without attaching them to the graph. Inputs are snapshotted by identity.
/// </summary>
public sealed class ProjectionContext
{
    private readonly FrozenDictionary<string, ProjectionMetadata> _elements;
    private readonly FrozenDictionary<HeadingProjectionSourceKey, string> _sources;

    public static ProjectionContext Empty { get; } = new();

    public ProjectionContext(
        IReadOnlyDictionary<string, ProjectionMetadata>? elements = null,
        IReadOnlyDictionary<HeadingProjectionSourceKey, string>? sources = null)
    {
        _elements = (elements ?? new Dictionary<string, ProjectionMetadata>())
            .ToFrozenDictionary(StringComparer.Ordinal);
        _sources = (sources ?? new Dictionary<HeadingProjectionSourceKey, string>()).ToFrozenDictionary();
    }

    public ProjectionMetadata? ForElement(string elementId) => _elements.GetValueOrDefault(elementId);

    public string? StableIdFor(string elementId, string sourceId) =>
        _sources.GetValueOrDefault(new HeadingProjectionSourceKey(elementId, sourceId));

    public ProjectionContext Retain(IReadOnlySet<string> elementIds) => new(
        _elements.Where(pair => elementIds.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value),
        _sources.Where(pair => elementIds.Contains(pair.Key.ElementId)).ToDictionary(pair => pair.Key, pair => pair.Value));
}
