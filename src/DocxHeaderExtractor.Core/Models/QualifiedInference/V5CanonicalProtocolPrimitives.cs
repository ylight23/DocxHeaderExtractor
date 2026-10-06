using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.V5;

/// <summary>Frozen request identity shared by qualification builders and the production carrier.</summary>
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

internal static class Hashing
{
    internal static string Sha256(string text) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

internal static class CanonicalJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };
}
