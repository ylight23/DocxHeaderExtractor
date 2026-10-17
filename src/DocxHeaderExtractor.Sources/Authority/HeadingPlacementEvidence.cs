namespace DocxHeaderExtractor.DocumentProcessing.Authority;

/// <summary>
/// Source facts for shared placement. The selector routes provide source context and upstream states
/// as well as style measurements. The model decides ancestry; the harness validates evidence and graph shape.
/// A caller without Source retains the legacy narrow placement contract.
/// </summary>
/// <param name="DocumentStylesJson">The style table of the whole document (JSON object).</param>
/// <param name="StyleJsonByAlias">The style of a line (JSON object), by the line's alias; a line without a measurable style has no entry.</param>
/// <param name="MeasurementsNote">What the style objects hold, when the default (PDF) sentence does not describe them.</param>
internal sealed record HeadingPlacementEvidence(string DocumentStylesJson, IReadOnlyDictionary<string, string> StyleJsonByAlias, string? MeasurementsNote = null)
{
    public HeadingPlacementSource? Source { get; init; }
}

/// <summary>Source facts, including lines withheld upstream. No line is classified as an ancestor by C#.</summary>
internal sealed record HeadingPlacementSource(IReadOnlyList<DocxHeaderExtractor.Core.Models.SemanticSourceAtom> Lines,
    IReadOnlyDictionary<string, DocxHeaderExtractor.DocumentProcessing.Routing.HeadingAliasState> AliasStates, string SourceSha256 = "")
{
    public IReadOnlyDictionary<string, object?> LocationByAlias { get; init; } = new Dictionary<string, object?>();
}
