using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Tests;

public sealed class OpenRouterTransportFacadeTests
{
    [Fact]
    public void Production_facade_exposes_no_raw_observation_or_forced_tool_transport()
    {
        var publicMethods = typeof(OpenRouterHeaderExtractor).GetMethods()
            .Where(method => method.DeclaringType == typeof(OpenRouterHeaderExtractor))
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("ExecuteObservedAsync", publicMethods);
        Assert.DoesNotContain("ExecuteObservedUnconstrainedAsync", publicMethods);
        Assert.DoesNotContain("ExecuteToolCallAsync", publicMethods);
    }

    [Fact]
    public void Qualification_facade_owns_raw_observation_and_forced_tool_transport()
    {
        var publicMethods = typeof(OpenRouterQualificationTransport).GetMethods()
            .Where(method => method.DeclaringType == typeof(OpenRouterQualificationTransport))
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains(nameof(OpenRouterQualificationTransport.ExecuteObservedAsync), publicMethods);
        Assert.Contains(nameof(OpenRouterQualificationTransport.ExecuteObservedUnconstrainedAsync), publicMethods);
        Assert.Contains(nameof(OpenRouterQualificationTransport.ExecuteToolCallAsync), publicMethods);
    }
}
