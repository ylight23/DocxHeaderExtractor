namespace DocxHeaderExtractor.Contracts.Runtime;

public sealed record RunStorageKey(string RunId, int SchemaVersion = 1)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(RunId))
            throw new ArgumentException("RunId không được rỗng.", nameof(RunId));
        if (SchemaVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(SchemaVersion));
    }
}
