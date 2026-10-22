using DocxHeaderExtractor.App;
using DocxHeaderExtractor.Contracts.Provider;
using DocxHeaderExtractor.HeadingAgent.Protocol;

namespace DocxHeaderExtractor.Tests;

public sealed class ModelProviderTests
{
    [Fact]
    public void The_openrouter_provider_routes_the_qualified_model_and_never_prints_its_key()
    {
        var route = new OpenRouterProvider("sk-test").CreateRoute();
        Assert.Equal((AgentProtocol.ChatModel, "sk-test"), (route.Model, route.ApiKey));
        Assert.DoesNotContain("sk-test", route.ToString(), StringComparison.Ordinal);
        Assert.Null(new OpenRouterProvider("  ").CreateRoute().ApiKey);
        Assert.False(new OpenRouterProvider(null).HasKey);
    }

    [Fact]
    public void A_provider_without_a_route_refuses_rather_than_falling_back()
    {
        var refused = Assert.Throws<InvalidOperationException>(() => ((IModelProvider)new NoRoute()).CreateRoute());
        Assert.Equal("MODEL_ROUTE_REQUIRED", refused.Message);
    }

    sealed class NoRoute : IModelProvider;
}
