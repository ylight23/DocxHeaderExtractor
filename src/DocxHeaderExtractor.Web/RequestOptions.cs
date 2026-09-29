using System.Globalization;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Web;

/// <summary>Dựng <see cref="PipelineOptions"/> từ form của giao diện.</summary>
public static class RequestOptions
{
    public static PipelineOptions Build(IFormCollection form, out string? problem, out InferenceProviderSelection provider)
    {
        problem = null;
        provider = new InferenceProviderSelection();
        var o = new PipelineOptions();

        o.DisableLlm = Flag(form, "noLlm");

        o.ShowRawOutput = Flag(form, "showRaw");

        if (o.DisableLlm) return o;

        provider.Backend = form["backend"].ToString().ToLowerInvariant() switch
        {
            "openrouter" => InferenceBackend.OpenRouter,
            "lmstudio" => InferenceBackend.LmStudio,
            _ => InferenceBackend.Local,
        };

        if (provider.Backend == InferenceBackend.OpenRouter)
        {
            provider.Remote = RemoteInferenceOptions.FromEnvironment("openrouter");
            if (string.IsNullOrWhiteSpace(provider.Remote.ApiKey))
                problem = "Backend OpenRouter chưa được cấu hình OPENROUTER_API_KEY trên server.";
            return o;
        }

        if (provider.Backend == InferenceBackend.LmStudio)
        {
            provider.Remote = RemoteInferenceOptions.FromEnvironment("lmstudio");
            var selectedModel = form["lmStudioModel"].ToString().Trim();
            if (!string.IsNullOrEmpty(selectedModel)) provider.Remote.Model = selectedModel;
            try
            {
                provider.Remote.Validate();
            }
            catch (InvalidOperationException ex)
            {
                problem = ex.Message;
                return o;
            }

            return o;
        }

        var model = form["model"].ToString();
        if (string.IsNullOrWhiteSpace(model))
        {
            var first = ModelCatalog.List().FirstOrDefault();
            if (first is null)
            {
                problem = "Không tìm thấy file .gguf nào trong thư mục models. "
                        + "Dùng LM Studio hoặc OpenRouter, hoặc bật \"Không gọi mô hình\".";
                return o;
            }
            model = first.Path;
        }

        if (!File.Exists(model))
        {
            problem = $"Không tìm thấy file mô hình: {model}";
            return o;
        }

            provider.LocalModel.ModelPath = model;
            if (Number(form, "ctx") is { } ctx and >= 1024)
            {
                provider.LocalModel.ContextSize = (uint)ctx;
                provider.LocalModel.AutoContextSize = false;
            }

        // Bản CPU bỏ qua giá trị này; bản dựng với -p:UseVulkan=true / -p:UseCuda=true thì
        // 0 nghĩa là vẫn chạy CPU, nên không truyền xuống là giao diện không bao giờ dùng GPU.
        if (Number(form, "gpuLayers") is { } gl and >= 0)
            provider.LocalModel.GpuLayerCount = (int)gl;
        return o;
    }

    private static bool Flag(IFormCollection form, string key) =>
        form[key].ToString() is "1" or "true" or "on";

    private static double? Number(IFormCollection form, string key) =>
        double.TryParse(form[key].ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;
}
