using DocxHeaderExtractor.Infrastructure.AI.QualifiedInference;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.V5;

/// <summary>Frozen request identity shared by the V5 qualification request builders.</summary>
public sealed record V5ComposedSemanticRequest(
    string ComposerVersion,
    string Prompt,
    string PromptHash,
    string SchemaHash,
    string RequestHash,
    int Utf8Bytes);

/// <summary>Harness-resolved coordinates for one source-backed endpoint.</summary>
public sealed record BoundClaimEndpoint(IReadOnlyList<BoundSourcePart> Parts)
{
    public string Identity => string.Join("|", Parts.Select(part =>
        $"{part.SourceId}:{part.Start}-{part.End}"));
}

/// <summary>System prompt of the V5 V2.1 request family; the production PDF route uses its own prompts.</summary>
public static class V5SystemPromptV2_1
{
    public const string Text = "You are a task-defined semantic reasoner. Respond with a single JSON object matching the declared schema exactly.";
}

/// <summary>One transport-level tool-call delta fragment, as surfaced to the forced-tool canaries.</summary>
public sealed record V5ToolCallDeltaFragment(int Index, string? Id, string? FunctionName, string? ArgumentsChunk);

/// <summary>Carries a composed V5 request over the production json_object carrier with the V2.1 system prompt.</summary>
public static class OpenRouterQwen37ComposedRequestCarrierV2_1
{
    public static V5ProviderRequestBodyV2_1 Build(
        V5ComposedSemanticRequest request, int maxCompletionTokens, V5ProviderEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(request);
        return OpenRouterQwen37JsonObjectCarrierV2_1.BuildFromRaw(V5SystemPromptV2_1.Text, request.Prompt, maxCompletionTokens, envelope);
    }
}
