namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Runaway guard for one document's semantic lane. It is not a work budget: every provider call is
/// already bounded by its own transport deadline and bounded retries. The former 5-minute default
/// cut real documents short - SRC-095's 24 leaves took about 327 s of provider time in the production
/// re-baseline, so the default lane would have thrown before finishing - and the per-request/batch
/// timeouts that sat beside it were never read.
/// </summary>
public sealed record SemanticLaneOptions(TimeSpan LaneDeadline)
{
    public static readonly SemanticLaneOptions Default = new(TimeSpan.FromMinutes(60));
}
