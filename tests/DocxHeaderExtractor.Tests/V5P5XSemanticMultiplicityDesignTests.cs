using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Projection;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// P5X is a design authority audit, not a protocol migration. It freezes what the current task can
/// identify, what its projections consume, and which proposed two-phase facts remain unproven.
/// </summary>
public sealed class V5P5XSemanticMultiplicityDesignTests
{
    private const string Root = "artifacts/v5-p5x-semantic-multiplicity";

    [Fact]
    public void Freeze_semantic_identity_and_relation_lane_design_authority_without_provider()
    {
        var contract = DocumentStructureTaskContract.Create();
        var subject = new BoundClaimEndpoint([new BoundSourcePart("O0", "src", 0, 0, 7, "Heading", SemanticSourceLocality.SameSegment)]);
        var target = new BoundClaimEndpoint([new BoundSourcePart("O1", "src", 1, 0, 6, "Target", SemanticSourceLocality.SameSegment)]);
        var exactA = new BoundSemanticClaim("a", subject, "CONTINUES", null, target, ClaimResolutionState.RESOLVED, []);
        var exactB = exactA with { ClaimId = "b" };
        var differentTarget = exactA with { ClaimId = "c", Object = new BoundClaimEndpoint([new BoundSourcePart("O2", "src", 2, 0, 5, "Other", SemanticSourceLocality.SameSegment)]) };
        var differentValue = new BoundSemanticClaim("d", subject, "STRUCTURAL_REGION", "heading", null, ClaimResolutionState.RESOLVED, []);
        var differentUnaryValue = differentValue with { ClaimId = "e", Value = "toc" };
        Assert.Equal(exactA.Identity, exactB.Identity);
        Assert.NotEqual(exactA.Identity, differentTarget.Identity);
        Assert.NotEqual(differentValue.Identity, differentUnaryValue.Identity);

        var projections = new IKnowledgeProjection[] { new DocumentIdentityProjection(), new OutlineProjection(), new NavigationProjection() };
        var projectionInputs = projections.ToDictionary(projection => projection.Name, projection => projection.Name switch
        {
            "document-identity" => new[] { "DOCUMENT_IDENTITY" },
            "outline" => new[] { "STRUCTURAL_REGION", "PARENT_OF" },
            "navigation" => new[] { "NAVIGATION_REPRESENTATION", "REFERENCES", "SAME_ENTITY", "CONTINUES" },
            _ => throw new InvalidOperationException("unexpected-projection"),
        }, StringComparer.Ordinal);

        FreezeArtifact.AssertJson(Root, "semantic-multiplicity-design.v1.json", new
        {
            schemaVersion = "v5-p5x-semantic-multiplicity-design-v1",
            providerCalls = 0, goldRead = false, goldMutation = "NONE",
            canonicalSemanticIdentity = new
            {
                formula = "subject.Identity | predicate | (object.Identity ?? value ?? empty)",
                exactDuplicate = "REDUNDANT_BY_IDENTITY",
                sameSubjectPredicateDifferentTarget = "DISTINCT_SEMANTIC_FACT",
                sameSubjectPredicateDifferentUnaryValue = "DISTINCT_SEMANTIC_FACT",
                runtimeEnforcement = "NOT_ENFORCED_AS_A_DEDUPLICATION_RULE: ClaimId retains proposal ordinal; identity is available on BoundSemanticClaim",
            },
            currentTaskMultiplicity = new
            {
                unary = contract.Predicates.Select(item => new { item.Name, multiplicity = "NOT_DEFINED" }).OrderBy(item => item.Name).ToArray(),
                relations = contract.Relations.Select(item => new
                {
                    item.Name,
                    multiplicity = "NOT_DEFINED",
                    graphConstraint = item.StructuralParent ? "NO_SELF_PARENT_AND_NO_CYCLE_AFTER_BINDING" : item.IdentityMerge ? "IDENTITY_MERGE_MARKER" : "NONE",
                }).OrderBy(item => item.Name).ToArray(),
                conclusion = "Exact duplicate identity is a safe normalization candidate; caps, outgoing fan-out and target uniqueness require task semantics and are not inferred here.",
            },
            projectionDependency = projectionInputs,
            phaseDesign = new
            {
                phase1 = new { name = "occurrence-classification", permittedPredicates = contract.Predicates.Select(item => item.Name).OrderBy(item => item).ToArray(),
                    requiredBy = new[] { "document-identity", "outline", "navigation" },
                    status = "DESIGN_CANDIDATE_NOT_IMPLEMENTED" },
                phase2 = new { name = "relation-resolution", permittedRelations = contract.Relations.Select(item => item.Name).OrderBy(item => item).ToArray(),
                    requiredBy = new[] { "outline:PARENT_OF", "navigation:REFERENCES,SAME_ENTITY,CONTINUES" },
                    candidateUniverse = "Only phase-1 semantic candidates, subject to a future explicit rule for whether relation targets may include non-candidates.",
                    status = "DESIGN_CANDIDATE_NOT_IMPLEMENTED" },
                currentRuntimeCompatibility = "NO: current required outline and navigation projections consume relations in the same final knowledge state; separating calls requires an orchestrator/projection contract change and qualification.",
            },
            finiteBound = new
            {
                exclusiveOneOfThreeOccurrenceKinds = "HYPOTHETICAL: <= ownedCount decisions/claims, only after the task declares the three predicates mutually exclusive.",
                independentUnaryFacets = "HYPOTHETICAL: <= ownedCount * 3, only after the task declares at-most-one fact per predicate per canonical subject.",
                relationLane = "NO_BOUND_DERIVED: requires relation-specific semantic multiplicity and target-universe definitions.",
                gate = "P5U_RESPONSE_VOLUME_GATE_REMAINS_BLOCKED",
            },
            goldBoundary = "The current canonical Gold evaluates occurrence predicates only. No relation/hierarchy Gold axis exists for SRC-089/SRC-095, so phase-2 quality cannot be scored from that Gold.",
        });
    }
}
