using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Encodings.Web;
using DocxHeaderExtractor.App;
using DocxHeaderExtractor.DocumentProcessing.Harness;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

/// <summary>Live full DOCX lane: cold cache followed by warm cache on the same immutable inputs.</summary>
static class DocxCacheTrial
{
    public static int ReplaySelect(string[] args)
    {
        if (args.Length != 4) throw new ArgumentException("docx-replay-select <first-request.json> <responses.jsonl> <output.json>");
        using var request = JsonDocument.Parse(File.ReadAllText(args[1]));
        var content = request.RootElement.GetProperty("messages")[1].GetProperty("content");
        var text = content.ValueKind == JsonValueKind.String ? content.GetString()! : content[0].GetProperty("text").GetString()!;
        using var packet = JsonDocument.Parse(text);
        var targets = packet.RootElement.GetProperty("targets").EnumerateArray().ToArray();
        var context = packet.RootElement.GetProperty("context").EnumerateArray().ToArray();
        var decide = targets.Select(x => x.GetProperty("alias").GetString()!).ToArray();
        var texts = targets.ToDictionary(x => x.GetProperty("alias").GetString()!, x => x.GetProperty("text").GetString()!, StringComparer.Ordinal);
        var shown = targets.Concat(context).Select(x => x.GetProperty("alias").GetString()!).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).Select((alias, index) => (alias, index)).ToDictionary(x => x.alias, x => x.index, StringComparer.Ordinal);
        var asm = typeof(DocxHeaderExtractor.HeadingAgent.Selectors.DocxSelector).Assembly;
        var protocol = asm.GetType("DocxHeaderExtractor.HeadingAgent.Protocol.AgentProtocol", true)!;
        var parse = protocol.GetMethod("ParseSelect")!;
        var version = (string)protocol.GetField("SelectExtentVersion")!.GetRawConstantValue()!;
        var results = new List<object>();
        foreach (var line in File.ReadLines(args[2]))
        {
            using var row = JsonDocument.Parse(line);
            if (row.RootElement.GetProperty("Unit").GetString() != "SEG-000") continue;
            var reply = row.RootElement.GetProperty("Content").GetString()!;
            var parsed = parse.Invoke(null, [reply, shown, decide, version, texts, null])!;
            var type = parsed.GetType();
            results.Add(new { turn = row.RootElement.GetProperty("Turn").GetInt32(),
                ok = type.GetProperty("Ok")!.GetValue(parsed), failure = type.GetProperty("Failure")!.GetValue(parsed),
                detail = type.GetProperty("Detail")!.GetValue(parsed) });
        }
        File.WriteAllText(args[3], JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Console.WriteLine("replayed=" + results.Count);
        return 0;
    }

    public static int FirstPacket(string[] args)
    {
        if (args.Length != 3) throw new ArgumentException("docx-first-packet <docx> <output-json>");
        var asm = typeof(DocxHeaderExtractor.HeadingAgent.Selectors.DocxSelector).Assembly;
        var sourceType = asm.GetType("DocxHeaderExtractor.HeadingAgent.Formats.DocxAgentSource", true)!;
        object?[] loadArgs = [Path.GetFullPath(args[1]), null, null, 0];
        var source = sourceType.GetMethod("Load")!.Invoke(null, loadArgs) ?? throw new InvalidDataException("DOCX_LOAD_FAILED:" + loadArgs[2]);
        var protocol = asm.GetType("DocxHeaderExtractor.HeadingAgent.Protocol.DocxAgentProtocol", true)!;
        var instruction = (string)protocol.GetField("Instruction")!.GetRawConstantValue()!;
        if (args[0] == "docx-boundary-packet")
        {
            sourceType.GetProperty("QualificationFormatBoundaryEvidence")!.SetValue(source, true);
            instruction += "\n" + DocxBoundaryTrial.Addition;
        }
        if (args[0] == "docx-format-runs-packet")
            sourceType.GetProperty("QualificationFormatRunEvidence")!.SetValue(source, true);
        var unit = asm.GetType("DocxHeaderExtractor.HeadingAgent.Runtime.AgentUnit", true)!;
        var method = unit.GetMethod("PrepareFirstRequest", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var request = method.Invoke(null, [source, 0, instruction, null, null])!;
        var body = (string)request.GetType().GetProperty("Body")!.GetValue(request)!;
        var sha = (string)request.GetType().GetProperty("Sha256")!.GetValue(request)!;
        File.WriteAllText(args[2], body);
        Console.WriteLine("firstRequestSha256=" + sha);
        return 0;
    }

    public static async Task<int> DiagnoseAsync(string[] args)
    {
        if (args.Length != 4) throw new ArgumentException("docx-diagnose <output-dir> <existing-cache-root> <docx>");
        var output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output)) throw new IOException("OUTPUT_DIRECTORY_ALREADY_EXISTS");
        var cacheRoot = Path.GetFullPath(args[2]);
        if (!Directory.Exists(cacheRoot)) throw new DirectoryNotFoundException(cacheRoot);
        var source = Path.GetFullPath(args[3]);
        var sourceHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(source)));
        var provider = OpenRouterProvider.FromEnvironment();
        if (!provider.HasKey) throw new InvalidOperationException("OPENROUTER_API_KEY_MISSING");
        Directory.CreateDirectory(output);
        var json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var lineJson = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var guard = new object();
        foreach (var phase in new[] { "place-1", "place-2", "place-3", "fresh-traced" })
        {
            var options = new PipelineOptions { DocxReuseSelection = phase != "fresh-traced", DocxSelectionCacheRoot = cacheRoot,
                MaxProviderCostUsd = 1, MaxProviderAttempts = 80 };
            var log = Path.Combine(output, phase + "-responses.jsonl");
            void Observe(ModelReplyView reply)
            {
                // Diagnostic structured responses only; do not collect private reasoning.
                var row = new { reply.Unit, reply.Turn, reply.Content, reply.FinishReason, reply.RequestSha256, reply.ToolCalls, reply.ToolResults };
                lock (guard) File.AppendAllText(log, JsonSerializer.Serialize(row, lineJson) + "\n");
                Console.WriteLine($"{phase} reply unit={reply.Unit} turn={reply.Turn} finish={reply.FinishReason}");
            }
            var clock = Stopwatch.StartNew();
            try
            {
                var result = await new DocumentRunner(options, provider, Observe).ExtractAsync(source);
                var audit = result.HeadingPipeline?.Audit;
                var data = new { phase, sourceSha256 = sourceHash, seconds = clock.Elapsed.TotalSeconds, outline = result.Outline,
                    budget = result.ResourceBudget, selectionCalls = audit?.SelectionProviderCalls, selectionResources = audit?.SelectionResources,
                    selectionLinesAnswered = audit?.SelectionLinesAnswered, selectionLinesTotal = audit?.SelectionLinesTotal,
                    grouping = audit?.Grouping, placement = audit?.PlacementExecution, stageSeconds = audit?.HeadingStageSeconds };
                File.WriteAllText(Path.Combine(output, phase + ".json"), JsonSerializer.Serialize(data, json));
                Console.WriteLine($"{phase} complete seconds={clock.Elapsed.TotalSeconds:F1} selectionCalls={audit?.SelectionProviderCalls} placementCalls={audit?.PlacementExecution?.LogicalCalls}");
                if (Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(source))) != sourceHash) throw new InvalidDataException("SOURCE_CHANGED");
                if (phase != "fresh-traced" && audit?.SelectionProviderCalls != 0) throw new InvalidDataException("WARM_SELECTION_CACHE_MISS");
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(output, phase + "-error.txt"), ex.ToString());
                Console.Error.WriteLine($"{phase} failed: {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
        }
        return 0;
    }

    public static async Task<int> ControlAsync(string[] args)
    {
        if (args.Length < 3) throw new ArgumentException("docx-cache-control <output-dir> <docx>...");
        var output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output)) throw new IOException("OUTPUT_DIRECTORY_ALREADY_EXISTS");
        var provider = OpenRouterProvider.FromEnvironment();
        if (!provider.HasKey) throw new InvalidOperationException("OPENROUTER_API_KEY_MISSING");
        Directory.CreateDirectory(output);
        var options = new PipelineOptions { DocxReuseSelection = false, MaxProviderCostUsd = 1, MaxProviderAttempts = 80 };
        var json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        foreach (var input in args.Skip(2))
        {
            var file = Path.GetFullPath(input);
            var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));
            var name = Path.GetFileNameWithoutExtension(file);
            var clock = Stopwatch.StartNew();
            try
            {
                var result = await new DocumentRunner(options, provider).ExtractAsync(file);
                var audit = result.HeadingPipeline?.Audit;
                var data = new { source = name, sourceSha256 = hash, phase = "control-cache-off", seconds = clock.Elapsed.TotalSeconds,
                    outline = result.Outline, budget = result.ResourceBudget, selectionCalls = audit?.SelectionProviderCalls,
                    selectionResources = audit?.SelectionResources, selectionLinesAnswered = audit?.SelectionLinesAnswered,
                    selectionLinesTotal = audit?.SelectionLinesTotal, grouping = audit?.Grouping, placement = audit?.PlacementExecution,
                    stageSeconds = audit?.HeadingStageSeconds };
                File.WriteAllText(Path.Combine(output, name + "-control.json"), JsonSerializer.Serialize(data, json));
                Console.WriteLine($"{name} control seconds={clock.Elapsed.TotalSeconds:F1} selectionCalls={audit?.SelectionProviderCalls} placementCalls={audit?.PlacementExecution?.LogicalCalls}");
                if (Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))) != hash) throw new InvalidDataException("SOURCE_CHANGED");
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(output, name + "-error.txt"), ex.ToString());
                Console.Error.WriteLine($"{name} control failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return 0;
    }

    public static async Task<int> ChangesAsync(string[] args)
    {
        if (args.Length != 4) throw new ArgumentException("docx-cache-changes <output-dir> <baseline-cache-dir> <074.docx>");
        var output = Path.GetFullPath(args[1]);
        var oldCache = Path.GetFullPath(args[2]);
        var original = Path.GetFullPath(args[3]);
        if (Directory.Exists(output)) throw new IOException("OUTPUT_DIRECTORY_ALREADY_EXISTS");
        var provider = OpenRouterProvider.FromEnvironment();
        if (!provider.HasKey) throw new InvalidOperationException("OPENROUTER_API_KEY_MISSING");
        Directory.CreateDirectory(output);
        var cache = Path.Combine(output, "cache");
        foreach (var entry in Directory.EnumerateFiles(oldCache, "*.json", SearchOption.AllDirectories))
        {
            var target = Path.Combine(cache, Path.GetRelativePath(oldCache, entry));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(entry, target);
        }
        var options = new PipelineOptions { DocxReuseSelection = true, DocxSelectionCacheRoot = cache, MaxProviderCostUsd = 1, MaxProviderAttempts = 80 };
        var json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var variants = args[0] == "docx-cache-delete-move" ? new[] { "heading-deleted", "heading-moved" } : new[] { "body-added", "heading-added", "heading-renamed" };
        foreach (var variant in variants)
        {
            var file = Path.Combine(output, variant + ".docx");
            File.Copy(original, file);
            using (var doc = WordprocessingDocument.Open(file, true))
            {
                var body = doc.MainDocumentPart!.Document!.Body!;
                var opening = body.Elements<Paragraph>().Single(p => p.InnerText.TrimStart().StartsWith("Opening:", StringComparison.Ordinal));
                var present = body.Elements<Paragraph>().Single(p => p.InnerText.TrimStart().StartsWith("Present:", StringComparison.Ordinal));
                if (variant == "body-added") opening.InsertAfterSelf(new Paragraph(new Run(new Text("Additional report information was recorded for this meeting."))));
                else if (variant == "heading-added") present.InsertBeforeSelf(new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }), new Run(new RunProperties(new Bold()), new Text("Additional Test Section"))));
                else if (variant == "heading-deleted")
                    present.Remove();
                else if (variant == "heading-moved")
                {
                    var later = body.Elements<Paragraph>().Single(p => p.InnerText.TrimStart().StartsWith("Governance/Voting Rights.", StringComparison.Ordinal));
                    present.Remove();
                    later.InsertBeforeSelf(present);
                }
                else
                {
                    var text = present.Descendants<Text>().First(t => t.Text.Contains("Present:", StringComparison.Ordinal));
                    text.Text = text.Text.Replace("Present:", "Attendance:", StringComparison.Ordinal);
                }
                doc.MainDocumentPart.Document.Save();
            }
            var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));
            var clock = Stopwatch.StartNew();
            try
            {
                var result = await new DocumentRunner(options, provider).ExtractAsync(file);
                var audit = result.HeadingPipeline?.Audit;
                var data = new { variant, sourceSha256 = hash, seconds = clock.Elapsed.TotalSeconds, outline = result.Outline,
                    budget = result.ResourceBudget, selectionCalls = audit?.SelectionProviderCalls, selectionResources = audit?.SelectionResources,
                    selectionLinesAnswered = audit?.SelectionLinesAnswered, selectionLinesTotal = audit?.SelectionLinesTotal,
                    grouping = audit?.Grouping, placement = audit?.PlacementExecution, stageSeconds = audit?.HeadingStageSeconds };
                File.WriteAllText(Path.Combine(output, variant + ".json"), JsonSerializer.Serialize(data, json));
                Console.WriteLine($"{variant} seconds={clock.Elapsed.TotalSeconds:F1} selectionCalls={audit?.SelectionProviderCalls} placementCalls={audit?.PlacementExecution?.LogicalCalls}");
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(output, variant + "-error.txt"), ex.ToString());
                Console.Error.WriteLine($"{variant} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return 0;
    }

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 3) throw new ArgumentException("docx-cache-ab <output-dir> <docx>...");
        var output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output)) throw new IOException("OUTPUT_DIRECTORY_ALREADY_EXISTS");
        var provider = OpenRouterProvider.FromEnvironment();
        if (!provider.HasKey) throw new InvalidOperationException("OPENROUTER_API_KEY_MISSING");
        Directory.CreateDirectory(output);
        var options = new PipelineOptions { DocxReuseSelection = true, DocxSelectionCacheRoot = Path.Combine(output, "cache"), MaxProviderCostUsd = 1, MaxProviderAttempts = 80 };
        var json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        foreach (var input in args.Skip(2))
        {
            var file = Path.GetFullPath(input);
            var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));
            var name = Path.GetFileNameWithoutExtension(file);
            foreach (var phase in new[] { "cold", "warm" })
            {
                var clock = Stopwatch.StartNew();
                try
                {
                    var result = await new DocumentRunner(options, provider).ExtractAsync(file);
                    var audit = result.HeadingPipeline?.Audit;
                    var data = new { source = name, sourceSha256 = hash, phase, seconds = clock.Elapsed.TotalSeconds,
                        outline = result.Outline, budget = result.ResourceBudget,
                        selectionCalls = audit?.SelectionProviderCalls, selectionResources = audit?.SelectionResources,
                        selectionLinesAnswered = audit?.SelectionLinesAnswered, selectionLinesTotal = audit?.SelectionLinesTotal,
                        grouping = audit?.Grouping, placement = audit?.PlacementExecution, stageSeconds = audit?.HeadingStageSeconds };
                    File.WriteAllText(Path.Combine(output, name + "-" + phase + ".json"), JsonSerializer.Serialize(data, json));
                    Console.WriteLine($"{name} {phase} seconds={clock.Elapsed.TotalSeconds:F1} selectionCalls={audit?.SelectionProviderCalls} placementCalls={audit?.PlacementExecution?.LogicalCalls}");
                    if (Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))) != hash) throw new InvalidDataException("SOURCE_CHANGED");
                }
                catch (Exception ex)
                {
                    File.WriteAllText(Path.Combine(output, name + "-" + phase + "-error.txt"), ex.ToString());
                    Console.Error.WriteLine($"{name} {phase} failed: {ex.GetType().Name}: {ex.Message}");
                    return 1;
                }
            }
        }
        return 0;
    }
}
