namespace DocxHeaderExtractor.Contracts.Runtime;

public interface ITaskRunStore
{
    ValueTask SaveAsync(PersistedTaskRun run, CancellationToken ct = default);
    ValueTask<PersistedTaskRun?> LoadAsync(RunStorageKey key, CancellationToken ct = default);
}
