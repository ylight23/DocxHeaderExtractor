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
                o.Llama.MaxCandidatesPerChunk = (int)cc;
        }

        o.TrustStyles = !Flag(form, "noTrustStyles");
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
