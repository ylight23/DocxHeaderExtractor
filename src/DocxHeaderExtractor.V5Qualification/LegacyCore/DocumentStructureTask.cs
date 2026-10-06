using DocxHeaderExtractor.Core.V5;

namespace DocxHeaderExtractor.DocumentProcessing.Projection;

/// <summary>Task-owned vocabulary for a document structure consumer; generic V5 knows none of these names.</summary>
public static class DocumentStructureTaskContract
{
    public static DocumentTaskContract Create() => new(
        V5Protocol.TaskContractVersion,
        "document-structure",
        "Resolve document identity, structural regions and navigation references from source-backed evidence.",
        [
            new SemanticPredicateDefinition("DOCUMENT_IDENTITY", "A source-backed document identity observation."),
            new SemanticPredicateDefinition("STRUCTURAL_REGION", "A source-backed structural region observation."),
            new SemanticPredicateDefinition("NAVIGATION_REPRESENTATION", "A source-backed navigation representation."),
        ],
        [
            new SemanticRelationDefinition("PARENT_OF", "A task-defined structural parent relation.", StructuralParent: true),
            new SemanticRelationDefinition("REFERENCES", "A task-defined navigation reference."),
            new SemanticRelationDefinition("SAME_ENTITY", "A task-defined identity relation.", IdentityMerge: true),
            new SemanticRelationDefinition("CONTINUES", "A task-defined continuation relation."),
        ],
        [
            new ProjectionRequest("document-identity", "Document identity projection.", Required: true),
            new ProjectionRequest("outline", "Structural region projection.", Required: true),
            new ProjectionRequest("navigation", "Navigation relation projection.", Required: true),
        ],
        new EvidencePolicy([EvidenceModality.TEXT, EvidenceModality.LAYOUT, EvidenceModality.VISUAL],
            [EvidenceNeed.GLOBAL_TARGET, EvidenceNeed.MORE_CONTEXT, EvidenceNeed.STRUCTURAL_CONTEXT, EvidenceNeed.LAYOUT_EVIDENCE]),
        "retain-open",
        new ExecutionBudget(MaxSemanticModelCalls: 2, MaxRetrievalRounds: 2));
}

public sealed class DocumentIdentityProjection : IKnowledgeProjection
{
    public string Name => "document-identity";
    public ProjectionResult Project(DocumentKnowledgeState state, DocumentTaskContract contract)
    {
        var claims = state.ResolvedClaims.Where(item => item.Predicate == "DOCUMENT_IDENTITY").ToArray();
        return new ProjectionResult(Name, new { identities = claims.Select(item => new { item.ClaimId, item.Value }).ToArray() },
            claims.Select(item => item.ClaimId).ToArray(), EvidenceIds(state, claims));
    }

    private static string[] EvidenceIds(DocumentKnowledgeState state, IEnumerable<BoundSemanticClaim> claims) =>
        state.EvidenceGraph.Nodes.Where(node => claims.Any(claim => claim.Subject.Parts.Any(part => part.Alias == node.SourceAlias)))
            .Select(item => item.EvidenceId).Distinct(StringComparer.Ordinal).ToArray();
}

public sealed class OutlineProjection : IKnowledgeProjection
{
    public string Name => "outline";
    public ProjectionResult Project(DocumentKnowledgeState state, DocumentTaskContract contract)
    {
        var claims = state.ResolvedClaims.Where(item => item.Predicate is "STRUCTURAL_REGION" or "PARENT_OF").ToArray();
        return new ProjectionResult(Name, new { structuralClaims = claims.Select(item => item.ClaimId).ToArray() },
            claims.Select(item => item.ClaimId).ToArray(), EvidenceIds(state, claims));
    }

    private static string[] EvidenceIds(DocumentKnowledgeState state, IEnumerable<BoundSemanticClaim> claims) =>
        state.EvidenceGraph.Nodes.Where(node => claims.Any(claim => claim.Subject.Parts.Any(part => part.Alias == node.SourceAlias)))
            .Select(item => item.EvidenceId).Distinct(StringComparer.Ordinal).ToArray();
}

public sealed class NavigationProjection : IKnowledgeProjection
{
    public string Name => "navigation";
    public ProjectionResult Project(DocumentKnowledgeState state, DocumentTaskContract contract)
    {
        var claims = state.ResolvedClaims.Where(item => item.Predicate is "NAVIGATION_REPRESENTATION" or "REFERENCES" or "SAME_ENTITY" or "CONTINUES").ToArray();
        return new ProjectionResult(Name, new { navigationClaims = claims.Select(item => item.ClaimId).ToArray() },
            claims.Select(item => item.ClaimId).ToArray(), EvidenceIds(state, claims));
    }

    private static string[] EvidenceIds(DocumentKnowledgeState state, IEnumerable<BoundSemanticClaim> claims) =>
        state.EvidenceGraph.Nodes.Where(node => claims.Any(claim => claim.Subject.Parts.Any(part => part.Alias == node.SourceAlias)))
            .Select(item => item.EvidenceId).Distinct(StringComparer.Ordinal).ToArray();
}
