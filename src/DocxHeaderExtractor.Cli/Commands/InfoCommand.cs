namespace DocxHeaderExtractor.Cli.Commands;

/// <summary><c>dhx info</c>: the size, parameters, context and metadata of a local <c>.gguf</c> model.</summary>
internal static class InfoCommand
{
    public static int Run(CommandLineOptions o)
    {
        var path = o.Inputs.FirstOrDefault() ?? o.Provider.LocalModel.ModelPath;
        if (string.IsNullOrWhiteSpace(path)) path = ModelLocator.Locate() ?? "";
        if (!File.Exists(path))
        {
            Console.Error.WriteLine("Không tìm thấy file .gguf.");
            return 2;
        }

        DocxHeaderExtractor.Infrastructure.AI.LlamaInferenceTransport.ConfigureNativeLogging(o.Provider.LocalModel.VerboseNativeLog);

        var fi = new FileInfo(path);
        Console.WriteLine($"File   : {fi.FullName}");
        Console.WriteLine($"Kích cỡ: {fi.Length / 1024.0 / 1024.0:0.0} MB");

        using var weights = LLama.LLamaWeights.LoadFromFile(
            new LLama.Common.ModelParams(path) { ContextSize = 256, GpuLayerCount = 0, VocabOnly = true });

        Console.WriteLine($"Tham số: {weights.ParameterCount / 1_000_000_000.0:0.00} B");

        // VocabOnly = true nên weights.ContextSize là 0; giá trị thật nằm trong metadata.
        var trainCtx = weights.Metadata.FirstOrDefault(k => k.Key.EndsWith(".context_length")).Value;
        Console.WriteLine($"Ngữ cảnh tối đa: {trainCtx ?? "?"}");
        Console.WriteLine($"Chat template  : {(weights.Metadata.ContainsKey("tokenizer.chat_template") ? "có" : "không (dùng template Llama 3 dựng tay)")}");
        Console.WriteLine();
        foreach (var kv in weights.Metadata.OrderBy(k => k.Key))
        {
            var v = kv.Value.Length > 120 ? kv.Value[..120] + "…" : kv.Value;
            Console.WriteLine($"  {kv.Key} = {v.ReplaceLineEndings(" ")}");
        }
        return 0;
    }
}
