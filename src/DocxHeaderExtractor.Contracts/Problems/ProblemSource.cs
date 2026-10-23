using DocxHeaderExtractor.Contracts.Models;

namespace DocxHeaderExtractor.Contracts.Problems;

/// <summary>
/// The document a problem works on: who it is, and its source units in reading order, each with its text, its anchor in the file and its
/// span. Every format reads into this same shape, so a problem never opens the file itself and never needs to know its format.
/// </summary>
public sealed record ProblemSource(DocumentIdentity Identity, DocumentSourceCatalog Catalog);
