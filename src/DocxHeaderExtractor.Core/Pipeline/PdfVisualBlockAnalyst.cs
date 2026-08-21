using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Vision;

namespace DocxHeaderExtractor.Core.Pipeline;

internal sealed record PdfVisualBlockDecision(
    string Id,
    PdfBlockRole Role,
    double Confidence,
    string Evidence,
    string Raw);

internal sealed record PdfVisualBlockAnalysis(
    IReadOnlyList<PdfVisualBlockDecision> Decisions,
    IReadOnlyList<string> RawResponses);

/// <summary>
/// Visual analyst for already-filtered PDF candidate blocks. Deterministic code decides which blocks
/// are worth looking at and later grounds any accepted heading; the VLM only classifies the rendered
/// crop's role when text/style signals are ambiguous.
/// </summary>
internal static class PdfVisualBlockAnalyst
{
    private const int MaxBlocks = 40;
    private const double PaddingX = 8;
    private const double PaddingY = 5;

    public static async Task<PdfVisualBlockAnalysis> AnalyzeAsync(
        VlmImageQuestion vlm,
        string pdfPath,
        IReadOnlyList<PdfSemanticBlock> candidateBlocks,
        int dpi = 120,
        CancellationToken ct = default)
    {
        var decisions = new List<PdfVisualBlockDecision>();
        var raw = new List<string>();

        foreach (var block in candidateBlocks.Take(MaxBlocks))
        {
            ct.ThrowIfCancellationRequested();
            byte[] png;
            try
            {
                png = PdfRegionRasterizer.RenderCropPng(
                    pdfPath,
                    block.Page,
                    Math.Max(0, block.Left - PaddingX),
                    Math.Max(0, block.BottomY - PaddingY),
                    block.Right + PaddingX,
                    block.TopY + PaddingY,
                    dpi);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                var failed = new PdfVisualBlockDecision(block.Id, PdfBlockRole.Uncertain, 0,
                    $"render-failed: {ex.Message}", "");
                decisions.Add(failed);
                continue;
            }

            var answer = await vlm.AskAsync(png, BuildQuestion(block), maxTokens: 420, ct);
            raw.Add(answer);
            decisions.Add(ParseDecision(block.Id, answer));
        }

        return new PdfVisualBlockAnalysis(decisions, raw);
    }

    internal static string BuildQuestion(PdfSemanticBlock block) =>
        "Bạn là visual analyst bị giới hạn cho trích xuất outline PDF. Code deterministic đã tạo " +
        "candidate này sau khi lọc header/footer, số trang và nhiễu bảng rõ ràng; ảnh crop chỉ chứa MỘT block còn mơ hồ.\n" +
        "Nhiệm vụ duy nhất: phân vai block có sẵn. Không tạo block mới, không sửa/OCR lại text, " +
        "không nối block, không tự lập outline và không tự gán cấp/cha-con.\n" +
        "role CHỈ được là: heading_topic, body_sentence, table_or_chart_label, decorative_noise, uncertain.\n" +
        "heading_topic là nhãn/chủ đề mở mục nội dung. table_or_chart_label là nhãn bảng, nhãn cột, metric, " +
        "legend, số liệu hoặc caption biểu đồ dù có in đậm. body_sentence là câu/vế văn xuôi.\n" +
        "Chỉ dùng heading_topic khi crop có bằng chứng nhìn thấy. Nếu crop không đủ, mâu thuẫn, hoặc text parser khác ảnh, trả uncertain.\n" +
        "evidence phải nêu chi tiết NHÌN THẤY trong crop (ví dụ text hiển thị, đường kẻ, khoảng trắng, thụt lề, vùng bảng); " +
        "không suy diễn từ confidence và không được để trống.\n" +
        "Trả lời đúng một JSON object, không thêm lời dẫn: " +
        "{\"id\":\"" + block.Id + "\",\"role\":\"heading_topic|body_sentence|table_or_chart_label|decorative_noise|uncertain\",\"confidence\":0.0,\"evidence\":\"mô tả cụ thể\"}\n" +
        "Text do parser đọc được chỉ để định danh candidate, không phải dữ liệu để bạn sửa hay bổ sung:\n" +
        block.DisplayText;

    internal static PdfVisualBlockDecision ParseDecision(string expectedId, string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(ExtractJsonObject(raw));
            var root = doc.RootElement;
            var id = root.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
            if (!string.Equals(id, expectedId, StringComparison.Ordinal))
                return new PdfVisualBlockDecision(expectedId, PdfBlockRole.Uncertain, 0, "id-mismatch", raw);

            var roleText = root.TryGetProperty("role", out var roleProp) ? roleProp.GetString() ?? "" : "";
            var confidence = root.TryGetProperty("confidence", out var confProp) &&
                             confProp.TryGetDouble(out var c)
                ? Math.Clamp(c, 0, 1)
                : 0;
            var evidence = root.TryGetProperty("evidence", out var evidenceProp)
                ? evidenceProp.GetString() ?? ""
                : "";
            if (!HasUsableEvidence(evidence))
                return new PdfVisualBlockDecision(expectedId, PdfBlockRole.Uncertain, 0, "unusable-evidence", raw);

            return new PdfVisualBlockDecision(expectedId, ParseRole(roleText), confidence, evidence, raw);
        }
        catch (JsonException)
        {
            return new PdfVisualBlockDecision(expectedId, PdfBlockRole.Uncertain, 0, "invalid-json", raw);
        }
    }

    private static PdfBlockRole ParseRole(string role) =>
        role.Trim().ToLowerInvariant() switch
        {
            "heading_topic" or "heading" or "topic" => PdfBlockRole.HeadingTopic,
            "body_sentence" or "body" or "prose" => PdfBlockRole.BodySentence,
            "table_or_chart_label" or "table_label" or "chart_label" or "table" or "chart" =>
                PdfBlockRole.TableOrChartLabel,
            "decorative_noise" or "decorative" or "noise" or "logo" => PdfBlockRole.DecorativeNoise,
            _ => PdfBlockRole.Uncertain,
        };

    private static bool HasUsableEvidence(string evidence)
    {
        var text = evidence.Trim('.', '…', ' ', '"');
        if (text.Length < 15) return false;
        if (Regex.IsMatch(text, @"^(?:n/?a|none|null|unknown|không rõ|\.\.\.)$", RegexOptions.IgnoreCase))
            return false;
        return true;
    }

    private static string ExtractJsonObject(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        return start >= 0 && end > start ? raw[start..(end + 1)] : raw;
    }
}
