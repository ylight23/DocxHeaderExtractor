using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Mcp;

public sealed class McpJobStore
{
    private readonly string _directory;
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public McpJobStore()
    {
        _directory = Environment.GetEnvironmentVariable("DHX_MCP_JOB_DIR") is { Length: > 0 } configured
            ? Path.GetFullPath(configured)
            : Path.Combine(Path.GetTempPath(), "DocxHeaderExtractor", "mcp-jobs");
        Directory.CreateDirectory(_directory);
    }

    public int CountActiveOrRecent() => Files().Count();

    public void Save(McpJobStatusResult status)
    {
        var target = FilePath(status.JobId);
        var temp = target + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, JsonSerializer.Serialize(status, _json));
        File.Move(temp, target, overwrite: true);
    }

    public McpJobStatusResult? Load(string jobId)
    {
        var path = FilePath(jobId);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<McpJobStatusResult>(File.ReadAllText(path), _json); }
        catch (JsonException) { return null; }
    }

    public void DeleteExpired(DateTimeOffset cutoff)
    {
        foreach (var path in Files())
        {
            try
            {
                var status = JsonSerializer.Deserialize<McpJobStatusResult>(File.ReadAllText(path), _json);
                if (status?.CompletedAt is { } completed && completed < cutoff) File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private string FilePath(string jobId) => Path.Combine(_directory, jobId.ToLowerInvariant() + ".json");
    private IEnumerable<string> Files() => Directory.EnumerateFiles(_directory, "*.json");
}
