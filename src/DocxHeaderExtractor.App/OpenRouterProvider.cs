using DocxHeaderExtractor.Contracts.Provider;
using DocxHeaderExtractor.Agents;

namespace DocxHeaderExtractor.App;

/// <summary>
/// The production model provider: OpenRouter, serving the model the agents were qualified on (<see cref="OpenRouterWire.ChatModel"/> on
/// Alibaba). The agents send every request themselves and check that the reply came from that model and route; this only hands them the key.
/// </summary>
public sealed class OpenRouterProvider(string? apiKey) : IModelProvider
{
    /// <summary>The environment variable the key is read from. The key is never printed.</summary>
    public const string ApiKeyVariable = "OPENROUTER_API_KEY";

    /// <summary>The model every agent was qualified on; the agents refuse a reply from any other.</summary>
    public const string Model = OpenRouterWire.ChatModel;

    public static OpenRouterProvider FromEnvironment() => new(Environment.GetEnvironmentVariable(ApiKeyVariable));

    /// <summary>Whether a key is configured, so a host can say so before a run starts.</summary>
    public bool HasKey => !string.IsNullOrWhiteSpace(apiKey);

    public ModelRoute CreateRoute() => new(Model, HasKey ? apiKey : null);
}
