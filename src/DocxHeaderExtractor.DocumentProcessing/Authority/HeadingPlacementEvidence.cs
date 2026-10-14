namespace DocxHeaderExtractor.DocumentProcessing.Authority;

/// <summary>
/// Measurements a heading authority hands to the shared placement pass (PDF lane, V6 promotion): the document's style table and the style of
/// each line, as JSON text keyed by line alias. They are facts about the document, never a label: the placement model still decides every
/// parent relation, and the resolver still only validates the relations and counts depth. Null evidence leaves the placement packet exactly as
/// it was before (DOCX).
/// </summary>
/// <param name="DocumentStylesJson">The style table of the whole document (JSON object).</param>
/// <param name="StyleJsonByAlias">The style of a line (JSON object), by the line's alias; a line without a measurable style has no entry.</param>
internal sealed record HeadingPlacementEvidence(string DocumentStylesJson, IReadOnlyDictionary<string, string> StyleJsonByAlias);
