namespace DocxHeaderExtractor.Core.Architecture;

/// <summary>
/// Small, dependency-free guard vocabulary for the final architecture. Verification tests can use
/// these rules without loading a third-party architecture framework or executing extraction.
/// </summary>
public static class ArchitectureBoundaryGuards
{
    public static IReadOnlyList<ArchitectureBoundaryRule> Rules { get; } =
    [
        new("generic-structure-no-heading-record", "generic structural runtime", "HeadingRecord"),
        new("section-chunk-no-heading-record", "section/chunk projection", "HeadingRecord"),
        new("retrieval-no-heading-record", "retrieval/search projection", "HeadingRecord"),
        new("fact-runtime-no-heading-record", "fact runtime", "HeadingRecord"),
        new("product-fact-no-heading-record", "product fact runtime", "HeadingRecord"),
        new("schema-discovery-no-heading-record", "schema discovery", "HeadingRecord"),
        new("source-catalog-parser-owned", "source catalog reconstruction", "ValidatedStructure"),
        new("compatibility-heading-one-way", "generic structural authority", "HeadingRecord -> ValidatedStructure"),
        new("structural-proposal-validation", "structural authority", "StructuralProposalValidator"),
        new("relation-proposal-validation", "relation authority", "StructuralRelationProposalValidator"),
        new("fact-proposal-validation", "fact authority", "FactProposalValidator"),
        new("registered-schema-selection", "schema authority", "SchemaSelectionValidator"),
    ];

    public static void RequireProposalValidation(bool passed, string boundary)
    {
        if (!passed)
            throw new ArchitectureBoundaryViolationException($"{boundary}:proposal-validation-required");
    }

    public static void RequireSourceCatalogOwner(string owner)
    {
        if (!string.Equals(owner, "parser", StringComparison.Ordinal))
            throw new ArchitectureBoundaryViolationException("source-catalog-owner-must-be-parser");
    }

    public static void RejectDirectAuthorityMaterialization(string producer, string target)
    {
        throw new ArchitectureBoundaryViolationException(
            $"{producer}:direct-materialization-forbidden:{target}");
    }
}

public sealed record ArchitectureBoundaryRule(string Key, string Boundary, string ForbiddenDependency);

public sealed class ArchitectureBoundaryViolationException : InvalidOperationException
{
    public ArchitectureBoundaryViolationException(string message) : base(message)
    {
    }
}
