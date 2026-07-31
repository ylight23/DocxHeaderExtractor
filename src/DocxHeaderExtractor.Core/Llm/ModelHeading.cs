using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.Core.Llm;

/// <summary>Một mục do mô hình trả về: chỉ số đoạn + cấp tiêu đề (0 = không phải tiêu đề).</summary>
public sealed class ModelHeading
{
    public int Index { get; set; }
    public int Level { get; set; }
}

/// <summary>
/// Đọc JSON từ đầu ra mô hình. Chấp nhận cả hai lược đồ — {"h":[{"i":..,"l":..}]} (mặc định)
/// và {"headings":[{"i":..,"level":..}]} — đồng thời chịu được rác thừa ở hai đầu.
/// </summary>
public static class ModelJson
{
    private static readonly string[] ArrayKeys = ["h", "headings", "items", "result"];
    private static readonly string[] IndexKeys = ["i", "index", "idx"];
    private static readonly string[] LevelKeys = ["l", "level", "lvl"];

    private static readonly Regex SalvageRx = new(
        @"""(?:i|index)""\s*:\s*(\d+)\s*,\s*""(?:l|level|lvl)""\s*:\s*(\d+)",
        RegexOptions.Compiled);

    /// <summary>Trả về các mục có cấp ≥ 1; mục cấp 0 nghĩa là mô hình từ chối, bị loại tại đây.</summary>
    public static IReadOnlyList<ModelHeading> Parse(string raw)
    {
        var json = ExtractFirstObject(raw);
        if (json is null) return Salvage(raw);   // đầu ra bị cắt giữa chừng

        try
        {
            using var doc = JsonDocument.Parse(json);
            var array = FindArray(doc.RootElement);
            if (array is null) return Salvage(json);

            var result = new List<ModelHeading>();
            foreach (var item in array.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (TryReadInt(item, IndexKeys, out var index) &&
                    TryReadInt(item, LevelKeys, out var level) &&
                    level >= 1)
                {
                    result.Add(new ModelHeading { Index = index, Level = level });
                }
            }
            return result;
        }
        catch (JsonException)
        {
            return Salvage(json);
        }
    }

    private static JsonElement? FindArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root;
        if (root.ValueKind != JsonValueKind.Object) return null;

        foreach (var key in ArrayKeys)
            if (root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array)
                return v;

        // Khoá lạ nhưng giá trị là mảng – vẫn dùng.
        foreach (var prop in root.EnumerateObject())
            if (prop.Value.ValueKind == JsonValueKind.Array)
                return prop.Value;

        return null;
    }

    private static bool TryReadInt(JsonElement obj, string[] keys, out int value)
    {
        foreach (var key in keys)
        {
            if (!obj.TryGetProperty(key, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out value)) return true;
            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out value)) return true;
        }
        value = 0;
        return false;
    }

    /// <summary>Cắt lấy object JSON đầu tiên cân bằng ngoặc, bỏ qua ngoặc nằm trong chuỗi.</summary>
    public static string? ExtractFirstObject(string raw)
    {
        int start = raw.IndexOf('{');
        if (start < 0) return null;

        int depth = 0;
        bool inString = false, escaped = false;

        for (int i = start; i < raw.Length; i++)
        {
            var c = raw[i];

            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0) return raw[start..(i + 1)];
                    break;
            }
        }

        return null; // JSON bị cắt giữa chừng
    }

    /// <summary>Vớt vát khi JSON hỏng hoặc bị cắt: quét thủ công các cặp i/l.</summary>
    private static List<ModelHeading> Salvage(string text)
    {
        var result = new List<ModelHeading>();
        foreach (Match m in SalvageRx.Matches(text))
        {
            if (int.TryParse(m.Groups[1].Value, out var i) &&
                int.TryParse(m.Groups[2].Value, out var lvl) && lvl >= 1)
            {
                result.Add(new ModelHeading { Index = i, Level = lvl });
            }
        }
        return result;
    }

    public static string Describe(IReadOnlyList<ModelHeading> hs)
    {
        var sb = new StringBuilder();
        foreach (var h in hs) sb.Append(h.Index).Append(':').Append(h.Level).Append(' ');
        return sb.ToString().TrimEnd();
    }
}
