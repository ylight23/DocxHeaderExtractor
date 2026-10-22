using System.Text.Encodings.Web;
using System.Text.Json;
using MessagePack;
using MessagePack.Formatters;

namespace DocxHeaderExtractor.DocumentProcessing.Source.Common;

/// <summary>
/// Writes an <c>object</c>-typed evidence value as its JSON text inside a MessagePack entry, and reads it back as a <see cref="JsonElement"/>.
/// The evidence facts are anonymous objects whose shape is chosen at run time, so MessagePack has no formatter for them; their JSON text is
/// the same text the JSON cache wrote, and <c>object</c> reads as a <see cref="JsonElement"/> either way.
/// </summary>
internal sealed class ObjectJsonFormatter : IMessagePackFormatter<object?>
{
    static readonly JsonSerializerOptions Reflection = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public void Serialize(ref MessagePackWriter writer, object? value, MessagePackSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNil();
            return;
        }
        writer.Write(JsonSerializer.Serialize(value, value.GetType(), Reflection));
    }

    public object? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil()) return null;
        return JsonSerializer.Deserialize<JsonElement>(reader.ReadString()!);
    }
}
