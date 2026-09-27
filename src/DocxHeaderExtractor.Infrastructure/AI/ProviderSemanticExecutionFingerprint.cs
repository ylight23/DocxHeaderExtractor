using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Infrastructure.AI;

/// <summary>
/// A provider-facing execution fingerprint. It describes only fields that can change model
/// behaviour; credentials, request identifiers, timestamps and telemetry are deliberately absent.
/// The fingerprint is transport evidence, not a semantic prompt or a provider authorization.
/// </summary>
public sealed record ProviderSemanticExecutionConfig(
    string Provider,
    string Endpoint,
    string Model,
    string Temperature,
    string? TopP,
    int MaxTokens,
    string? ReasoningField,
    string? ReasoningValue,
    string ResponseFormat,
    string? ResponseSchemaSha256,
    string? ProviderRouting,
    string? ToolConfiguration)
{
    public string CanonicalJson => ProviderSemanticExecutionFingerprint.CanonicalJson(this);

    public string Sha256 => ProviderSemanticExecutionFingerprint.Compute(this);
}

public static class ProviderSemanticExecutionFingerprint
{
    public static string Compute(ProviderSemanticExecutionConfig config) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson(config))));

    /// <summary>Builds the exact OpenRouter boundary envelope identity without retaining its API key.</summary>
    public static ProviderSemanticExecutionConfig ForOpenRouterBoundary(
        RemoteInferenceOptions options,
        int maxTokens,
        string? responseSchemaSha256 = null)
    {
        options.Validate(requireModel: true);
        return new ProviderSemanticExecutionConfig(
            Provider: "OpenRouter",
            Endpoint: options.Endpoint.GetLeftPart(UriPartial.Path),
            Model: options.Model,
            Temperature: "0",
            TopP: null,
            MaxTokens: maxTokens,
            // OpenRouterHeaderExtractor writes this field unconditionally. An optional
            // RemoteInferenceOptions override is not evidence of the wire payload and is
            // therefore intentionally not substituted here.
            ReasoningField: "reasoning.effort",
            ReasoningValue: options.OpenRouterReasoningEffort,
            ResponseFormat: "json_object",
            ResponseSchemaSha256: responseSchemaSha256,
            ProviderRouting: string.Join(';',
                $"zdr={options.RequireZeroDataRetention.ToString().ToLowerInvariant()}",
                "data_collection=deny",
                "require_parameters=true",
                "allow_fallbacks=true",
                $"route={options.OpenRouterProviderRoute ?? "AUTO"}"),
            ToolConfiguration: "NONE");
    }

    internal static string CanonicalJson(ProviderSemanticExecutionConfig config)
    {
        var fields = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["endpoint"] = config.Endpoint,
            ["maxTokens"] = config.MaxTokens,
            ["model"] = config.Model,
            ["provider"] = config.Provider,
            ["providerRouting"] = config.ProviderRouting,
            ["reasoningField"] = config.ReasoningField,
            ["reasoningValue"] = config.ReasoningValue,
            ["responseFormat"] = config.ResponseFormat,
            ["responseSchemaSha256"] = config.ResponseSchemaSha256,
            ["temperature"] = config.Temperature,
            ["toolConfiguration"] = config.ToolConfiguration,
            ["topP"] = config.TopP,
        };
        return JsonSerializer.Serialize(fields, new JsonSerializerOptions { WriteIndented = false });
    }
}
