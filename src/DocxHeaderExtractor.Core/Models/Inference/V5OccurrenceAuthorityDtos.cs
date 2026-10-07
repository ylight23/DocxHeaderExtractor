namespace DocxHeaderExtractor.Core.Models;

/// <summary>Harness-issued source occurrence id used by the function-membership protocol.</summary>
public sealed record V5IssuedOccurrenceV1(string Id, SemanticSourceAtom Atom);

/// <summary>Read-only correspondence evidence; never selectable output.</summary>
public sealed record V5ReadOnlyCorrespondenceV1(int TargetPage, string TargetText);
