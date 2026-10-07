using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// One reader for H2-C provider captures, whether the raw archive is on this machine or only its sanitized ledger is.
/// <para>
/// Raw per-call captures (SSE streams, response bodies) are a local forensic archive and are not committed. What a downstream
/// audit needs from each is small: the response content (an occurrence-handle decision vector), its hashes, usage and finish
/// reason. That is committed beside each capture root as <c>sanitized-capture-ledger.v1.json</c> - every field of the raw file
/// except the SSE text, plus the raw file's own byte hash.
/// </para>
/// <para>
/// With the raw archive present, a read returns the raw file and is cross-checked against the ledger (raw -&gt; ledger). Without
/// it, the ledger is the authority and the audit runs unchanged over it; the SSE text it lacks is the one thing not re-hashed,
/// and that hash is itself recorded in the ledger. Nothing is skipped silently.
/// </para>
/// </summary>
internal static class H2cCaptureArchive
{
    public const string LedgerFile = "sanitized-capture-ledger.v1.json";
    public const string Schema = "v5-p6th2c-sanitized-capture-ledger-v1";

    /// <summary>A capture as the audits read it: the raw JSON record (without SSE when only the ledger exists) and its file hash.</summary>
    public sealed record Capture(string Path, string FileSha256, JsonDocument Record, bool FromRawArchive) : IDisposable
    {
        public JsonElement Root => Record.RootElement;
        public void Dispose() => Record.Dispose();
    }

    /// <summary>Relative paths under the capture root, from the raw files when the archive is complete and from the ledger otherwise.</summary>
    public static IReadOnlyList<string> List(string root, string pattern, SearchOption option)
    {
        var directory = TestRepository.Path(root);
        var files = Directory.Exists(directory)
            ? Directory.GetFiles(directory, pattern, option).Select(file => Relative(directory, file)).OrderBy(item => item, StringComparer.Ordinal).ToArray()
            : [];
        var ledger = ReadLedger(root);
        if (ledger is null) return files;
        var listed = ledger.Keys.Where(key => Matches(key, pattern, option)).OrderBy(item => item, StringComparer.Ordinal).ToArray();
        return listed.All(key => File.Exists(System.IO.Path.Combine(directory, key))) && files.Length >= listed.Length ? files : listed;
    }

    public static Capture Read(string root, string relativePath)
    {
        var directory = TestRepository.Path(root);
        var path = System.IO.Path.Combine(directory, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        var ledger = ReadLedger(root);
        if (File.Exists(path))
        {
            var bytes = File.ReadAllBytes(path);
            var capture = new Capture(relativePath, Hex(bytes), JsonDocument.Parse(bytes), true);
            if (ledger is not null && ledger.TryGetValue(relativePath, out var entry))
            {
                // Raw archive present: the committed ledger must be exactly what this raw file says.
                Assert.Equal(entry.FileSha256, capture.FileSha256);
                Assert.Equal(entry.Record.ToJsonString(), Sanitize(capture.Root).ToJsonString());
            }
            return capture;
        }

        Assert.True(ledger is not null && ledger.TryGetValue(relativePath, out _),
            $"neither the raw capture nor its sanitized ledger entry exists: {root}/{relativePath}");
        var recorded = ledger![relativePath];
        return new Capture(relativePath, recorded.FileSha256, JsonDocument.Parse(recorded.Record.ToJsonString()), false);
    }

    /// <summary>The ledger record of a raw capture: every field except the SSE text.</summary>
    public static JsonObject Sanitize(JsonElement raw)
    {
        var node = JsonNode.Parse(raw.GetRawText())!.AsObject();
        node.Remove("rawSse");
        return node;
    }

    public static string Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public sealed record LedgerEntry(string FileSha256, JsonObject Record);

    private static Dictionary<string, LedgerEntry>? ReadLedger(string root)
    {
        var path = TestRepository.Path(root + "/" + LedgerFile);
        if (!File.Exists(path)) return null;
        var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(Schema, document["schemaVersion"]!.GetValue<string>());
        var result = new Dictionary<string, LedgerEntry>(StringComparer.Ordinal);
        foreach (var item in document["captures"]!.AsArray())
        {
            var row = item!.AsObject();
            result.Add(row["path"]!.GetValue<string>(), new LedgerEntry(row["rawFileSha256"]!.GetValue<string>(), row["record"]!.AsObject()));
        }
        return result;
    }

    private static string Relative(string directory, string file) =>
        System.IO.Path.GetRelativePath(directory, file).Replace(System.IO.Path.DirectorySeparatorChar, '/');

    private static bool Matches(string key, string pattern, SearchOption option)
    {
        var name = key.Contains('/') ? key[(key.LastIndexOf('/') + 1)..] : key;
        if (option == SearchOption.TopDirectoryOnly && key.Contains('/')) return false;
        var suffix = pattern.TrimStart('*');
        return name.EndsWith(suffix, StringComparison.Ordinal);
    }

    /// <summary>Writes the ledger of a capture root from its local raw archive, verifying every hash it records. Gated test only.</summary>
    internal static void WriteLedger(string root, string pattern, SearchOption option)
    {
        var directory = TestRepository.Path(root);
        var captures = new JsonArray();
        foreach (var relative in Directory.GetFiles(directory, pattern, option).Select(file => Relative(directory, file)).OrderBy(item => item, StringComparer.Ordinal))
        {
            var bytes = File.ReadAllBytes(System.IO.Path.Combine(directory, relative));
            using var document = JsonDocument.Parse(bytes);
            var row = document.RootElement;
            if (row.TryGetProperty("rawResponse", out var response) && row.TryGetProperty("rawResponseSha256", out var responseHash) && response.ValueKind == JsonValueKind.String)
                Assert.Equal(responseHash.GetString(), Hex(Encoding.UTF8.GetBytes(response.GetString()!)));
            if (row.TryGetProperty("rawSse", out var sse) && row.TryGetProperty("rawSseSha256", out var sseHash) && sse.ValueKind == JsonValueKind.String)
                Assert.Equal(sseHash.GetString(), Hex(Encoding.UTF8.GetBytes(sse.GetString()!)));
            captures.Add(new JsonObject { ["path"] = relative, ["rawFileSha256"] = Hex(bytes), ["record"] = Sanitize(row) });
        }
        var ledger = new JsonObject
        {
            ["schemaVersion"] = Schema,
            ["providerCalls"] = 0,
            ["goldRead"] = false,
            ["rawSseIncluded"] = false,
            ["note"] = "Every field of each raw capture except the SSE text, plus the raw file's byte hash. The raw archive is local and not committed.",
            ["captures"] = captures,
        };
        File.WriteAllBytes(System.IO.Path.Combine(directory, LedgerFile),
            new UTF8Encoding(false).GetBytes(ledger.ToJsonString(FreezeArtifact.Json).ReplaceLineEndings("\n")));
    }
}

/// <summary>Writes the sanitized capture ledgers from a local raw archive. Inert unless A99_WRITE_CAPTURE_LEDGERS=1.</summary>
public sealed class H2cCaptureLedgerWriter
{
    private const string Root = "artifacts/v5-p6t-function-membership";

    [Fact]
    public void Write_the_sanitized_capture_ledgers_from_the_local_raw_archive()
    {
        if (Environment.GetEnvironmentVariable("A99_WRITE_CAPTURE_LEDGERS") != "1") return;
        H2cCaptureArchive.WriteLedger(Root + "/p6th2c-end-pointer-capture-20261005", "*.raw-capture.v1.json", SearchOption.TopDirectoryOnly);
        H2cCaptureArchive.WriteLedger(Root + "/p6th2c-end-pointer-retry-capture-20261005", "*.raw-capture.v1.json", SearchOption.AllDirectories);
        H2cCaptureArchive.WriteLedger(Root + "/p6th2c-end-pointer-clarified-retry-20261005", "*.raw-capture.v2.json", SearchOption.AllDirectories);
        H2cCaptureArchive.WriteLedger(Root + "/p6th2c-clean-v1-rerun-capture-20261005/raw", "*.raw-capture.v1.json", SearchOption.TopDirectoryOnly);
        H2cCaptureArchive.WriteLedger(Root + "/p6th2c-clean-v2-capture-20261005/raw", "*.raw-capture.v1.json", SearchOption.TopDirectoryOnly);
    }
}
