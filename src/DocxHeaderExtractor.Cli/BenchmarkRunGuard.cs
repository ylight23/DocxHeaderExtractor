using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Cli;

/// <summary>
/// Opt-in guard for a frozen live benchmark. All validation happens before a classifier/provider is
/// constructed, so a profile mismatch cannot spend a provider call. The guard intentionally does
/// not decide extraction behavior; it only protects an already-frozen execution contract.
/// </summary>
public sealed class BenchmarkRunGuard : IDisposable
{
    private readonly string _manifestPath;
    private readonly string _manifestHash;
    private readonly string _firstLiveCallHashPath;
    private readonly FileStream _lockHandle;
    private bool _completed;

    private BenchmarkRunGuard(string manifestPath, string manifestHash, string firstLiveCallHashPath, FileStream lockHandle)
    {
        _manifestPath = manifestPath;
        _manifestHash = manifestHash;
        _firstLiveCallHashPath = firstLiveCallHashPath;
        _lockHandle = lockHandle;
    }

    public static BenchmarkRunGuard? Prepare(CommandLineOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.BenchmarkManifestPath)) return null;

        var manifestPath = Path.GetFullPath(options.BenchmarkManifestPath);
        if (!File.Exists(manifestPath)) throw new InvalidOperationException($"Benchmark manifest không tồn tại: {manifestPath}");
        var canonicalOutput = options.BenchmarkCanonicalOutputPath;
        if (string.IsNullOrWhiteSpace(canonicalOutput))
            throw new InvalidOperationException("--benchmark-manifest cần --benchmark-canonical-output để chặn ghi đè canonical run.");
        canonicalOutput = Path.GetFullPath(canonicalOutput);
        if (File.Exists(canonicalOutput))
            throw new InvalidOperationException($"Benchmark canonical output đã tồn tại; abort trước provider call: {canonicalOutput}");

        var actual = BenchmarkRunProfile.FromOptions(options);
        var manifestText = File.ReadAllText(manifestPath);
        using var manifest = JsonDocument.Parse(manifestText);
        ValidateFrozenProfile(manifest.RootElement, actual);

        var firstLiveCallHashPath = manifestPath + ".first-live-call-sha256";
        var manifestHash = Sha256(manifestPath);
        if (File.Exists(firstLiveCallHashPath))
            AssertHash(File.ReadAllText(firstLiveCallHashPath).Trim(), manifestHash,
                "Frozen benchmark manifest đã thay đổi sau first live call.");

        var lockPath = Path.GetFullPath(options.BenchmarkRunLockPath ?? manifestPath + ".run.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        try
        {
            var handle = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 1, FileOptions.DeleteOnClose);
            var bytes = Encoding.UTF8.GetBytes($"pid={Environment.ProcessId}\nmanifestSha256={manifestHash}\n");
            handle.SetLength(0);
            handle.Write(bytes, 0, bytes.Length);
            handle.Flush(true);
            return new BenchmarkRunGuard(manifestPath, manifestHash, firstLiveCallHashPath, handle);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"Benchmark run lock đang được giữ: {lockPath}", ex);
        }
    }

    /// <summary>Records the immutable first-live-call hash only after the caller completed its run.</summary>
    public void Complete()
    {
        AssertHash(_manifestHash, Sha256(_manifestPath), "Frozen benchmark manifest bị thay đổi trong live run.");
        if (File.Exists(_firstLiveCallHashPath))
            AssertHash(File.ReadAllText(_firstLiveCallHashPath).Trim(), _manifestHash,
                "Frozen benchmark manifest hash sidecar không khớp.");
        else
            File.WriteAllText(_firstLiveCallHashPath, _manifestHash + Environment.NewLine, new UTF8Encoding(false));
        _completed = true;
    }

    public void Dispose()
    {
        _lockHandle.Dispose();
        if (!_completed) return;
    }

    private static void ValidateFrozenProfile(JsonElement root, BenchmarkRunProfile actual)
    {
        if (!root.TryGetProperty("frozenProfile", out var frozen))
            throw new InvalidOperationException("Benchmark manifest thiếu frozenProfile.");
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["command"] = RequiredString(frozen, "command"),
            ["backend"] = RequiredString(frozen, "backend"),
            ["model"] = RequiredString(frozen, "model"),
            ["wideCandidates"] = RequiredBoolean(frozen, "wideCandidates").ToString(),
            ["supplementCandidates"] = RequiredBoolean(frozen, "supplementCandidates").ToString(),
            ["analystBlocks"] = RequiredInt(frozen, "analystBlocks").ToString(),
            ["semanticConcurrency"] = RequiredInt(frozen, "semanticConcurrency").ToString(),
            ["semanticRequestTimeoutSeconds"] = RequiredInt(frozen, "semanticRequestTimeoutSeconds").ToString(),
            ["semanticBatchTimeoutSeconds"] = RequiredInt(frozen, "semanticBatchTimeoutSeconds").ToString(),
            ["semanticLaneDeadlineSeconds"] = RequiredInt(frozen, "semanticLaneDeadlineSeconds").ToString(),
            ["visualRegions"] = RequiredInt(frozen, "visualRegions").ToString(),
            ["roleAndSpanCheckpoint"] = RequiredBoolean(frozen, "roleAndSpanCheckpoint").ToString(),
        };
        foreach (var (key, value) in expected)
            if (!actual.Values.TryGetValue(key, out var observed) || !StringComparer.Ordinal.Equals(value, observed))
                throw new InvalidOperationException($"PREFLIGHT_PROFILE_MISMATCH {key}: manifest={value}; requested={observed ?? "<missing>"}");
    }

    private static string RequiredString(JsonElement element, string property) => element.GetProperty(property).GetString()
        ?? throw new InvalidOperationException($"frozenProfile.{property} is null");
    private static bool RequiredBoolean(JsonElement element, string property) => element.GetProperty(property).GetBoolean();
    private static int RequiredInt(JsonElement element, string property) => element.GetProperty(property).GetInt32();
    private static void AssertHash(string expected, string actual, string message)
    {
        if (!StringComparer.Ordinal.Equals(expected, actual)) throw new InvalidOperationException(message);
    }
    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
public sealed record BenchmarkRunProfile(IReadOnlyDictionary<string, string> Values)
{
    public static BenchmarkRunProfile FromOptions(CommandLineOptions options)
    {
        var backend = options.Provider.Backend.ToString();
        var model = options.Provider.Backend switch
        {
            InferenceBackend.OpenRouter => options.Provider.Remote.Model,
            InferenceBackend.Sglang => options.Provider.Remote.Model,
            InferenceBackend.LmStudio => options.Provider.Remote.Model,
            _ => options.Provider.LocalModel.ModelPath,
        };
        return new BenchmarkRunProfile(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["command"] = options.Command,
            ["backend"] = backend,
            ["model"] = model ?? "",
            ["wideCandidates"] = options.PdfStageWideCandidates.ToString(),
            ["supplementCandidates"] = options.PdfStageSupplementCandidates.ToString(),
            ["analystBlocks"] = (options.PdfStageAllCandidates ? 0 : options.PdfStageAnalystBlocks).ToString(),
            ["semanticConcurrency"] = options.PdfStageSemanticConcurrency.ToString(),
            ["semanticRequestTimeoutSeconds"] = options.PdfStageSemanticRequestTimeoutSeconds.ToString(),
            ["semanticBatchTimeoutSeconds"] = options.PdfStageSemanticBatchTimeoutSeconds.ToString(),
            ["semanticLaneDeadlineSeconds"] = options.PdfStageSemanticLaneDeadlineSeconds.ToString(),
            ["visualRegions"] = options.PdfStageVisualRegions.ToString(),
            ["roleAndSpanCheckpoint"] = (!string.IsNullOrWhiteSpace(options.PdfStageCheckpointPath)).ToString(),
        });
    }
}
