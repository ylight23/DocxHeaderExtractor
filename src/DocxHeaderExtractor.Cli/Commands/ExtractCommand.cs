using System.Text;
using DocxHeaderExtractor.App;
using DocxHeaderExtractor.DocumentProcessing.Output.Outline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Cli.Commands;

/// <summary><c>dhx extract</c>, the default command: the outline of each input file, printed or written to <c>--output</c>.</summary>
internal static class ExtractCommand
{
    public static async Task<int> RunAsync(CommandLineOptions o, CancellationToken ct)
    {
        if (!o.Pipeline.DisableLlm && o.Provider.Backend == InferenceBackend.Local &&
            string.IsNullOrWhiteSpace(o.Provider.LocalModel.ModelPath))
        {
            var found = ModelLocator.Locate();
            if (found is null)
            {
                Console.Error.WriteLine(
                    """
                    Chưa có mô hình. Chỉ định bằng --model <đường-dẫn.gguf>, đặt biến môi trường DHX_MODEL,
                    hoặc đặt file .gguf vào thư mục ./models. Xem models/README.md để tải
                    Llama-3.2-3B-Instruct-Q4_K_M.gguf. Dùng --no-llm nếu chỉ muốn chạy bằng luật OpenXML.
                    """);
                return 2;
            }
            o.Provider.LocalModel.ModelPath = found;
        }

        var files = Inputs.Expand(o.Inputs);
        if (files.Count == 0)
        {
            Console.Error.WriteLine("Không tìm thấy file nào khớp đầu vào.");
            return 2;
        }
        if (files.Count > 1 && o.WritebackPath is not null)
        {
            Console.Error.WriteLine("Chỉ dùng --write-docx khi xử lý đúng một tài liệu.");
            return 2;
        }

        // Chạy thẳng tuyến V6 (PDF/DOCX). Không qua harness: không có consent gửi dữ liệu ra ngoài,
        // không có validator, không có guardrail đích ghi (chủ dự án chấp nhận, 2026-10-15).
        o.Pipeline.ScanJobs = o.Jobs;
        o.Pipeline.ScanVision = o.Vision;
        o.Pipeline.ScanReuseSelection = !o.FreshSelection;
        using var runner = new DocumentRunner(o.Pipeline, new InferenceTransportFactory(o.Provider),
            o.ShowRawOutput ? r => ModelReplyPrinter.Print(r) : null);
        var outputs = new List<string>();
        int failed = 0;
        int partial = 0;

        foreach (var file in files)
        {
            if (!o.Quiet) Console.Error.WriteLine($"» {Path.GetFileName(file)}");
            try
            {
                var execution = await runner.ExtractAsync(file, ct: ct);
                var outline = execution.Outline;
                var partialResult = outline.DeterministicRoute == "scan-v6-selected-lines" && outline.RouteAudit?.SemanticLane?.Status == "partial";
                if (partialResult)
                {
                    Console.Error.WriteLine("  Scan trả kết quả một phần: " + outline.RouteAudit?.SemanticLane?.FailureClass);
                }
                if (!o.Quiet)
                    Console.Error.WriteLine($"  {outline.Headings.Count} heading từ {outline.ParagraphCount} đoạn.");
                if (!o.Quiet && outline.RouteAudit?.Scan is { } scan)
                {
                    Console.Error.WriteLine($"  scan: chép {scan.TranscriptionsAccepted}/{scan.TranscribedLines} dòng dùng được, " +
                        $"tiền xử lý {scan.PreflightCalls} POST {scan.PreflightCostUsd:0.0000} USD, " +
                        $"hội thoại {scan.ConversationCalls} POST {scan.ConversationCostUsd:0.0000} USD, dựng cấp {scan.PlacementCalls} lần");
                    Console.Error.WriteLine("  thời gian: " + string.Join(", ", scan.StageSeconds.Select(kv => $"{kv.Key} {kv.Value:0.0}s")));
                }

                if (o.WritebackPath is not null && outline.DeterministicRoute == "scan-v6-selected-lines")
                {
                    Console.Error.WriteLine("  Không ghi outline vào Word: bản scan chưa có các đoạn văn bản để gắn heading. Kết quả trích xuất vẫn được giữ.");
                    partialResult = true;
                }
                else if (o.WritebackPath is not null)
                {
                    var writeback = DocumentRunner.WriteOutline(
                        file, outline, o.WritebackPath, o.Pipeline.Extraction,
                        applyHeadingStyles: o.WritebackHeadingStyles, overwrite: o.WritebackOverwrite);
                    if (!o.Quiet)
                        Console.Error.WriteLine($"  Đã ghi {writeback.Applied} heading, bỏ qua {writeback.Skipped} mục: {Path.GetFileName(writeback.OutputPath)}");
                }
                outputs.Add(OutlineFormatter.Format(outline, o.Format));
                if (partialResult) partial++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"  {ex.Message}");
                if (o.Verbose) Console.Error.WriteLine(ex.ToString());
                failed++;
            }
        }

        var text = string.Join(Environment.NewLine, outputs);
        if (o.OutputPath is { } path)
        {
            // Không tài liệu nào qua được thì không ghi. Mã thoát đã khác 0 từ trước, nhưng lần chạy
            // hỏng vẫn tạo file rỗng — và nếu đường dẫn đó đang giữ kết quả lần trước thì nó vừa bị
            // xoá. Script đọc file thay vì đọc mã thoát sẽ hiểu thành "tài liệu không có heading nào".
            if (outputs.Count == 0)
                Console.Error.WriteLine(
                    $"Không ghi {path}: cả {failed} tài liệu đều lỗi nên không có kết quả nào để ghi.");
            else
            {
                await File.WriteAllTextAsync(path, text, new UTF8Encoding(false), ct);
                if (!o.Quiet)
                    Console.Error.WriteLine(failed == 0 && partial == 0
                        ? $"Đã ghi: {path}"
                        : $"Đã ghi {outputs.Count}/{files.Count} tài liệu vào {path}; {partial} kết quả một phần; {failed} tài liệu lỗi không có trong file.");
            }
        }
        else if (outputs.Count > 0)
        {
            Console.WriteLine(text);
        }

        return failed == 0 && partial == 0 ? 0 : 1;
    }
}
