using System.Text.Json;

namespace DocxHeaderExtractor.Cli;

/// <summary>Finds a local <c>.gguf</c> model: <c>DHX_MODEL</c>, then <c>modelPath</c> in appsettings.json, then a <c>models</c> folder.</summary>
internal static class ModelLocator
{
    public static string? Locate()
    {
        var env = Environment.GetEnvironmentVariable("DHX_MODEL");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

        foreach (var settings in SettingsCandidates())
        {
            if (!File.Exists(settings)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(settings));
                if (doc.RootElement.TryGetProperty("modelPath", out var mp))
                {
                    var p = mp.GetString();
                    if (!string.IsNullOrWhiteSpace(p))
                    {
                        var full = Path.IsPathRooted(p)
                            ? p
                            : Path.Combine(Path.GetDirectoryName(settings)!, p);
                        if (File.Exists(full)) return Path.GetFullPath(full);
                    }
                }
            }
            catch (JsonException) { /* file cấu hình hỏng – bỏ qua */ }
        }

        foreach (var dir in ModelDirCandidates())
        {
            if (!Directory.Exists(dir)) continue;
            var ggufs = Directory.GetFiles(dir, "*.gguf");
            var preferred = ggufs.FirstOrDefault(f =>
                Path.GetFileName(f).Contains("llama-3.2-3b", StringComparison.OrdinalIgnoreCase));
            if (preferred is not null) return preferred;
            if (ggufs.Length >= 1) return ggufs[0];
        }

        return null;
    }

    private static IEnumerable<string> SettingsCandidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        yield return Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
    }

    private static IEnumerable<string> ModelDirCandidates()
    {
        yield return Path.Combine(Directory.GetCurrentDirectory(), "models");
        yield return Path.Combine(AppContext.BaseDirectory, "models");
        // bin/Debug/net9.0 → lên gốc repo
        yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "models"));
    }
}
