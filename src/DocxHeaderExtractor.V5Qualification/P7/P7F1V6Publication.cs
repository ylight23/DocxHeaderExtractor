using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.V5Qualification.P7;

/// <summary>
/// Copies the safe-store runs of one consumed plan into the repository for publication (D4.11f; the owner instructed on 2026-10-13 that raw
/// responses and results be pushed; a run is published only if its approving message repeats that). Nothing is rewritten: raw responses keep
/// their bytes. It refuses, before copying anything, when: no consumed marker for the plan sits beside the runs; a run summary names another
/// plan; the runs are not in one safe root; a stored response's hash is not its file name; any file contains the API key or a string shaped
/// like one; or an output file already exists.
/// </summary>
internal static class P7F1V6Publication
{
    public const string ManifestArtifact = "p7.f1.v6.d4.11-publication-manifest.v1";
    static readonly Regex KeyShaped = new(@"sk-or-[A-Za-z0-9_\-]{10,}", RegexOptions.CultureInvariant);
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <param name="UncompressedSha256">For a raw-sse-gz file: the SHA-256 of the received bytes (the stored file's name), which the gzip decompresses to.</param>
    public sealed record Entry(string File, string Kind, long Bytes, string Sha256, string? UncompressedSha256 = null, long? UncompressedBytes = null);
    public sealed record Result(string? Refusal, IReadOnlyList<Entry> Files);

    static Result Refuse(string code) => new(code, Array.Empty<Entry>());

    /// <param name="compressRawSse">Write each raw SSE response as a gzip file (lossless; a stream is large and compresses well). The manifest keeps the
    /// hash of the received bytes, which is the stored file's name, so the response is still identified by what was received.</param>
    public static Result Publish(string planPath, string outDir, IReadOnlyList<string> runDirs, string? apiKey, bool compressRawSse = false)
    {
        if (runDirs.Count == 0) return Refuse("NO_RUNS");
        var planSha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(planPath)));
        var root = Path.GetDirectoryName(Path.GetFullPath(runDirs[0]))!;
        if (!File.Exists(Path.Combine(root, "consumed-" + planSha + ".json"))) return Refuse("NO_CONSUMED_MARKER_FOR_PLAN");

        var copies = new List<(string From, string To, string Kind)>();
        foreach (var run in runDirs)
        {
            var full = Path.GetFullPath(run);
            if (Path.GetDirectoryName(full) != root) return Refuse("RUNS_NOT_IN_ONE_SAFE_ROOT");
            var summaryPath = Path.Combine(full, P7F1V6Retention.SummaryFile);
            if (!File.Exists(summaryPath)) return Refuse("RUN_SUMMARY_MISSING");
            using var summary = JsonDocument.Parse(File.ReadAllBytes(summaryPath));
            if (summary.RootElement.GetProperty("planSha256").GetString() != planSha) return Refuse("RUN_BELONGS_TO_ANOTHER_PLAN");
            var arm = summary.RootElement.GetProperty("Arm").GetString()!;
            if (arm.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) return Refuse("ARM_NAME_INVALID");
            var armDir = Path.Combine(outDir, "arm-" + arm);
            copies.Add((summaryPath, Path.Combine(armDir, P7F1V6Retention.SummaryFile), "summary"));
            foreach (var f in Directory.EnumerateFiles(full, "*.bin").OrderBy(x => x, StringComparer.Ordinal))
            {
                var bytes = File.ReadAllBytes(f);
                var name = Path.GetFileNameWithoutExtension(f);
                if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != name) return Refuse("STORED_RESPONSE_HASH_MISMATCH");
                var sse = bytes.Length > 0 && (bytes[0] == (byte)':' || Encoding.ASCII.GetString(bytes, 0, Math.Min(5, bytes.Length)) == "data:");
                copies.Add((f, Path.Combine(armDir, "raw", name + (sse ? ".sse" : ".json")), sse ? "raw-sse" : "raw-json"));
            }
            var requests = Path.Combine(full, "requests");
            if (Directory.Exists(requests))
                foreach (var f in Directory.EnumerateFiles(requests, "*.json").OrderBy(x => x, StringComparer.Ordinal))
                    copies.Add((f, Path.Combine(armDir, "requests", Path.GetFileName(f)), "request"));
        }

        // Every file is read and checked before the first copy: one key anywhere refuses the whole publication.
        foreach (var (from, to, _) in copies)
        {
            if (File.Exists(to)) return Refuse("OUTPUT_EXISTS");
            var text = Encoding.UTF8.GetString(File.ReadAllBytes(from));
            if (!string.IsNullOrWhiteSpace(apiKey) && text.Contains(apiKey, StringComparison.Ordinal)) return Refuse("API_KEY_IN_FILE");
            if (KeyShaped.IsMatch(text)) return Refuse("KEY_SHAPED_STRING_IN_FILE");
        }

        var entries = new List<Entry>();
        foreach (var (from, to, kind) in copies)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            if (compressRawSse && kind == "raw-sse")
            {
                var original = File.ReadAllBytes(from);
                var target = to + ".gz";
                using (var fs = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionLevel.SmallestSize))
                    gz.Write(original, 0, original.Length);
                var zipped = File.ReadAllBytes(target);
                // Read back: the file must decompress to the received bytes.
                using (var back = new System.IO.Compression.GZipStream(new MemoryStream(zipped), System.IO.Compression.CompressionMode.Decompress))
                using (var ms = new MemoryStream())
                {
                    back.CopyTo(ms);
                    if (!ms.ToArray().AsSpan().SequenceEqual(original)) throw new IOException("GZIP_ROUND_TRIP_MISMATCH");
                }
                entries.Add(new Entry(Path.GetRelativePath(outDir, target).Replace(Path.DirectorySeparatorChar, '/'), "raw-sse-gz", zipped.Length,
                    Convert.ToHexStringLower(SHA256.HashData(zipped)), Convert.ToHexStringLower(SHA256.HashData(original)), original.Length));
                continue;
            }
            File.Copy(from, to, overwrite: false);
            var copied = File.ReadAllBytes(to);
            entries.Add(new Entry(Path.GetRelativePath(outDir, to).Replace(Path.DirectorySeparatorChar, '/'), kind, copied.Length,
                Convert.ToHexStringLower(SHA256.HashData(copied))));
        }
        File.WriteAllText(Path.Combine(outDir, "publication-manifest.json"), JsonSerializer.Serialize(new
        {
            artifact = ManifestArtifact, planSha256 = planSha, files = entries,
            note = (compressRawSse ? "Copied from the safe store; raw SSE responses are gzip files whose decompressed bytes are the received bytes (uncompressedSha256 is the safe-store name). " : "Copied unchanged from the safe store. ") + "Raw SSE responses include the model's reasoning text. The documents are the repository's public heading corpus (todo10_8/); each plan states its document's publicity. No API key is in any file (checked before copying).",
        }, Indented) + "\n", new UTF8Encoding(false));
        return new Result(null, entries);
    }
}
