using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Vision;

namespace DocxHeaderExtractor.Core.Pipeline;

internal enum PdfVisualBlockRelation
{
    Uncertain,
    ParentChild,
    Siblings,
}

internal sealed record PdfVisualRelationDecision(
    string ParentCandidateId,
    string ChildCandidateId,
    PdfVisualBlockRelation Relation,
    double Confidence,
    string Evidence,
    string Raw);

/// <summary>
/// Asks a VLM about one visual relationship between two already-grounded candidate blocks.
/// This is deliberately audit-only: its answer must be checked against PDF geometry/path evidence
/// before any route is allowed to use it for a heading level.
/// </summary>
internal static class PdfVisualRelationAnalyst
{
    private const double PaddingX = 16;
    private const double PaddingY = 22;

    public static async Task<PdfVisualRelationDecision> AnalyzeAsync(
        VlmImageQuestion vlm,
        string pdfPath,
        PdfSemanticBlock upperBlock,
        PdfSemanticBlock lowerBlock,
        int dpi = 140,
        CancellationToken ct = default)
    {
        if (upperBlock.Page != lowerBlock.Page)
            return new PdfVisualRelationDecision(
                upperBlock.Id, lowerBlock.Id, PdfVisualBlockRelation.Uncertain, 0,
                "different-pages", "");

        var left = Math.Max(0, Math.Min(upperBlock.Left, lowerBlock.Left) - PaddingX);
        var right = Math.Max(upperBlock.Right, lowerBlock.Right) + PaddingX;
        var bottom = Math.Max(0, Math.Min(upperBlock.BottomY, lowerBlock.BottomY) - PaddingY);
        var top = Math.Max(upperBlock.TopY, lowerBlock.TopY) + PaddingY;
        byte[] crop;
        try
        {
            crop = PdfRegionRasterizer.RenderCropPng(pdfPath, upperBlock.Page, left, bottom, right, top, dpi);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return new PdfVisualRelationDecision(
                upperBlock.Id, lowerBlock.Id, PdfVisualBlockRelation.Uncertain, 0,
                $"render-failed: {ex.Message}", "");
        }

        var raw = await vlm.AskAsync(crop, BuildQuestion(upperBlock, lowerBlock), maxTokens: 260, ct);
        return ParseDecision(upperBlock.Id, lowerBlock.Id, raw);
    }

    internal static string BuildQuestion(PdfSemanticBlock upperBlock, PdfSemanticBlock lowerBlock) =>
        "Bạn là visual analyst cho outline PDF. Ảnh là cùng một vùng trang, với hai block ứng viên đã " +
        "được đánh dấu theo thứ tự từ trên xuống dưới. Chỉ quyết định QUAN HỆ giữa hai block có sẵn, " +
        "không trích xuất, không sửa text, không tạo heading mới, và không tự gán cấp cuối.\n" +
        $"A (ở trên): [{upperBlock.Id}] {upperBlock.DisplayText}\n" +
        $"B (ở dưới): [{lowerBlock.Id}] {lowerBlock.DisplayText}\n" +
        "parent_child chỉ khi A là tiêu đề cha/nhãn phần của B; siblings chỉ khi A và B cùng cấp; " +
        "uncertain khi crop không đủ hoặc evidence mâu thuẫn. Evidence chỉ mô tả điều thực sự nhìn thấy " +
        "(text, đường kẻ, khoảng trắng, thụt lề, bố cục); verdict sẽ còn bị grounding kiểm lại.\n" +
        "Trả lời đúng một JSON object, không thêm lời dẫn: " +
        "{\"upperId\":\"...\",\"lowerId\":\"...\",\"relation\":\"parent_child|siblings|uncertain\",\"confidence\":0.0,\"evidence\":\"mô tả cụ thể\"}";

    internal static PdfVisualRelationDecision ParseDecision(string expectedUpperId, string expectedLowerId, string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(ExtractJsonObject(raw));
            var root = document.RootElement;
            var upperId = root.TryGetProperty("upperId", out var upper) ? upper.GetString() ?? "" : "";
            var lowerId = root.TryGetProperty("lowerId", out var lower) ? lower.GetString() ?? "" : "";
            if (!string.Equals(upperId, expectedUpperId, StringComparison.Ordinal) ||
                !string.Equals(lowerId, expectedLowerId, StringComparison.Ordinal))
            {
                return new PdfVisualRelationDecision(expectedUpperId, expectedLowerId,
                    PdfVisualBlockRelation.Uncertain, 0, "id-mismatch", raw);
            }

            var evidence = root.TryGetProperty("evidence", out var evidenceProp)
                ? evidenceProp.GetString() ?? ""
                : "";
            if (!HasUsableEvidence(evidence))
            {
                return new PdfVisualRelationDecision(expectedUpperId, expectedLowerId,
                    PdfVisualBlockRelation.Uncertain, 0, "unusable-evidence", raw);
            }

            var relation = root.TryGetProperty("relation", out var relationProp)
                ? ParseRelation(relationProp.GetString() ?? "")
                : PdfVisualBlockRelation.Uncertain;
            var confidence = root.TryGetProperty("confidence", out var confidenceProp) && confidenceProp.TryGetDouble(out var value)
                ? Math.Clamp(value, 0, 1)
                : 0;
            return new PdfVisualRelationDecision(expectedUpperId, expectedLowerId, relation, confidence, evidence, raw);
        }
        catch (JsonException)
        {
            return new PdfVisualRelationDecision(expectedUpperId, expectedLowerId,
                PdfVisualBlockRelation.Uncertain, 0, "invalid-json", raw);
        }
    }

    private static PdfVisualBlockRelation ParseRelation(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "parent_child" or "parent-child" => PdfVisualBlockRelation.ParentChild,
            "siblings" or "sibling" => PdfVisualBlockRelation.Siblings,
            _ => PdfVisualBlockRelation.Uncertain,
        };

    private static bool HasUsableEvidence(string evidence)
    {
        var text = evidence.Trim('.', ' ', '"');
        return text.Length >= 15 && !Regex.IsMatch(text, "^(?:n/?a|none|null|unknown|không rõ)$", RegexOptions.IgnoreCase);
    }

    private static string ExtractJsonObject(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        return start >= 0 && end > start ? raw[start..(end + 1)] : raw;
    }
}
