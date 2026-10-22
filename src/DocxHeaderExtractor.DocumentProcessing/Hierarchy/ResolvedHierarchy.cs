namespace DocxHeaderExtractor.DocumentProcessing.Hierarchy;

/// <summary>Resolved parent and level of a validated heading: the product of hierarchy resolution, owned by materialization.</summary>
public sealed record ResolvedHierarchy(
    string SourceId,
    int Level,
    string? ParentId,
    string ParentResolution,
    string Decision)
{
    public string StructuralScope { get; init; } = "document_body";
}
