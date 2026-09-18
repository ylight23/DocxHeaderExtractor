using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.DocumentProcessing.Projection;

/// <summary>One projected record: canonical fields, selected by the intent.</summary>
public sealed record ProjectedRecord(IReadOnlyDictionary<string, object?> Fields);

public sealed record ProjectionResult(
    ExtractionTask Task,
    OutputFormat OutputFormat,
    IReadOnlyList<string> Fields,
    IReadOnlyList<ProjectedRecord> Records);

/// <summary>
/// Reads a canonical document and answers the user's question about it.
/// <para>
/// It only ever selects and reshapes. It never re-derives meaning: no field here is recomputed from
/// text, and a field the canonical document does not carry is reported as unavailable rather than
/// guessed. That is what keeps one canonical truth behind every different question.
/// </para>
/// </summary>
public static class CanonicalProjector
{
    private static readonly string[] HeadingDefaults = ["text", "level", "parent", "source"];
    private static readonly string[] StructureDefaults = ["text", "level", "parent", "source", "semanticRole", "order"];

    /// <summary>Fields this projector can serve, so an unknown request fails with a list.</summary>
    public static IReadOnlyList<string> AvailableFields { get; } =
        ["text", "level", "parent", "source", "semanticRole", "order", "elementId"];

    public static ProjectionResult Project(ProjectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var intent = request.Intent;
        var fields = Resolve(intent);
        var unknown = fields.Where(field => !AvailableFields.Contains(field, StringComparer.Ordinal)).ToArray();
        if (unknown.Length > 0)
            throw new ArgumentException(
                $"The canonical document does not carry: {string.Join(", ", unknown)}. " +
                $"Available: {string.Join(", ", AvailableFields)}.", nameof(request));

        var elements = request.Document.Structure.OutlineElements.ToArray();
        var idToText = elements.ToDictionary(
            element => element.Id,
            element => element.ProjectionMetadata?.OriginalText ?? element.Text,
            StringComparer.Ordinal);

        var records = elements
            .Select((element, order) => new ProjectedRecord(
                fields.ToDictionary(field => field, field => Value(field, element, order, idToText), StringComparer.Ordinal)))
            .ToArray();

        return new ProjectionResult(intent.Task, intent.OutputFormat, fields, records);
    }

    private static IReadOnlyList<string> Resolve(ExtractionIntent intent)
    {
        if (intent.RequestedFields.Count > 0) return intent.RequestedFields;
        return intent.Task switch
        {
            ExtractionTask.Headings => HeadingDefaults,
            ExtractionTask.DocumentStructure => StructureDefaults,
            // A custom projection with no field list has not said what it wants. Defaulting to
            // everything would hide that, so it gets the structure set and the instruction travels
            // with the result for whoever interprets it.
            _ => StructureDefaults,
        };
    }

    private static object? Value(
        string field,
        ValidatedStructuralElement element,
        int order,
        IReadOnlyDictionary<string, string?> idToText) => field switch
    {
        "text" => element.ProjectionMetadata?.OriginalText ?? element.Text,
        "level" => element.ProjectionMetadata?.CompatibilityLevelIsSet == true
            ? element.ProjectionMetadata.CompatibilityLevel
            : element.Level,
        "parent" => element.ParentId is null ? null : idToText.GetValueOrDefault(element.ParentId),
        "source" => element.Sources.FirstOrDefault()?.StableId ?? element.Sources.FirstOrDefault()?.SourceId,
        "semanticRole" => element.Role.ToString(),
        "order" => order,
        "elementId" => element.Id,
        _ => null,
    };
}
