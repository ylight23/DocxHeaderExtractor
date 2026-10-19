using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.Tests;

/// <summary>The text of a request message's content: a plain string, or the first block of the cache-marked array production sends.</summary>
internal static class MessageText
{
    public static string? Text(this JsonElement content) =>
        content.ValueKind == JsonValueKind.String ? content.GetString() : content[0].GetProperty("text").GetString();

    public static string Text(this JsonNode content) =>
        content is JsonArray blocks ? blocks[0]!["text"]!.GetValue<string>() : content.GetValue<string>();
}
