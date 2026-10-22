namespace DocxHeaderExtractor.Contracts.Runtime;

public enum PersistedRunLifecycle
{
    Draft,
    Running,
    Completed,
    NeedsHumanReview,
    Blocked,
    Failed,
    Cancelled,
    Frozen,
}
