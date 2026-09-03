namespace DocxHeaderExtractor.AgentHarness;

/// <summary>
/// Framework boundary for Microsoft Agent Framework integration. The adapter deliberately has no
/// framework package dependency: a host can wrap the framework's session/turn callback here while
/// all intent validation, capability resolution, policy, source validation, and projection remain
/// owned by <see cref="DocumentAgentHarness"/>.
/// </summary>
public interface IMicrosoftAgentFrameworkAdapter
{
    Task<DocumentAgentRunResult> RunAsync(
        DocumentAgentRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class MicrosoftAgentFrameworkAdapter : IMicrosoftAgentFrameworkAdapter
{
    private readonly DocumentAgentHarness _harness;

    public MicrosoftAgentFrameworkAdapter(DocumentAgentHarness harness)
    {
        _harness = harness ?? throw new ArgumentNullException(nameof(harness));
    }

    public Task<DocumentAgentRunResult> RunAsync(
        DocumentAgentRequest request,
        CancellationToken cancellationToken = default) =>
        _harness.RunAsync(request, cancellationToken);
}
