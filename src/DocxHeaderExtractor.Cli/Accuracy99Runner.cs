using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Eval.Accuracy99;

namespace DocxHeaderExtractor.Cli;

internal static class Accuracy99Runner
{
    private const string StructuralProfile = "structural";
    private const string ProductProfile = "product";
    private const string FixedPrompt = "Trích xuất heading của văn bản này.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<int> RunAsync(CommandLineOptions options, CancellationToken cancellationToken)
    {
        var operation = options.Accuracy99Operation?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(operation) || operation is "help" or "-h")
        {
            Console.WriteLine("accuracy99 operations: packet, inventory, evaluate, baseline");
            return 0;
        }

        return operation switch
        {
            "packet" => await BuildPacketAsync(options, cancellationToken),
            "inventory" => await BuildInventoryAsync(options, cancellationToken),
            "evaluate" => await EvaluateAsync(options, cancellationToken),
            "baseline" => await BuildBaselineAsync(options, cancellationToken),
            _ => throw new ArgumentException($"accuracy99 operation không hợp lệ: {operation}"),
        };
    }

    private static async Task<int> BuildPacketAsync(
        CommandLineOptions options,
        CancellationToken cancellationToken)
    {
        var path = RequireInput(options, "packet");
        var source = ReadSource(path);
        var packet = BlindSourcePacketBuilder.CreateFromFile(source);
        var json = JsonSerializer.Serialize(packet, JsonOptions);
        BlindSourcePacketLeakageValidator.EnsureClean(json);
        await WriteAsync(options.OutputPath, json, cancellationToken);
        return 0;
    }

    private static async Task<int> BuildInventoryAsync(
        CommandLineOptions options,
        CancellationToken cancellationToken)
    {
        var root = options.Accuracy99Root ?? RequireInput(options, "inventory");
        var inventory = Accuracy99DatasetInventoryBuilder.Discover(root);
        var json = JsonSerializer.Serialize(inventory, JsonOptions);
        await WriteAsync(options.OutputPath, json, cancellationToken);
        return inventory.InvalidSourceCount == 0 ? 0 : 1;
    }

    private static async Task<int> EvaluateAsync(
        CommandLineOptions options,
        CancellationToken cancellationToken)
    {
        var sourcePath = RequireInput(options, "evaluate");
        var goldPath = options.Accuracy99GoldPath
                       ?? throw new ArgumentException("evaluate cần --accuracy-gold <path>");
        var predictionPath = options.Accuracy99PredictionPath
                             ?? throw new ArgumentException("evaluate cần --prediction <path>");
        var source = ReadSource(sourcePath);
        var gold = DeserializeRequired<HumanGoldArtifact>(await File.ReadAllTextAsync(goldPath, cancellationToken));
        var outline = await ReadOutlineAsync(predictionPath, cancellationToken);
        var metric = Accuracy99Evaluator.Evaluate(
            source, gold, outline, HumanGoldValidator.ComputeSha256(source.SourcePath));
        await WriteAsync(options.OutputPath, JsonSerializer.Serialize(metric, JsonOptions), cancellationToken);
        return 0;
    }

    private static async Task<int> BuildBaselineAsync(
        CommandLineOptions options,
        CancellationToken cancellationToken)
    {
        var sourcePath = RequireInput(options, "baseline");
        var profile = options.Accuracy99Profile?.Trim().ToLowerInvariant() ?? StructuralProfile;
        var source = ReadSource(sourcePath);
        if (profile == ProductProfile)
        {
            var notMeasured = new
            {
                artifactKind = "accuracy99_baseline",
                status = "NOT_MEASURED",
                profile,
                reason = "product-provider-not-run-by-accuracy99-infrastructure",
                sourceDocumentSha256 = HumanGoldValidator.ComputeSha256(source.SourcePath),
                providerCalls = 0,
                expectedChanged = false,
                fixedPrompt = FixedPrompt,
            };
            await WriteAsync(options.OutputPath, JsonSerializer.Serialize(notMeasured, JsonOptions), cancellationToken);
            return 0;
        }
        if (profile != StructuralProfile)
            throw new ArgumentException($"accuracy99 baseline profile không hợp lệ: {profile}");

        var pipelineOptions = options.Pipeline;
        pipelineOptions.DisableLlm = true;
        using var pipeline = new AuthorityExtractionPipeline(pipelineOptions);
        var outline = await pipeline.RunAsync(sourcePath, cancellationToken);
        var predictions = outline.Headings.Select(Accuracy99Evaluator.FromHeading).ToArray();
        var measured = new
        {
            artifactKind = "accuracy99_baseline",
            status = "MEASURED",
            profile,
            documentId = source.DocumentId,
            sourceDocumentSha256 = HumanGoldValidator.ComputeSha256(source.SourcePath),
            fixedPrompt = FixedPrompt,
            providerCalls = 0,
            expectedChanged = false,
            predictionCount = predictions.Length,
            predictions,
            outline,
            accuracyClaim = "NOT_YET_ESTABLISHED",
        };
        await WriteAsync(options.OutputPath, JsonSerializer.Serialize(measured, JsonOptions), cancellationToken);
        return 0;
    }

    private static SourceDocument ReadSource(string path)
    {
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".docx", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".docm", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(
                "accuracy99 source packet/evaluator hiện chỉ đọc parser-owned DOCX/ DOCM; PDF reader chưa được cấu hình.");
        return new OpenXmlDocumentSource().Read(path);
    }

    private static async Task<DocumentOutline> ReadOutlineAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("outline", out var outline))
            return DeserializeRequired<DocumentOutline>(outline.GetRawText());
        return DeserializeRequired<DocumentOutline>(json);
    }

    private static T DeserializeRequired<T>(string json)
    {
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
               ?? throw new InvalidDataException($"Không đọc được JSON {typeof(T).Name}.");
    }

    private static string RequireInput(CommandLineOptions options, string operation)
    {
        if (options.Inputs.Count == 0)
            throw new ArgumentException($"{operation} cần một input path.");
        return options.Inputs[0];
    }

    private static async Task WriteAsync(
        string? outputPath,
        string content,
        CancellationToken cancellationToken)
    {
        if (outputPath is null)
        {
            Console.WriteLine(content);
            return;
        }
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(outputPath, content + Environment.NewLine, new UTF8Encoding(false), cancellationToken);
    }
}
