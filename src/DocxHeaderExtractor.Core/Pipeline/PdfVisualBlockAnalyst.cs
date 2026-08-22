using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Vision;
using SkiaSharp;

namespace DocxHeaderExtractor.Core.Pipeline;

internal sealed record PdfVisualBlockDecision(
    string Id,
    PdfBlockRole Role,
    double Confidence,
    string Evidence,
    string Raw,
    IReadOnlyList<string>? VisualEvidenceTags = null,
    SourceTextSpan? HeadingSpan = null);

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
    private const double PaddingX = 8;
    private const double PaddingY = 10;
    private const int MultiImageBatchSize = 4;
    // Remote VLM calls dominate audit time. Keep this deliberately bounded so a document gets
    // faster review without turning a corpus run into an unbounded request burst.
    private const int DefaultMaximumConcurrentVisualRequests = 4;

    public static async Task<PdfVisualBlockAnalysis> AnalyzeAsync(
        IVisualQuestion vlm,
        string pdfPath,
        IReadOnlyList<PdfSemanticBlock> candidateBlocks,
        int dpi = 120,
        CancellationToken ct = default)
        => await AnalyzeAsync(vlm, pdfPath, candidateBlocks, candidateBlocks, dpi, maximumBlocks: 40, ct: ct);

    /// <summary>
    /// Confirms an existing candidate span in visual context. Neighbour blocks are supplied only to
    /// frame the crop; the VLM can never emit their IDs or create a new candidate.
    /// </summary>
    public static async Task<PdfVisualBlockAnalysis> AnalyzeAsync(
        IVisualQuestion vlm,
        string pdfPath,
        IReadOnlyList<PdfSemanticBlock> candidateBlocks,
        IReadOnlyList<PdfSemanticBlock> pageCatalog,
        int dpi,
        int maximumBlocks,
        int maximumConcurrentRequests = DefaultMaximumConcurrentVisualRequests,
        CancellationToken ct = default)
    {
        var candidates = maximumBlocks == 0 ? candidateBlocks : candidateBlocks.Take(maximumBlocks);
        if (vlm is IMultiImageVisualQuestion multiImageVlm && multiImageVlm.MaximumImagesPerRequest > 1)
            return await AnalyzeMultiImageAsync(multiImageVlm, pdfPath, candidates.ToArray(), pageCatalog, dpi, ct);

        using var throttle = new SemaphoreSlim(Math.Clamp(maximumConcurrentRequests, 1, 16));
        var tasks = candidates.Select(async block =>
        {
            await throttle.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                byte[] png;
                IReadOnlyList<PdfSemanticBlock> context;
                try
                {
                    context = NeighborContext(block, pageCatalog);
                    png = RenderContextPng(pdfPath, context, dpi);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                {
                    return new PdfVisualBlockDecision(block.Id, PdfBlockRole.Uncertain, 0,
                        $"render-failed: {ex.Message}", "");
                }

                var answer = await vlm.AskAsync(png, BuildQuestion(block, context), maxTokens: 420, ct);
                return ParseDecision(block.Id, answer);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new PdfVisualBlockDecision(block.Id, PdfBlockRole.Uncertain, 0,
                    $"visual-request-failed: {ex.Message}", "");
            }
            finally
            {
                throttle.Release();
            }
        }).ToArray();
        var decisions = await Task.WhenAll(tasks);

        return new PdfVisualBlockAnalysis(decisions, decisions.Where(d => !string.IsNullOrWhiteSpace(d.Raw)).Select(d => d.Raw).ToArray());
    }

    private static async Task<PdfVisualBlockAnalysis> AnalyzeMultiImageAsync(
        IMultiImageVisualQuestion vlm,
        string pdfPath,
        IReadOnlyList<PdfSemanticBlock> candidates,
        IReadOnlyList<PdfSemanticBlock> pageCatalog,
        int dpi,
        CancellationToken ct)
    {
        var decisions = new List<PdfVisualBlockDecision>();
        var rawResponses = new List<string>();
        foreach (var batch in candidates.Chunk(MultiImageBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var contexts = new List<IReadOnlyList<PdfSemanticBlock>>(batch.Length);
            var crops = new List<byte[]>(batch.Length);
            try
            {
                foreach (var block in batch)
                {
                    var context = NeighborContext(block, pageCatalog);
                    contexts.Add(context);
                    crops.Add(RenderContextPng(pdfPath, context, dpi));
                }

                var raw = await vlm.AskManyAsync(crops, BuildBatchQuestion(batch, contexts), maxTokens: 900, ct);
                rawResponses.Add(raw);
                decisions.AddRange(ParseBatchDecisions(batch, raw));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Preserve transport/parser failures in audit output. A missing VLM decision must
                // never be indistinguishable from a semantic rejection.
                rawResponses.Add("[visual-batch-failed] " + ex.Message);
                decisions.AddRange(batch.Select(block => new PdfVisualBlockDecision(
                    block.Id, PdfBlockRole.Uncertain, 0, $"visual-batch-failed: {ex.Message}", "")));
            }
        }

        return new PdfVisualBlockAnalysis(decisions, rawResponses);
    }

    internal static string BuildQuestion(PdfSemanticBlock block) => BuildQuestion(block, [block]);

    internal static string BuildQuestion(PdfSemanticBlock block, IReadOnlyList<PdfSemanticBlock> context) =>
        "Bạn là visual analyst bị giới hạn cho trích xuất outline PDF. Code deterministic đã tạo " +
        "candidate này từ catalog lossless; ảnh crop chứa candidate và tối đa hai dòng liền trước/sau " +
        "theo thứ tự đọc, kể cả khi chúng nằm ở trang kề. Các trang khác nhau là panel riêng trong ảnh.\n" +
        "Nhiệm vụ duy nhất: phân vai block có sẵn. Không tạo block mới, không sửa/OCR lại text, " +
        "không nối block, không tự lập outline và không tự gán cấp/cha-con.\n" +
        "role CHỈ được là: heading_topic, document_title, body_sentence, table_or_chart_label, decorative_noise, uncertain.\n" +
        "document_title/cover_title là tên tài liệu ở bìa, KHÔNG phải heading_topic. heading_topic là nhãn/chủ đề mở mục nội dung. table_or_chart_label là nhãn bảng, nhãn cột, metric, " +
        "legend, số liệu hoặc caption biểu đồ dù có in đậm. body_sentence là câu/vế văn xuôi.\n" +
        "Chỉ dùng heading_topic khi crop có bằng chứng nhìn thấy. Nếu crop không đủ, mâu thuẫn, hoặc text parser khác ảnh, trả uncertain.\n" +
        "visualEvidence là BẮT BUỘC và CHỈ dùng các tag sau: standalone_label, section_boundary, " +
        "distinct_heading_style, prose_sentence, continues_paragraph, inside_table_grid, numeric_column, chart_caption, " +
        "repeated_running_header, page_furniture, logo_or_artifact, insufficient_visual_evidence. Chọn tag thực sự nhìn thấy, " +
        "không đoán. heading_topic cần ít nhất standalone_label, section_boundary hoặc distinct_heading_style. " +
        "table_or_chart_label cần inside_table_grid, numeric_column hoặc chart_caption.\n" +
        "evidence là ghi chú ngắn để audit, không phải cơ sở quyết định; confidence chỉ tham khảo và không được dùng để bù visualEvidence. " +
        "Không trả opens_content hay suy luận ngữ nghĩa: đó là semanticEvidence của text analyst, không phải visual evidence.\n" +
        "Nếu role là heading_topic hoặc document_title, headingSpan là BẮT BUỘC: offset start/end trên đúng chuỗi candidate parser đưa, " +
        "0 <= start < end <= candidateLength. Nếu cả candidate là heading thì dùng start=0,end=candidateLength; " +
        "TUYỆT ĐỐI không dùng span rỗng 0,0 và không trả headingText.\n" +
        "Trả lời đúng một JSON object, không thêm lời dẫn: " +
        "{\"id\":\"" + block.Id + "\",\"role\":\"heading_topic|document_title|body_sentence|table_or_chart_label|decorative_noise|uncertain\",\"headingSpan\":{\"start\":0,\"end\":candidateLength},\"confidence\":0.0,\"visualEvidence\":[\"...\"],\"evidence\":\"ghi chú ngắn\"}\n" +
        "Text do parser đọc được chỉ để định danh candidate, không phải dữ liệu để bạn sửa hay bổ sung:\n" +
        "candidateLength=" + block.Text.Length + "; candidate: " + block.Text + "\n" +
        "neighbor context (không được phân loại các id này):\n" +
        string.Join("\n", context.Where(x => x.Id != block.Id).Select(x => "- " + x.DisplayText));

    internal static string BuildBatchQuestion(
        IReadOnlyList<PdfSemanticBlock> blocks,
        IReadOnlyList<IReadOnlyList<PdfSemanticBlock>> contexts) =>
        "Bạn là visual analyst bị giới hạn cho trích xuất outline PDF. Payload có nhiều ảnh crop ĐỘC LẬP, " +
        "theo đúng thứ tự các candidate dưới đây; mỗi crop chứa candidate và tối đa hai dòng liền trước/sau.\n" +
        "Chỉ phân loại các id đã cho. Không tạo block, không nối text, không lập outline, không gán cấp/cha-con.\n" +
        "role CHỈ được là heading_topic, document_title, body_sentence, table_or_chart_label, decorative_noise, uncertain. " +
        "heading_topic là nhãn/chủ đề mở mục; table_or_chart_label là nhãn bảng, metric, legend hoặc caption.\n" +
        "Mỗi block phải có visualEvidence từ vocabulary: standalone_label, section_boundary, distinct_heading_style, " +
        "prose_sentence, continues_paragraph, inside_table_grid, numeric_column, chart_caption, repeated_running_header, " +
        "page_furniture, logo_or_artifact, insufficient_visual_evidence. confidence chỉ tham khảo, không được bù tag thiếu.\n" +
        "heading_topic/document_title bắt buộc headingSpan {start,end} offset trên candidate với 0 <= start < end <= candidateLength; " +
        "nếu toàn bộ candidate là heading dùng {start:0,end:candidateLength}; các role khác để headingSpan null. " +
        "Trả lời đúng JSON: {\"blocks\":[{\"id\":\"...\",\"role\":\"...\",\"headingSpan\":{\"start\":0,\"end\":0},\"confidence\":0.0,\"visualEvidence\":[\"...\"],\"evidence\":\"ghi chú ngắn\"}]}.\n" +
        string.Join("\n\n", blocks.Select((block, index) =>
            $"crop {index + 1}; id={block.Id}; candidateLength={block.Text.Length}; candidate: {block.Text}\n" +
            "neighbors (không được phân loại):\n" +
            string.Join("\n", contexts[index].Where(x => x.Id != block.Id).Select(x => "- " + x.DisplayText))));

    private static IReadOnlyList<PdfSemanticBlock> NeighborContext(
        PdfSemanticBlock block,
        IReadOnlyList<PdfSemanticBlock> catalog)
    {
        var ordered = catalog.OrderBy(x => x.Page).ThenByDescending(x => x.TopY)
            .ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
        var index = Array.FindIndex(ordered, x => x.Id == block.Id);
        if (index < 0) return [block];
        return ordered.Skip(Math.Max(0, index - 2)).Take(5).ToArray();
    }

    private static byte[] RenderContextPng(string pdfPath, IReadOnlyList<PdfSemanticBlock> context, int dpi)
    {
        var panels = context.GroupBy(block => block.Page).OrderBy(group => group.Key)
            .Select(group => PdfRegionRasterizer.RenderCropPng(
                pdfPath,
                group.Key,
                Math.Max(0, group.Min(block => block.Left) - PaddingX),
                Math.Max(0, group.Min(block => block.BottomY) - PaddingY),
                group.Max(block => block.Right) + PaddingX,
                group.Max(block => block.TopY) + PaddingY,
                dpi))
            .ToArray();
        if (panels.Length == 1) return panels[0];

        var decoded = new SKBitmap[panels.Length];
        for (var i = 0; i < panels.Length; i++)
            decoded[i] = SKBitmap.Decode(panels[i]) ?? throw new InvalidOperationException("Không giải mã được PDF context panel.");
        try
        {
            const int gap = 16;
            var width = decoded.Max(image => image.Width);
            var height = decoded.Sum(image => image.Height) + gap * (decoded.Length - 1);
            using var surface = SKSurface.Create(new SKImageInfo(width, height))
                ?? throw new InvalidOperationException("Không tạo được ảnh PDF context montage.");
            surface.Canvas.Clear(SKColors.White);
            var top = 0;
            foreach (var image in decoded)
            {
                surface.Canvas.DrawBitmap(image, 0, top, new SKSamplingOptions());
                top += image.Height + gap;
            }

            using var snapshot = surface.Snapshot();
            using var encoded = snapshot.Encode(SKEncodedImageFormat.Png, 100);
            return encoded.ToArray();
        }
        finally
        {
            foreach (var image in decoded) image?.Dispose();
        }
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
            var role = ParseRole(roleText);
            var evidenceTags = ParseVisualEvidenceTags(root);
            var headingSpan = ParseHeadingSpan(root);
            if (!HasUsableEvidence(evidence) || !HasRoleEvidence(role, evidenceTags))
                return new PdfVisualBlockDecision(expectedId, PdfBlockRole.Uncertain, 0, "unusable-evidence-tags", raw, evidenceTags, headingSpan);

            return new PdfVisualBlockDecision(expectedId, role, confidence, evidence, raw, evidenceTags, headingSpan);
        }
        catch (JsonException)
        {
            return new PdfVisualBlockDecision(expectedId, PdfBlockRole.Uncertain, 0, "invalid-json", raw);
        }
    }

    internal static IReadOnlyList<PdfVisualBlockDecision> ParseBatchDecisions(
        IReadOnlyList<PdfSemanticBlock> expectedBlocks,
        string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(ExtractJsonObject(raw));
            if (!doc.RootElement.TryGetProperty("blocks", out var items) || items.ValueKind != JsonValueKind.Array)
                throw new JsonException("Missing blocks array.");
            var byId = items.EnumerateArray()
                .Where(item => item.TryGetProperty("id", out var id) && !string.IsNullOrWhiteSpace(id.GetString()))
                .ToDictionary(item => item.GetProperty("id").GetString()!, item => item.GetRawText(), StringComparer.Ordinal);
            return expectedBlocks.Select(block => byId.TryGetValue(block.Id, out var item)
                ? ParseDecision(block.Id, item)
                : new PdfVisualBlockDecision(block.Id, PdfBlockRole.Uncertain, 0, "missing-batch-decision", raw)).ToArray();
        }
        catch (JsonException)
        {
            return expectedBlocks.Select(block => new PdfVisualBlockDecision(
                block.Id, PdfBlockRole.Uncertain, 0, "invalid-batch-json", raw)).ToArray();
        }
    }

    private static PdfBlockRole ParseRole(string role) =>
        role.Trim().ToLowerInvariant() switch
        {
            "heading_topic" or "heading" or "topic" => PdfBlockRole.HeadingTopic,
            "document_title" or "cover_title" => PdfBlockRole.DocumentTitle,
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

    private static readonly HashSet<string> AllowedVisualEvidenceTags = new(StringComparer.Ordinal)
    {
        "standalone_label", "section_boundary", "distinct_heading_style",
        "prose_sentence", "continues_paragraph", "inside_table_grid", "numeric_column", "chart_caption",
        "repeated_running_header", "page_furniture", "logo_or_artifact", "insufficient_visual_evidence",
    };

    private static IReadOnlyList<string> ParseVisualEvidenceTags(JsonElement root)
    {
        if (!root.TryGetProperty("visualEvidence", out var tags) || tags.ValueKind != JsonValueKind.Array) return [];
        return tags.EnumerateArray()
            .Where(tag => tag.ValueKind == JsonValueKind.String)
            .Select(tag => tag.GetString()?.Trim().ToLowerInvariant() ?? "")
            .Where(AllowedVisualEvidenceTags.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static SourceTextSpan? ParseHeadingSpan(JsonElement root)
    {
        if (!root.TryGetProperty("headingSpan", out var span) || span.ValueKind != JsonValueKind.Object ||
            !span.TryGetProperty("start", out var start) || !span.TryGetProperty("end", out var end) ||
            !start.TryGetInt32(out var startOffset) || !end.TryGetInt32(out var endOffset)) return null;
        return new SourceTextSpan(startOffset, endOffset);
    }

    private static bool HasRoleEvidence(PdfBlockRole role, IReadOnlyList<string> tags) => role switch
    {
        PdfBlockRole.HeadingTopic or PdfBlockRole.DocumentTitle => tags.Any(tag => tag is "standalone_label" or "section_boundary" or "distinct_heading_style"),
        PdfBlockRole.BodySentence => tags.Any(tag => tag is "prose_sentence" or "continues_paragraph"),
        PdfBlockRole.TableOrChartLabel => tags.Any(tag => tag is "inside_table_grid" or "numeric_column" or "chart_caption"),
        PdfBlockRole.DecorativeNoise => tags.Any(tag => tag is "repeated_running_header" or "page_furniture" or "logo_or_artifact"),
        PdfBlockRole.Uncertain => tags.Contains("insufficient_visual_evidence"),
        _ => false,
    };

    private static string ExtractJsonObject(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        return start >= 0 && end > start ? raw[start..(end + 1)] : raw;
    }
}
