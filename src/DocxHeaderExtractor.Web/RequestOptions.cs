using System.Globalization;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Web;

/// <summary>Dựng <see cref="PipelineOptions"/> từ form của giao diện.</summary>
public static class RequestOptions
{
    public static PipelineOptions Build(IFormCollection form, out string? problem)
    {
        problem = null;
        var o = new PipelineOptions();

        o.Extraction.UseLexicalRules = !Flag(form, "structuralOnly");

        // Cắt tiêu đề nằm LỌT GIỮA paragraph — cần cho tài liệu chuyển từ PDF, nơi cả trang bị gộp
        // vào một <w:p>. Đo trên corpus 95 file: 83 file thuộc dạng này, và 4.590/6.858 mục (67%)
        // có ranh giới heading nằm giữa đoạn (§45.2). Mặc định TẮT vì nó phá giả định "mỗi đoạn
        // nhiều nhất một mục" mà mọi đáp án trong keys/ đang dựa vào.
        o.Extraction.SplitMergedParagraphs = Flag(form, "splitMerged");

        // Ba bộ dựng TẤT ĐỊNH — đọc một dữ kiện cấu trúc cho cả tài liệu, không điểm số, không
        // ngưỡng. Chúng thay thế hẳn đường chấm điểm chứ không bổ sung vào nó, nên loại trừ nhau.
        o.StyleDeclaredOutline = Flag(form, "styleOutline");
        o.NumberingDeclaredOutline = Flag(form, "numberingOutline");
        o.AdministrativeDeclaredOutline = Flag(form, "adminOutline");
        if (Number(form, "threshold") is { } th) o.Extraction.CandidateThreshold = th;

        o.DisableLlm = Flag(form, "noLlm");

        // Ba cờ này không phụ thuộc backend nên đặt một lần ở đây. Trước đây chúng được lặp lại ở
        // cuối mỗi nhánh backend, và mỗi bản sao là một cơ hội để một nhánh lệch khỏi các nhánh kia.
        //
        // KHÔNG suy SkipStyledCandidates từ TrustStyles. Dòng cũ mã hoá đúng lập luận mà chính
        // PipelineOptions.SkipStyledCandidates đã bác bỏ bằng số đo: "tin style thì khỏi hỏi model
        // về chúng" chỉ đúng nếu câu trả lời của mô hình cố định, mà bỏ câu hỏi ra khỏi khối lại
        // làm đổi thành phần khối và đổi câu trả lời cho các đoạn CÒN LẠI — precision 100% → 94,1%.
        // Giao diện cũng không có ô nào cho cờ này, nên người dùng web không hề biết mình đang chạy
        // ở chế độ đó. Để mặc định của core quyết định.
        o.TrustStyles = !Flag(form, "noTrustStyles");
        o.ShowRawOutput = Flag(form, "showRaw");
        o.TwoPass = !o.DisableLlm && Flag(form, "twoPass");

        if (o.DisableLlm) return o;

        o.Backend = form["backend"].ToString().ToLowerInvariant() switch
        {
            "openrouter" => InferenceBackend.OpenRouter,
            "lmstudio" => InferenceBackend.LmStudio,
            _ => InferenceBackend.Local,
        };

        // Cả hai backend RPC đều không bị VRAM local ràng buộc nên dùng profile 8K/5K đã đo cho
        // Qwen. Thiếu dòng này thì ngân sách rơi về 2200 của bản local và tài liệu bị xé thành
        // hàng chục khối — 13 ứng viên thành 27 lượt RPC.
        if (o.Backend == InferenceBackend.OpenRouter)
        {
            o.OpenRouter = DocxHeaderExtractor.Core.Llm.OpenRouterOptions.FromEnvironment();
            o.Chunking.UseRemoteProfile();
            if (string.IsNullOrWhiteSpace(o.OpenRouter.ApiKey))
                problem = "Backend OpenRouter chưa được cấu hình OPENROUTER_API_KEY trên server.";
            return o;
        }

        if (o.Backend == InferenceBackend.LmStudio)
        {
            o.LmStudio = DocxHeaderExtractor.Core.Llm.LmStudioOptions.FromEnvironment();
            var selectedModel = form["lmStudioModel"].ToString().Trim();
            if (!string.IsNullOrEmpty(selectedModel)) o.LmStudio.Model = selectedModel;
            try
            {
                o.LmStudio.Validate();
            }
            catch (InvalidOperationException ex)
            {
                problem = ex.Message;
                return o;
            }

            o.Chunking.UseRemoteProfile();
            return o;
        }

        var model = form["model"].ToString();
        if (string.IsNullOrWhiteSpace(model))
        {
            var first = ModelCatalog.List().FirstOrDefault();
            if (first is null)
            {
                problem = "Không tìm thấy file .gguf nào trong thư mục models. "
                        + "Bật \"Chỉ dùng luật OpenXML\" để chạy không cần mô hình.";
                return o;
            }
            model = first.Path;
        }

        if (!File.Exists(model))
        {
            problem = $"Không tìm thấy file mô hình: {model}";
            return o;
        }

            o.Llama.ModelPath = model;
            if (Number(form, "ctx") is { } ctx and >= 1024)
                o.Llama.ContextSize = (uint)ctx;
            else
                o.Llama.ContextSize = ModelCatalog.List().FirstOrDefault(m => m.Path == model)?.SuggestedCtx ?? 4096u;

        if (Number(form, "chunkCandidates") is { } cc and >= 2 and <= 64)
            o.Chunking.MaxCandidatesPerChunk = (int)cc;

        // Bản CPU bỏ qua giá trị này; bản dựng với -p:UseVulkan=true / -p:UseCuda=true thì
        // 0 nghĩa là vẫn chạy CPU, nên không truyền xuống là giao diện không bao giờ dùng GPU.
        if (Number(form, "gpuLayers") is { } gl and >= 0)
            o.Llama.GpuLayerCount = (int)gl;

        // Chốt profile ở server. Trình duyệt cũ có thể vẫn gửi 4096; không để request đi
        // tới bước nạp model rồi mới vỡ vì tổng ngân sách lớn hơn context.
        o.Llama.ApplyRecommendedModelProfile(o.Chunking);
        return o;
    }

    private static bool Flag(IFormCollection form, string key) =>
        form[key].ToString() is "1" or "true" or "on";

    private static double? Number(IFormCollection form, string key) =>
        double.TryParse(form[key].ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;
}
