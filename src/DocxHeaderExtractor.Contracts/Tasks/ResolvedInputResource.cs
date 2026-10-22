namespace DocxHeaderExtractor.Contracts.Tasks;

public sealed record ResolvedInputResource(
    InputResource Resource,
    Stream Content,
    bool LeaveOpen);
