using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Eval.R18;

namespace DocxHeaderExtractor.Cli;

internal static class R18Runner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<int> RunAsync(CommandLineOptions options, CancellationToken cancellationToken)
    {
        if (!string.Equals(options.R18Operation, "ownership", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("r18 operations: ownership");
            return 0;
        }

        if (options.Inputs.Count != 1)
            throw new ArgumentException("r18 ownership cần đúng một file .docx/.docm.");
        var report = await R18DecisionOwnershipAuditRunner.RunAsync(options.Inputs[0], cancellationToken);
        var json = JsonSerializer.Serialize(report, JsonOptions);
        if (options.OutputPath is null) Console.WriteLine(json);
        else
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(options.OutputPath));
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(options.OutputPath, json + Environment.NewLine, cancellationToken);
        }
        return report.ProviderCalls == 0 ? 0 : 1;
    }
}
