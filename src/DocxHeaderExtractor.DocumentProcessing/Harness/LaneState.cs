namespace DocxHeaderExtractor.DocumentProcessing.Harness;

/// <summary>The state of one lane execution; <see cref="Active"/> is the only non-terminal one.</summary>
internal enum LaneState
{
    Active = 0,
    Succeeded = 1,
    Failed = 2,
    TimedOut = 3,
    Cancelled = 4,
}
