namespace DocxHeaderExtractor.Infrastructure.Runtime;

/// <summary>Composition-root paths for non-authoritative runtime state.</summary>
public static class RuntimeStatePaths
{
    public static string RootDirectory =>
        Path.GetFullPath(Environment.GetEnvironmentVariable("DHX_RUNTIME_STATE_DIR") ??
            Path.Combine(AppContext.BaseDirectory, "runtime"));

    public static string RunDirectory => Path.Combine(RootDirectory, "runs");

    public static string ReviewDirectory => Path.Combine(RootDirectory, "reviews");

    public static string TelemetryPath => Path.Combine(RootDirectory, "telemetry.jsonl");
}
