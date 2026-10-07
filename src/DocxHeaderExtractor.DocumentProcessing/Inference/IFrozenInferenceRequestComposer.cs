namespace DocxHeaderExtractor.DocumentProcessing.Inference;

/// <summary>
/// Turns immutable semantic prompt bytes into the exact provider request body a frozen transport sends. The semantic layer
/// owns the prompt and user message; the implementation, which lives with the provider, owns every provider and model
/// choice. Document processing therefore never names a provider, a model or a wire envelope.
/// </summary>
public interface IFrozenInferenceRequestComposer
{
    byte[] Build(string systemPrompt, string userMessage, int maxTokens);
}
