using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.App;

namespace DocxHeaderExtractor.Cli.Commands;

/// <summary><c>dhx entities</c>: the entities of one document, as the entity problem finds them (the default types, one run per document).</summary>
internal static class EntityCommand
{
    public static async Task<int> RunAsync(CommandLineOptions o, CancellationToken ct)
    {
        if (o.Inputs.Count != 1) { Console.Error.WriteLine("Cách dùng: dhx entities <file.docx|file.pdf> [-o <out.json>]"); return 2; }
        var provider = OpenRouterProvider.FromEnvironment();
        if (!o.Pipeline.DisableLlm && !provider.HasKey)
        {
            Console.Error.WriteLine($"Chưa có {OpenRouterProvider.ApiKeyVariable}. Đặt biến môi trường này, hoặc dùng --no-llm để chỉ đọc nguồn.");
            return 2;
        }
        var run = await EntityExtraction.RunAsync(o.Inputs[0], o.Pipeline, provider, ct);
        var json = JsonSerializer.Serialize(new { envelope = run.Envelope, output = run.Output },
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        if (o.OutputPath is { } path) await File.WriteAllTextAsync(path, json, new UTF8Encoding(false), ct);
        else Console.WriteLine(json);
        if (run.Output is null) return 0;
        return run.Output.BatchFailures.Count == 0 ? 0 : 1;
    }
}
