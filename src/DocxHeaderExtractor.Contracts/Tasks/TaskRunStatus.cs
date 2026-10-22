namespace DocxHeaderExtractor.Contracts.Tasks;

public enum TaskRunStatus
{
    Started,
    Completed,
    NeedsHumanReview,
    Blocked,
    Failed,
    Cancelled,
}
