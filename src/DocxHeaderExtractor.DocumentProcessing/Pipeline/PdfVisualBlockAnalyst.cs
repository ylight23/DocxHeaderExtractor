using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.DocumentProcessing.Vision;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

internal sealed record PdfVisualBlockDecision(
    string Id,
    PdfBlockRole Role,
    double Confidence,
    string Evidence,
    string Raw,
    int ContextLinesAbove = 0,
    int ContextLinesBelow = 0);

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
    internal const int MaximumVisualBlocks = 40;
    private const double PaddingX = 8;
    private const double PaddingY = 5;

    public static async Task<PdfVisualBlockAnalysis> AnalyzeAsync(
        IPdfVisualQuestion vlm,
        string pdfPath,
        IReadOnlyList<PdfSemanticBlock> candidateBlocks,
        IReadOnlyList<PdfLine> documentLines,
        int dpi = 120,
        IReadOnlyDictionary<string, PdfCandidateContext>? contexts = null,
        CancellationToken ct = default)
    {
        var decisions = new List<PdfVisualBlockDecision>();
        var raw = new List<string>();

        foreach (var block in candidateBlocks.Take(MaximumVisualBlocks))
        {
            ct.ThrowIfCancellationRequested();
            byte[] png;
            try
            {
                var neighborhood = SelectNeighborhood(block, documentLines);
                var page = PdfRegionRasterizer.GetPageBounds(pdfPath, block.Page);
                png = PdfRegionRasterizer.RenderCropPng(
                    pdfPath,
                    block.Page,
                    0,
                    Math.Max(0, neighborhood.BottomY - PaddingY),
                    page.Width,
                    Math.Min(page.Height, neighborhood.TopY + PaddingY),
                    dpi);
                var context = contexts is not null && contexts.TryGetValue(block.Id, out var value) ? value : null;
                var answer = await vlm.AskAsync(png, BuildQuestion(block, context), maxTokens: 420, ct);
                raw.Add(answer);
                decisions.Add(ParseDecision(block.Id, answer) with
                {
                    ContextLinesAbove = neighborhood.Above.Count,
                    ContextLinesBelow = neighborhood.Below.Count,
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or HttpRequestException or TaskCanceledException)
            {
                var failed = new PdfVisualBlockDecision(block.Id, PdfBlockRole.Uncertain, 0,
                    $"render-failed: {ex.Message}", "");
                decisions.Add(failed);
                continue;
            }
        }

        return new PdfVisualBlockAnalysis(decisions, raw);
    }

    internal static string BuildQuestion(PdfSemanticBlock block, PdfCandidateContext? context = null) =>
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
        block.DisplayText +
        "\nThe rendered crop includes the full page width plus up to three nearby lines above and below the candidate." +
        (context is null ? "" : $"\nDocument context: regime={context.DocumentRegime}; active_heading_stack=[{string.Join(" | ", context.ActiveHeadingStack)}].");

    internal static PdfVisualNeighborhood SelectNeighborhood(PdfSemanticBlock block, IReadOnlyList<PdfLine> documentLines)
    {
        var samePage = documentLines.Where(line => line.Page == block.Page).ToArray();
        var above = samePage.Where(line => line.Y > block.TopY)
            .OrderBy(line => line.Y - block.TopY).Take(3).ToArray();
        var below = samePage.Where(line => line.Y < block.BottomY)
            .OrderBy(line => block.BottomY - line.Y).Take(3).ToArray();
        var linePadding = Math.Max(12, (block.Lines.Count == 0 ? 12 : block.Lines.Max(line => line.FontSize)) * 1.5);
        var top = Math.Max(block.TopY, above.Select(line => line.Y).DefaultIfEmpty(block.TopY).Max()) + linePadding;
        var bottom = Math.Min(block.BottomY, below.Select(line => line.Y).DefaultIfEmpty(block.BottomY).Min()) - linePadding;
        return new PdfVisualNeighborhood(above, below, top, bottom);
    }

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

internal sealed record PdfVisualNeighborhood(
    IReadOnlyList<PdfLine> Above,
    IReadOnlyList<PdfLine> Below,
    double TopY,
    double BottomY);
