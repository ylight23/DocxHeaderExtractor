namespace DocxHeaderExtractor.Contracts.Tasks;

public enum TaskFailureKind
{
    None,
    Validation,
    PolicyDenied,
    Capability,
    Provider,
    Timeout,
    Cancelled,
    Unknown,
}
