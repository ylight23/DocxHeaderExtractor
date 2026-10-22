using System.Text;
using DocxHeaderExtractor.Cli;
using DocxHeaderExtractor.Cli.Commands;

Console.OutputEncoding = Encoding.UTF8;

CommandLineOptions options;
try
{
    options = CommandLineOptions.Parse(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Lỗi tham số: {ex.Message}");
    Console.Error.WriteLine("Chạy `dhx help` để xem hướng dẫn.");
    return 2;
}
if (options.ShowHelp)
{
    Console.WriteLine(CommandLineOptions.HelpText);
    return 0;
}
// `info` tự dò mô hình – không cần đầu vào.
if (options.Inputs.Count == 0 && options.Command is not "info")
{
    Console.Error.WriteLine("Chưa chỉ định file đầu vào.");
    return 2;
}
if (!options.Quiet) options.Pipeline.Log = m => Console.Error.WriteLine($"  {m}");
// Explicit raw diagnostics remain available with --quiet; never mix them into result stdout.
options.ConfigureProviderDiagnostics(m => Console.Error.WriteLine($"  {m}"));

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    return options.Command switch
    {
        "info" => InfoCommand.Run(options),
        "scan-extract" => await ScanCommands.ExtractAsync(options, cts.Token),
        "scan-text-eval" => ScanCommands.TextEval(options),
        "scan-heading-eval" => ScanCommands.HeadingEval(options),
        "scan-evidence-json" => ScanCommands.EvidenceJson(options),
        _ => await ExtractCommand.RunAsync(options, cts.Token),
    };
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Đã huỷ.");
    return 130;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Lỗi: {ex.Message}");
    if (Environment.GetEnvironmentVariable("DHX_DEBUG") == "1")
        Console.Error.WriteLine(ex);
    return 1;
}
