namespace DocxHeaderExtractor.Contracts.Tasks;

public interface IInputResourceResolver
{
    ValueTask<ResolvedInputResource> ResolveAsync(
        InputResource resource,
        CancellationToken cancellationToken = default);
}
