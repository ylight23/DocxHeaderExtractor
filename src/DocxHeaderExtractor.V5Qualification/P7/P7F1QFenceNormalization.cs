using System.Text.Json;
using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.V5Qualification.P7;

internal sealed record F1QFenceResult(bool Applied, string Normalized, string Reason);

/// <summary>
/// Issue #6 Decision 4: the only approved transport normalization. Removes one complete, outermost
/// ```json ... ``` or ``` ... ``` wrapper (optional surrounding whitespace) if and only if the enclosed content is
/// exactly one valid JSON object with no other prose or markdown. Never repairs labels, ids, aliases, counts or JSON.
/// </summary>
internal static class P7F1QFenceNormalization
{
    public const string Version = "P7_F1Q_SINGLE_OUTER_FENCE_NORMALIZATION_V1";
    private static readonly Regex Fence = new(@"\A\s*```(?:json|JSON)?[ \t]*\r?\n(?<body>.*)\r?\n[ \t]*```\s*\z", RegexOptions.Singleline);

    public static F1QFenceResult Normalize(string raw)
    {
        var m = Fence.Match(raw);
        if (!m.Success) return new(false, raw, "NO_SINGLE_OUTER_FENCE");
        var body = m.Groups["body"].Value;
        if (body.Contains("```", StringComparison.Ordinal)) return new(false, raw, "NESTED_OR_MULTIPLE_FENCES");
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return new(false, raw, "ENCLOSED_NOT_OBJECT");
        }
        catch (JsonException) { return new(false, raw, "ENCLOSED_NOT_SINGLE_VALID_JSON"); }
        return new(true, body.Trim(), "SINGLE_OUTER_FENCE_REMOVED");
    }
}
