using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.V5Qualification.P7;

/// <summary>
/// Canary T1 (D4): one request, synthetic public text only. This class builds the request and checks it. It has no send path: the single
/// POST is a separate step, run only after a reviewed pinned commit and an explicit confirmation. Every check below is evaluated from the
/// built bytes, not from a flag.
/// </summary>
internal static class P7F1V6DirectCanary
{
    public const int MaxRequestBytes = 16384;
    public const double CeilingUsd = 0.02;
    public const int MaxTokensCap = 32768;

    // Synthetic and public: written for this check, not taken from any PDF, Gold or internal document.
    const string Instruction = "Chọn các dòng là tiêu đề giả lập (kiểm tra kỹ thuật, không phải dữ liệu thật).";
    static readonly (string Alias, string Text)[] SyntheticLines =
    {
        ("S0001", "Mục A. Phần mở đầu giả lập"),
        ("S0002", "Đây là đoạn văn bản thường dùng để kiểm tra độ dài."),
        ("S0003", "Mục B. Phần thứ hai giả lập"),
        ("S0004", "Một dòng nội dung khác, không phải tiêu đề."),
    };

    public sealed record Check(string Code, bool Passed, string Reason);

    public sealed record Built(P7F1V6Protocol.PreparedRequest Request, IReadOnlyList<Check> Checks, bool ReadyForReviewedSend,
        double ReserveUsd, string SyntheticSha256);

    public static Built Build()
    {
        var lines = SyntheticLines.Select(l => new P7F1V6Direct.Line(l.Alias, 1, l.Text)).ToList();
        var syntheticSha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", SyntheticLines.Select(l => l.Text)))));
        var req = P7F1V6Direct.BuildRequest(Instruction, syntheticSha, lines, 0, lines.Count);

        var checks = new List<Check>();
        void Add(string code, bool ok, string reason) => checks.Add(new Check(code, ok, ok ? "OK" : reason));

        Add("REQUEST_BYTES", req.Bytes <= MaxRequestBytes, $"request {req.Bytes} bytes exceeds {MaxRequestBytes}");
        var recomputed = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(req.Body)));
        Add("REQUEST_SHA", recomputed == req.Sha256 && Encoding.UTF8.GetByteCount(req.Body) == req.Bytes, "body does not match its recorded SHA or length");

        using var doc = JsonDocument.Parse(req.Body);
        var root = doc.RootElement;
        Add("MAX_TOKENS", root.TryGetProperty("max_tokens", out var mt) && mt.GetInt32() is > 0 and <= MaxTokensCap,
            "max_tokens missing or above the cap");
        Add("NO_REASONING_DECLARED", !root.TryGetProperty("reasoning", out _) && !req.Body.Contains("\"reasoning\"", StringComparison.Ordinal),
            "a reasoning object is declared; the canary declares none");
        var route = root.GetProperty("provider");
        var only = route.GetProperty("only").EnumerateArray().Select(x => x.GetString()).ToArray();
        var ignore = route.GetProperty("ignore").EnumerateArray().Select(x => x.GetString()).ToArray();
        Add("ROUTE", only.SequenceEqual(new[] { P7F1V6Protocol.ProviderTag }) && ignore.Contains("alibaba/us-east-1")
            && !route.GetProperty("allow_fallbacks").GetBoolean() && route.GetProperty("require_parameters").GetBoolean(),
            "route is not alibaba-only with us-east-1 ignored, fallbacks off and required parameters");
        Add("MODEL", root.GetProperty("model").GetString() == P7F1V6Protocol.ChatModel, "model is not the approved slug");
        Add("USAGE_INCLUDED", root.TryGetProperty("usage", out var usage) && usage.GetProperty("include").GetBoolean(), "usage.include is not set");

        var reserve = P7F1V6Direct.AttemptUsd(req.Bytes);
        Add("RESERVE_UNDER_CEILING", reserve < CeilingUsd, $"worst-case reserve {reserve:G6} is not below {CeilingUsd}");

        var ready = checks.All(c => c.Passed);
        return new Built(req, checks, ready, reserve, syntheticSha);
    }
}
