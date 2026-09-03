namespace DocxHeaderExtractor.Core.Llm;

public sealed record ChunkResult(
    IReadOnlyList<ModelHeading> Headings,
    string RawOutput,
    int RejectedIndexes,
    long ElapsedMs,
    IReadOnlySet<int> ExplicitNonHeadings,
    IReadOnlyDictionary<int, SemanticRole>? RejectedRoles = null);

public sealed record HierarchyItem(
    int Index,
    string Text,
    int? StyleLevel,
    int? OutlineLevel,
    int? HintLevel,
    string? Numbering);
