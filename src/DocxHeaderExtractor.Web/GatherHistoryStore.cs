using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Gather;

namespace DocxHeaderExtractor.Web;

/// <summary>One local preview snapshot per explicitly named history. It stores document text; it never carries approvals or user decisions.</summary>
public sealed class GatherHistoryStore(string root)
{
    static readonly object Gate = new();
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    public sealed record Outcome(string Status, GatherImpactResult? Impact);

    public Outcome CompareAndSave(string key, GatherSnapshot current)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 100) return new("INVALID_HISTORY_KEY", null);
        var name = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key.Trim()))) + ".json";
        var path = Path.Combine(root, name);
        lock (Gate)
        {
            string? temp = null;
            try
            {
                GatherSnapshot? previous = null;
                if (File.Exists(path))
                {
                    previous = JsonSerializer.Deserialize<GatherSnapshot>(File.ReadAllText(path), Json);
                    if (previous is null || previous.Documents is null || previous.Plan is null) return new("INVALID_SNAPSHOT", null);
                }
                var impact = previous is null ? null : GatherImpact.Compare(previous, current);
                Directory.CreateDirectory(root);
                temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(current, Json));
                File.Move(temp, path, overwrite: true);
                return new(previous is null ? "BASELINE_SAVED" : "COMPARED", impact);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
            { return new("HISTORY_UNAVAILABLE", null); }
            finally { if (temp is not null && File.Exists(temp)) try { File.Delete(temp); } catch (IOException) { } }
        }
    }
}
