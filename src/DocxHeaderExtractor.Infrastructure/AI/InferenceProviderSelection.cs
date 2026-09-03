namespace DocxHeaderExtractor.Infrastructure.AI;

/// <summary>
/// Provider choice and runtime configuration owned by the composition root. The document
/// processing layer consumes only the classifier contract and never needs to know these values.
/// </summary>
public enum InferenceBackend
{
    Local,
    OpenRouter,
    LmStudio,
    Sglang,
}

public sealed class InferenceProviderSelection
{
    public InferenceBackend Backend { get; set; }
    public LocalModelOptions LocalModel { get; set; } = new();
    public RemoteInferenceOptions Remote { get; set; } = RemoteInferenceOptions.FromEnvironment();

    public bool SendsDataExternally => Backend is InferenceBackend.OpenRouter or InferenceBackend.Sglang;

    public static InferenceProviderSelection LocalDefault() => new();
}
