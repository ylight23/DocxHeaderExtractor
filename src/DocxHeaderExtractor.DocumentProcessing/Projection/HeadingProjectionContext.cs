using System.Collections.Frozen;

namespace DocxHeaderExtractor.DocumentProcessing.Projection;

/// <summary>Projection-only source identity, scoped to one structural element/source pair.</summary>
public readonly record struct HeadingProjectionSourceKey(string ElementId, string SourceId);

/// <summary>
/// Runtime sidecar, never structural authority or model evidence. Carries the producer's output
/// compatibility facts without attaching them to the graph. Inputs are snapshotted by identity.
/// </summary>
public sealed class HeadingProjectionContext
{
    private readonly FrozenDictionary<string, HeadingProjectionMetadata> _elements;
    private readonly FrozenDictionary<HeadingProjectionSourceKey, string> _sources;

    public static HeadingProjectionContext Empty { get; } = new();

    public HeadingProjectionContext(
        IReadOnlyDictionary<string, HeadingProjectionMetadata>? elements = null,
        IReadOnlyDictionary<HeadingProjectionSourceKey, string>? sources = null)
    {
        _elements = (elements ?? new Dictionary<string, HeadingProjectionMetadata>())
            .ToFrozenDictionary(StringComparer.Ordinal);
        _sources = (sources ?? new Dictionary<HeadingProjectionSourceKey, string>()).ToFrozenDictionary();
    }

    public HeadingProjectionMetadata? ForElement(string elementId) => _elements.GetValueOrDefault(elementId);

    public string? StableIdFor(string elementId, string sourceId) =>
        _sources.GetValueOrDefault(new HeadingProjectionSourceKey(elementId, sourceId));

    public HeadingProjectionContext Retain(IReadOnlySet<string> elementIds) => new(
        _elements.Where(pair => elementIds.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value),
        _sources.Where(pair => elementIds.Contains(pair.Key.ElementId)).ToDictionary(pair => pair.Key, pair => pair.Value));
}
