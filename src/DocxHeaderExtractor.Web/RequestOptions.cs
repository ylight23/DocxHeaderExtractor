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
        if (Number(form, "threshold") is { } th) o.Extraction.CandidateThreshold = th;

        o.DisableLlm = Flag(form, "noLlm");
        if (!o.DisableLlm)
        {
            o.Backend = form["backend"].ToString().ToLowerInvariant() switch
            {
                "openrouter" => InferenceBackend.OpenRouter,
                "lmstudio" => InferenceBackend.LmStudio,
                _ => InferenceBackend.Local,
            };

            if (o.Backend == InferenceBackend.OpenRouter)
            {
                o.OpenRouter = DocxHeaderExtractor.Core.Llm.OpenRouterOptions.FromEnvironment();
                // Chunking vẫn thuộc pipeline chung; RPC không bị giới hạn VRAM local nên dùng
                // profile 8K/5K đã nghiên cứu cho Qwen.
                o.Chunking.UseRemoteProfile();
                if (string.IsNullOrWhiteSpace(o.OpenRouter.ApiKey))
                {
                    problem = "Backend OpenRouter chưa được cấu hình OPENROUTER_API_KEY trên server.";
                    return o;
                }
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

                // Cùng lý do như OpenRouter: LM Studio là RPC, không bị VRAM local ràng buộc. Thiếu
                // dòng này thì ngân sách rơi về 2200 của bản local và tài liệu bị xé thành hàng
                // chục khối — 13 ứng viên thành 27 lượt RPC.
                o.Chunking.UseRemoteProfile();
                o.TrustStyles = !Flag(form, "noTrustStyles");
                o.SkipStyledCandidates = o.TrustStyles;
                o.ShowRawOutput = Flag(form, "showRaw");
                o.TwoPass = Flag(form, "twoPass");
                return o;
            }

            if (o.Backend == InferenceBackend.OpenRouter)
            {
                o.TrustStyles = !Flag(form, "noTrustStyles");
                o.SkipStyledCandidates = o.TrustStyles;
                o.ShowRawOutput = Flag(form, "showRaw");
                o.TwoPass = Flag(form, "twoPass");
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
                o.Llama.ContextSize = ModelCatalog.List().FirstOrDefault(m => m.Path == model)?.SuggestedCtx
                    ?? DocxHeaderExtractor.Core.Llm.LlamaOptions.SuggestedContextForModel(model);

            if (Number(form, "chunkCandidates") is { } cc and >= 2 and <= 64)
                o.Chunking.MaxCandidatesPerChunk = (int)cc;

            // Bản CPU bỏ qua giá trị này; bản dựng với -p:UseVulkan=true / -p:UseCuda=true thì
            // 0 nghĩa là vẫn chạy CPU, nên không truyền xuống là giao diện không bao giờ dùng GPU.
            if (Number(form, "gpuLayers") is { } gl and >= 0)
                o.Llama.GpuLayerCount = (int)gl;

            // Chốt profile ở server. Trình duyệt cũ có thể vẫn gửi 4096; không để request đi
            // tới bước nạp model rồi mới vỡ vì tổng ngân sách lớn hơn context.
            o.Llama.ApplyRecommendedModelProfile(o.Chunking);
        }

        o.TrustStyles = !Flag(form, "noTrustStyles");
        o.SkipStyledCandidates = o.TrustStyles;
        o.ShowRawOutput = Flag(form, "showRaw");
        o.TwoPass = !o.DisableLlm && Flag(form, "twoPass");
        return o;
    }

    private static bool Flag(IFormCollection form, string key) =>
        form[key].ToString() is "1" or "true" or "on";

    private static double? Number(IFormCollection form, string key) =>
        double.TryParse(form[key].ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;
}
