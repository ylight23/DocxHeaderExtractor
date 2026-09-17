using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>Production adapter owned by the v6 entry point. The caller supplies a client, but
/// does not invoke Qwen or parse proposals; inference and contract parsing stay behind the
/// orchestration boundary.</summary>
public sealed class OpenRouterCanonicalSemanticTextModel : ICanonicalSemanticTextModel
{
    private readonly OpenRouterCeilingReasoningModel _model;

    public OpenRouterCanonicalSemanticTextModel(OpenRouterCeilingReasoningModel model) =>
        _model = model ?? throw new ArgumentNullException(nameof(model));

    public async Task<CanonicalSemanticTextInferenceResult> InferAsync(
        CanonicalSemanticProductionInput input,
        SemanticContextPacket packedContext,
        string requestId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(packedContext);
        var aliases = SemanticSourceAliasCatalog.FromCatalog(input.SourceCatalog);
        var packet = CanonicalSemanticRequestMaterializer.BuildPacket(input, packedContext);
        var route = ReasoningRoute.ModelCapabilityCeiling.ToString();
        var result = await _model.CompleteRawStructuredSemanticAsync(
            input.DocumentId ?? requestId,
            route,
            requestId,
            packet,
            aliases.Sum(alias => alias.Text.Length),
            aliases.Count,
            aliases.Count,
            SemanticTextExactBindingContract.System,
            SemanticTextExactBindingContract.BuildUser(packet, route),
            SemanticTextExactBindingContract.Schema(),
            "semantic_text_exact_binding_v1",
            cancellationToken);
        using var rawDocument = JsonDocument.Parse(result.Content);
        RejectProviderCoordinates(rawDocument.RootElement);
        var response = SemanticTextExactBindingContract.Parse(result.Content);
        // The provider DTO is an infrastructure compatibility shape. Only these semantic fields
        // cross into Core; any positional data in a legacy response is intentionally discarded.
        var proposals = response.Headings.Select(heading => new CanonicalSemanticProposal(
            heading.Source,
            true,
            heading.Text,
            SemanticRole: heading.Role,
            Occurrence: heading.Occurrence,
            LeftExactContext: heading.LeftExactContext,
            RightExactContext: heading.RightExactContext)).ToArray();
        return new(proposals, new CanonicalSemanticInferenceTelemetry(
            result.Telemetry.ProviderRoute,
            result.Telemetry.FinishReason,
            result.Telemetry.ReportedInputTokens,
            result.Telemetry.ReportedReasoningTokens,
            result.Telemetry.ReportedOutputTokens));
    }

    private static void RejectProviderCoordinates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name is "start" or "end" or "offset" or "page" or "bbox" or "coordinates")
                    throw new FormatException($"provider-positional-field-forbidden:{property.Name}");
                RejectProviderCoordinates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                RejectProviderCoordinates(item);
        }
    }
}
