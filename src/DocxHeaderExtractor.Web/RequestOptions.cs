using DocxHeaderExtractor.Contracts.Provider;
using DocxHeaderExtractor.DocumentProcessing.Harness;

namespace DocxHeaderExtractor.Web;

/// <summary>Dựng <see cref="PipelineOptions"/> từ form của giao diện.</summary>
public static class RequestOptions
{
    /// <param name="problem">Why the run cannot start: it calls the model and the provider has no key.</param>
    public static PipelineOptions Build(IFormCollection form, IModelProvider provider, out string? problem)
    {
        problem = null;
        var o = new PipelineOptions { DisableLlm = Flag(form, "noLlm") };
        if (!o.DisableLlm && string.IsNullOrWhiteSpace(provider.CreateRoute().ApiKey))
            problem = "OpenRouter chưa được cấu hình OPENROUTER_API_KEY trên server. Bật Không gọi mô hình để chỉ đọc nguồn.";
        return o;
    }

    private static bool Flag(IFormCollection form, string key) =>
        form[key].ToString() is "1" or "true" or "on";
}
