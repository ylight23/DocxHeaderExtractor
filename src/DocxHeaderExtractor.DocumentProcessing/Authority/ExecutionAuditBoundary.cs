namespace DocxHeaderExtractor.DocumentProcessing.Authority;

/// <summary>
/// Creates the common, observation-only portion of a pipeline audit.
/// <para>
/// This boundary records stage inputs and decisions that have already been produced upstream. It
/// does not decide heading membership, semantic identity, hierarchy, or materialization, and it
/// has no provider dependency. Lane-specific diagnostics remain on the returned record and are
/// attached by the lane adapters.
/// </para>
/// </summary>
internal static class ExecutionAuditBoundary
{
    internal static PipelineExecutionAudit Create(
        string pipelineId,
        int sourceBlocksAvailable,
        int sourceBlocksSelected,
        int sourcePagesAvailable,
        int sourcePagesSelected,
        IReadOnlyList<SourceBlockAudit> sourceBlocks,
        IReadOnlyList<SourceBlockAudit> selectedSourceBlocks,
        IReadOnlyList<SourceBlockDecisionAudit> blockDecisions,
        IReadOnlyList<string> groundedBlockIds) =>
        new(
            pipelineId,
            sourceBlocksAvailable,
            sourceBlocksSelected,
            sourcePagesAvailable,
            sourcePagesSelected,
            sourceBlocks,
            selectedSourceBlocks,
            blockDecisions,
            groundedBlockIds)
        {
            PipelineId = pipelineId,
        };
}
