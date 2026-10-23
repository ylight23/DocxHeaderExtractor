using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DocxHeaderExtractor.App;
using DocxHeaderExtractor.DocumentProcessing.Output;
using DocxHeaderExtractor.Sources.Detect;

namespace DocxHeaderExtractor.Web.Endpoints;

/// <summary>
/// <c>POST /api/extract</c>: runs one uploaded file through the V6 route and streams the progress, the model replies (when asked) and the
/// result as NDJSON events.
/// </summary>
internal static class ExtractEndpoint
{
    public static async Task HandleAsync(
        HttpRequest req,
        HttpResponse res,
        WritebackStore writebackStore,
        ModelProviderFactory modelProvider,
        CancellationToken ct)
    {
        if (!req.HasFormContentType)
        {
            res.StatusCode = StatusCodes.Status400BadRequest;
            await res.WriteAsync("Cần multipart/form-data.", ct);
            return;
        }

        IFormCollection form;
        try
        {
            form = await req.ReadFormAsync(ct);
        }
        catch (BadHttpRequestException ex)
        {
            // Vượt trần thì Kestrel huỷ kết nối; không bắt ở đây thì phía trình duyệt chỉ thấy
            // ERR_CONNECTION_RESET và không có cách nào biết vì sao.
            res.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await res.WriteAsync($"File quá lớn (trần {Uploads.MaxBytes / (1024 * 1024)} MB): {ex.Message}", ct);
            return;
        }
        catch (InvalidDataException)
        {
            res.StatusCode = StatusCodes.Status400BadRequest;
            await res.WriteAsync("Multipart upload không hợp lệ.", ct);
            return;
        }

        var upload = form.Files["file"];
        if (upload is null || upload.Length == 0)
        {
            res.StatusCode = StatusCodes.Status400BadRequest;
            await res.WriteAsync("Chưa chọn file.", ct);
            return;
        }

        // NDJSON: mỗi dòng một sự kiện, để trình duyệt hiện tiến độ trong lúc mô hình còn chạy.
        res.ContentType = "application/x-ndjson; charset=utf-8";
        res.Headers.CacheControl = "no-store";

        var events = Channel.CreateUnbounded<object>();

        async Task EmitAsync(object payload)
        {
            var line = JsonSerializer.Serialize(payload, WebJson.Options);
            await res.WriteAsync(line + "\n", Encoding.UTF8, ct);
            await res.Body.FlushAsync(ct);
        }

        var work = Path.Combine(Path.GetTempPath(), "dhx-ui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var inputPath = Path.Combine(work, Uploads.SafeName(upload.FileName));

        try
        {
            await using (var fs = File.Create(inputPath))
                await upload.CopyToAsync(fs, ct);

            var inspection = SourceInspection.Read(inputPath, ct);
            await EmitAsync(new { type = "source", source = inspection });
            if (!inspection.Supported)
            {
                await EmitAsync(new { type = "error", message = inspection.Message });
                return;
            }
            var provider = modelProvider();
            var options = RequestOptions.Build(form, provider, out var problem);
            if (problem is not null)
            {
                await EmitAsync(new { type = "error", message = problem });
                return;
            }

            // Production Web does not publish free-form diagnostics (which may contain source text or completions). Structured harness
            // events remain visible.
            options.Log = _ => { };

            // Một quyết định duy nhất về định dạng, đọc từ byte, dùng chung cho mọi bước phía sau.
            var uploadedType = SourceDetector.Detect(inputPath);

            // Chạy thẳng tuyến V6 (agent PDF/DOCX/scan), giống CLI: không qua harness. Tiến độ stream dưới dạng sự kiện
            // "agent" để giữ nhật ký trên giao diện.
            // Phản hồi của mô hình chỉ được giữ và trả về khi người dùng tự bật "Hiện phản hồi của mô hình".
            var showRaw = form["showRaw"].ToString() is "1" or "true" or "on";
            // Each reply is written to the stream as soon as the model returns it; units run in parallel, so replies interleave.
            var runner = new DocumentRunner(options, provider,
                showRaw ? r => events.Writer.TryWrite(ReplyEvent(r)) : null);

            // Đích ghi do server đặt bên trong thư mục tạm của request, không bao giờ lấy từ form:
            // một đường dẫn do client chỉ định là đường để ghi đè file bất kỳ trên máy chủ.
            var wantsWriteback = form["writeback"].ToString() is "1" or "true" or "on";
            var writebackTarget = wantsWriteback
                ? Path.Combine(work, Path.GetFileNameWithoutExtension(Uploads.SafeName(upload.FileName)) + ".outline.docx")
                : null;

            var runId = Guid.NewGuid();
            void Stage(string stage, string kind, string message) => events.Writer.TryWrite(new
            {
                type = "agent",
                runId,
                timestamp = DateTimeOffset.UtcNow,
                stage,
                kind,
                message,
            });
            var run = Task.Run(async () =>
            {
                Stage("extract", "Started", "Bắt đầu trích xuất tài liệu.");
                var execution = await runner.ExtractAsync(inputPath, ct: ct);
                var outline = execution.Outline;
                Stage("extract", outline.RouteAudit?.SemanticLane?.Status == "partial" ? "Partial" : "Completed",
                    $"Tuyến {outline.DeterministicRoute} trả {outline.Headings.Count} heading từ {outline.ParagraphCount} đơn vị nguồn.");
                OutlineWritebackReport? written = null;
                if (writebackTarget is not null && outline.DeterministicRoute == "scan-v6-selected-lines")
                    Stage("writeback", "Skipped", "Bản scan có tọa độ ảnh; chưa hỗ trợ ghi heading vào đoạn Word.");
                else if (writebackTarget is not null)
                {
                    Stage("writeback", "Started", "Ghi outline vào bản sao.");
                    written = DocumentRunner.WriteOutline(inputPath, outline, writebackTarget, options.Extraction);
                    Stage("writeback", "Completed", $"Đã ghi {written.Applied} heading, bỏ qua {written.Skipped} mục.");
                }
                return (Outline: outline, Execution: execution, Writeback: written);
            }, ct);
            _ = run.ContinueWith(_ => events.Writer.TryComplete(), TaskScheduler.Default);

            await foreach (var evt in events.Reader.ReadAllAsync(ct))
                await EmitAsync(evt);

            var (outline, execution, writeback) = await run;

            // Đọc vào bộ nhớ trước khi finally xoá thư mục tạm; link chỉ tải được đúng một lần.
            string? download = null;
            if (writeback is { } written && File.Exists(written.OutputPath))
            {
                writebackStore.Put(
                    runId,
                    await File.ReadAllBytesAsync(written.OutputPath, ct),
                    Path.GetFileName(written.OutputPath));
                download = $"/api/outline/{runId}.docx";
            }

            var requiresReview = outline.Headings.Count(h =>
                h.DecisionStatus == HeadingDecisionStatus.RequiresReview || h.Disputed);
            await EmitAsync(new
            {
                type = "result",
                outline = WebPipelineProjection.SafeOutline(outline, WebJson.Options),
                pipeline = WebPipelineProjection.Project(runId, outline.RouteAudit?.SemanticLane?.Status == "partial" ? "Partial" : "Completed", outline, execution, execution.Result.DocumentIdentity.SourceKind),
                stats = Stats.From(outline),
                sourceType = uploadedType.ToString().ToLowerInvariant(),
                sourceInspection = inspection,
                agent = new
                {
                    runId,
                    outcome = outline.RouteAudit?.SemanticLane?.Status == "partial" ? "Partial" : "Completed",
                    steps = writeback is null ? 1 : 2,
                    repairAttempts = 0,
                    requiresReview,
                    message = $"Trích xuất {outline.Headings.Count} heading từ {outline.ParagraphCount} đoạn."
                        + (requiresReview == 0 ? "" : $" Còn {requiresReview} mục cần xem lại."),
                    writebackApplied = writeback?.Applied,
                    download,
                },
            });
        }
        catch (OperationCanceledException)
        {
            // Người dùng đóng tab hoặc bấm huỷ — không còn gì để gửi.
        }
        catch (Exception ex)
        {
            // A refusal about the input format names only the format; it carries no provider payload, so it can be shown.
            var message = ex is NotSupportedException or FileNotFoundException
                ? ex.Message
                : $"Execution failed ({ex.GetType().Name}). Provider payload and source diagnostics are withheld.";
            try { await EmitAsync(new { type = "error",
                message,
                pipeline = new { schemaVersion = "web-pipeline-v2", availability = "unavailable", outcome = "Failed",
                    stages = new[] { new WebPipelineStage("execution", "failed", "Server exception; sub-stages not-recorded") } } }); }
            catch (Exception) { /* kết nối đã đứt */ }
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    // One model reply as a stream event: what the model streamed, the tool calls it made, the tool results it was given, and its content.
    static object ReplyEvent(ModelReplyView r) => new
    {
        type = "reply",
        unit = r.Unit,
        turn = r.Turn,
        reasoning = r.Reasoning,
        finishReason = r.FinishReason,
        content = r.Content,
        toolCalls = r.ToolCalls.Select(c => new { name = c.Name, arguments = c.Arguments }).ToArray(),
        toolResults = r.ToolResults.Select(x => new { toolCallId = x.ToolCallId, content = x.Content }).ToArray(),
    };
}
