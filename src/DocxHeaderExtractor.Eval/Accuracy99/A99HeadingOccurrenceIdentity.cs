namespace DocxHeaderExtractor.Eval.Accuracy99;

/// <summary>
/// Identifies one logical heading occurrence inside a parser-owned source occurrence.
/// The span is part of the identity because one source paragraph may contain multiple headings.
/// </summary>
public static class A99HeadingOccurrenceIdentity
{
    /// <summary>Physical source identity is scoped to a document; the source id alone is not global.</summary>
    public static string PhysicalSourceOccurrenceId(string documentId, string sourceId)
    {
        if (string.IsNullOrWhiteSpace(documentId)) throw new ArgumentException("Document scope is required.", nameof(documentId));
        if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("Source identity is required.", nameof(sourceId));
        return $"{documentId}/{sourceId}";
    }

    public static string Create(string sourceId, Accuracy99Span headingSpan) =>
        Create(sourceId, headingSpan.Start, headingSpan.End);

    public static string Create(string sourceId, A99ReviewSpan headingSpan) =>
        Create(sourceId, headingSpan.Start, headingSpan.End);

    public static string Create(string sourceId, int start, int end)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("Source identity is required.", nameof(sourceId));
        if (start < 0 || end < start) throw new ArgumentOutOfRangeException(nameof(end), "Heading span must be non-negative and ordered.");
        return $"{sourceId}@{start}:{end}";
    }
}
