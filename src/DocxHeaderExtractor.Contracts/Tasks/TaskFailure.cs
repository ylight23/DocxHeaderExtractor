namespace DocxHeaderExtractor.Contracts.Tasks;

public sealed record TaskFailure(
    TaskFailureKind Kind,
    string Code,
    string Message,
    string? Stage = null);
