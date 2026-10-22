namespace DocxHeaderExtractor.DocumentProcessing.Structure;

/// <summary>Which occurrences of a repeated heading reach the canonical structure.</summary>
internal enum PrimaryOccurrences
{
    /// <summary>Every validated source occurrence; the authority defines no semantic node identity.</summary>
    EverySourceOccurrence,

    /// <summary>Only the first occurrence of each semantic node the authority declared.</summary>
    FirstOfSemanticNode,
}
