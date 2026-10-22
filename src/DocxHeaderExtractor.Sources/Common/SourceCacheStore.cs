using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;

namespace DocxHeaderExtractor.Sources.Common;

/// <summary>
/// The file store behind the source caches. An entry is <c>&lt;root&gt;/&lt;file sha16&gt;/&lt;key&gt;.json</c> with a <c>.sha256</c> sidecar holding
/// the hash of the entry's bytes. The key is made from the schema, the file's bytes, the path, this build of the code and any options the
/// result depends on, so a change to any of them is a miss. A miss, a hash mismatch or an unreadable entry is never an error: the caller
/// builds again.
/// </summary>
internal static class SourceCacheStore
{
    const string Sidecar = ".sha256";

    static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
        Converters = { new DocumentSourceCatalogJsonConverter() },
    };

    /// <summary>This build of the code: the module version id changes with every build of the assembly.</summary>
    static string CodeIdentity => typeof(SourceCacheStore).Assembly.ManifestModule.ModuleVersionId.ToString("N");

    public static string FileSha(string path) => Hash(File.ReadAllBytes(path));

    public static string Key(string schema, string fileSha, string fullPath, string options) =>
        Hash(Encoding.UTF8.GetBytes(string.Join('|', schema, fileSha, fullPath, CodeIdentity, options)))[..24];

    /// <summary>The entry file for a key, under the file's own folder.</summary>
    public static string EntryPath(string root, string fileSha, string key, string prefix, string extension = ".json") =>
        Path.Combine(root, fileSha[..16], prefix + key + extension);

    /// <summary>The stored payload, or null when there is no valid entry for this schema and key.</summary>
    public static T? TryLoad<T>(string file, string schema, string key) where T : class
    {
        if (!File.Exists(file) || !File.Exists(file + Sidecar)) return null;
        try
        {
            var bytes = File.ReadAllBytes(file);
            if (Hash(bytes) != File.ReadAllText(file + Sidecar, Encoding.UTF8).Trim()) return null;
            var envelope = JsonSerializer.Deserialize<Envelope<T>>(bytes, Json);
            if (envelope is null || envelope.Schema != schema || envelope.Key != key || envelope.Payload is null) return null;
            return envelope.Payload;
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>Stores the payload atomically: the entry and its sidecar are written under temporary names, then renamed into place.</summary>
    public static void Store<T>(string file, string schema, string key, T payload) where T : class
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope<T>(schema, key, payload), Json);
        WriteAtomic(file, bytes);
    }

    /// <summary>The stored payload from a MessagePack entry, or null when there is no valid entry for this schema and key.</summary>
    public static T? TryLoadBinary<T>(string file, string schema, string key) where T : class
    {
        if (!File.Exists(file) || !File.Exists(file + Sidecar)) return null;
        try
        {
            var bytes = File.ReadAllBytes(file);
            if (Hash(bytes) != File.ReadAllText(file + Sidecar, Encoding.UTF8).Trim()) return null;
            var envelope = MessagePackSerializer.Deserialize<BinaryEnvelope<T>>(bytes, Binary);
            if (envelope is null || envelope.Schema != schema || envelope.Key != key || envelope.Payload is null) return null;
            return envelope.Payload;
        }
        catch (MessagePackSerializationException) { return null; }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    /// <summary>Stores the payload as MessagePack, under the same atomic write and sidecar as the JSON entries.</summary>
    public static void StoreBinary<T>(string file, string schema, string key, T payload) where T : class
    {
        var bytes = MessagePackSerializer.Serialize(new BinaryEnvelope<T>(schema, key, payload), Binary);
        WriteAtomic(file, bytes);
    }

    static void WriteAtomic(string file, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var id = Guid.NewGuid().ToString("N");
        var temp = file + "." + id + ".tmp";
        var tempSidecar = file + Sidecar + "." + id + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.WriteAllText(tempSidecar, Hash(bytes), new UTF8Encoding(false));
        File.Move(temp, file, overwrite: true);
        File.Move(tempSidecar, file + Sidecar, overwrite: true);
    }

    /// <summary>The JSON text of a value under the store's options, for keys built from options.</summary>
    public static string Fingerprint<T>(T value) => JsonSerializer.Serialize(value, Json);

    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    sealed record Envelope<T>(string Schema, string Key, T Payload);

    /// <summary>The MessagePack options: the standard resolver, plus the formatter for the <c>object</c>-typed evidence facts.</summary>
    static readonly MessagePackSerializerOptions Binary = MessagePackSerializerOptions.Standard.WithResolver(CompositeResolver.Create(
        new IMessagePackFormatter[] { new ObjectJsonFormatter() },
        new IFormatterResolver[] { StandardResolver.Instance }));
}

/// <summary>The entry written to a MessagePack cache file: the schema and key it was built under, and the payload.</summary>
[MessagePackObject(AllowPrivate = true)]
internal sealed record BinaryEnvelope<T>([property: Key(0)] string Schema, [property: Key(1)] string Key, [property: Key(2)] T Payload);
