using System.Text.Json;
using DocxHeaderExtractor.Cli;

namespace DocxHeaderExtractor.Tests;

public sealed class BenchmarkRunGuardTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dhx-benchmark-guard-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ProfileMismatchAbortsBeforeProviderCallback()
    {
        Directory.CreateDirectory(_directory);
        var manifest = WriteManifest(semanticConcurrency: 2);
        var options = Options(manifest, semanticConcurrency: 1);
        var providerCallCount = 0;

        Assert.Throws<InvalidOperationException>(() =>
        {
            using var guard = BenchmarkRunGuard.Prepare(options);
            providerCallCount++;
        });
        Assert.Equal(0, providerCallCount);
    }

    [Fact]
    public void ExistingCanonicalOutputAbortsBeforeProviderCallback()
    {
        Directory.CreateDirectory(_directory);
        var manifest = WriteManifest();
        var output = Path.Combine(_directory, "canonical.json");
        File.WriteAllText(output, "already canonical");
        var providerCallCount = 0;

        Assert.Throws<InvalidOperationException>(() =>
        {
            using var guard = BenchmarkRunGuard.Prepare(Options(manifest));
            providerCallCount++;
        });
        Assert.Equal(0, providerCallCount);
    }

    [Fact]
    public void ExclusiveLockRejectsConcurrentRun()
    {
        Directory.CreateDirectory(_directory);
        var manifest = WriteManifest();
        using var first = BenchmarkRunGuard.Prepare(Options(manifest));
        Assert.Throws<InvalidOperationException>(() => BenchmarkRunGuard.Prepare(Options(manifest, outputName: "other.json")));
    }

    [Fact]
    public void FirstLiveCallHashMakesManifestImmutableForLaterRuns()
    {
        Directory.CreateDirectory(_directory);
        var manifest = WriteManifest();
        using (var guard = BenchmarkRunGuard.Prepare(Options(manifest)))
            guard!.Complete();

        using (var json = JsonDocument.Parse(File.ReadAllText(manifest)))
        {
            var profile = json.RootElement.GetProperty("frozenProfile").GetRawText();
            File.WriteAllText(manifest, $"{{\"frozenProfile\":{profile},\"postRunNote\":\"mutation\"}}");
        }

        Assert.Throws<InvalidOperationException>(() => BenchmarkRunGuard.Prepare(Options(manifest, outputName: "later.json")));
    }

    private string WriteManifest(int semanticConcurrency = 2)
    {
        var path = Path.Combine(_directory, "manifest.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            frozenProfile = new
            {
                command = "pdf-hierarchy-facts",
                backend = "OpenRouter",
                model = "qwen/qwen3.5-9b",
                wideCandidates = true,
                supplementCandidates = true,
                analystBlocks = 160,
                semanticConcurrency,
                semanticRequestTimeoutSeconds = 90,
                semanticBatchTimeoutSeconds = 120,
                semanticLaneDeadlineSeconds = 300,
                visualRegions = 0,
                roleAndSpanCheckpoint = true,
            },
        }));
        return path;
    }

    private CommandLineOptions Options(string manifest, int semanticConcurrency = 2, string outputName = "canonical.json") =>
        CommandLineOptions.Parse([
            "pdf-hierarchy-facts", "document.docx", "--openrouter", "--openrouter-model", "qwen/qwen3.5-9b",
            "--pdf-stage-wide", "--pdf-stage-supplement", "--pdf-stage-blocks", "160",
            "--pdf-stage-semantic-concurrency", semanticConcurrency.ToString(),
            "--pdf-stage-semantic-request-timeout", "90", "--pdf-stage-semantic-batch-timeout", "120",
            "--pdf-stage-semantic-lane-deadline", "300", "--pdf-stage-checkpoint", Path.Combine(_directory, "checkpoint.jsonl"),
            "--benchmark-manifest", manifest, "--benchmark-run-lock", Path.Combine(_directory, "run.lock"),
            "--benchmark-canonical-output", Path.Combine(_directory, outputName),
        ]);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
