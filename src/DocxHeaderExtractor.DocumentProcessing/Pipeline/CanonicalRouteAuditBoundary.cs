using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Creates the common, observation-only portion of a route audit.
/// <para>
/// This boundary records stage inputs and decisions that have already been produced upstream. It
/// does not decide heading membership, semantic identity, hierarchy, or materialization, and it
/// has no provider dependency. Lane-specific diagnostics remain on the returned record and are
/// attached by the lane adapters.
/// </para>
/// </summary>
internal static class CanonicalRouteAuditBoundary
{
    internal static RouteExecutionAudit Create(
        string route,
        int sourceBlocksAvailable,
        int sourceBlocksSelected,
        int sourcePagesAvailable,
        int sourcePagesSelected,
        IReadOnlyList<RouteBlockAudit> sourceBlocks,
        IReadOnlyList<RouteBlockAudit> selectedSourceBlocks,
        IReadOnlyList<RouteBlockAudit> budgetExcluded,
        IReadOnlyList<RouteBlockDecisionAudit> blockDecisions,
        IReadOnlyList<string> groundedBlockIds,
        IReadOnlyList<RouteBlockRejectionAudit> groundingRejections,
        IReadOnlyList<string> alignedBlockIds) =>
        new(
            route,
            sourceBlocksAvailable,
            sourceBlocksSelected,
            sourcePagesAvailable,
            sourcePagesSelected,
            sourceBlocks,
            selectedSourceBlocks,
            budgetExcluded,
            blockDecisions,
            groundedBlockIds,
            groundingRejections,
            alignedBlockIds)
        {
            Route = route,
        };
}
