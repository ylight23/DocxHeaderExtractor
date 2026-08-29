using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

internal static class Program
{
    private static readonly string[] Required = ["worktree", "revision", "corpus", "output", "mode"];

    public static int Main(string[] args)
    {
        try
        {
            var options = Parse(args);
            foreach (var key in Required)
                if (!options.ContainsKey(key)) throw new InvalidOperationException($"MISSING_ARGUMENT: --{key}");
            if (options["mode"] is not ("diagnostic" or "pdf")) throw new InvalidOperationException("INVALID_MODE");

            var worktree = Path.GetFullPath(options["worktree"]);
            var revision = options["revision"];
            EnsureTargetWorktree(worktree, revision);
            var corpusPath = Path.GetFullPath(options["corpus"]);
            var corpus = JsonSerializer.Deserialize<Corpus>(File.ReadAllText(corpusPath), JsonOptions)
                         ?? throw new InvalidOperationException("CORPUS_PARSE_FAILED");
            var assemblyPath = Path.Combine(worktree, "src", "DocxHeaderExtractor.Core", "bin", "Release", "net9.0", "DocxHeaderExtractor.Core.dll");
            if (!File.Exists(assemblyPath)) throw new InvalidOperationException("TARGET_ASSEMBLY_MISSING: " + assemblyPath);
            var output = Path.GetFullPath(options["output"]);
            Directory.CreateDirectory(output);

            var context = new TargetAssemblyLoadContext(assemblyPath);
            var core = context.LoadFromAssemblyPath(assemblyPath);
            foreach (var item in corpus.Items.Where(item => item.EnabledFor.Contains(options["mode"], StringComparer.Ordinal)))
            {
                var docx = Path.Combine(worktree, item.Docx.Replace('/', Path.DirectorySeparatorChar));
                var pdf = Path.Combine(worktree, item.Pdf.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(docx) || !File.Exists(pdf)) throw new InvalidOperationException("CORPUS_FILES_MISSING: " + item.Id);
                var snapshot = options["mode"] == "diagnostic"
                    ? ExportDiagnostic(core, item, docx, pdf, revision)
                    : ExportPdf(core, item, docx, pdf, revision);
                var path = Path.Combine(output, item.Id + ".json");
                File.WriteAllText(path, JsonSerializer.Serialize(snapshot, JsonOptions));
                Console.WriteLine(path);
            }
            context.Unload();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.InnerException?.ToString() ?? ex.ToString());
            return 1;
        }
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
                throw new InvalidOperationException("INVALID_ARGUMENTS");
            result[args[i][2..]] = args[++i];
        }
        return result;
    }

    private static void EnsureTargetWorktree(string worktree, string revision)
    {
        var head = RunGit(worktree, "rev-parse HEAD");
        if (!string.Equals(head, revision, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"TARGET_REVISION_MISMATCH: expected {revision}, got {head}");
        if (!string.IsNullOrWhiteSpace(RunGit(worktree, "status --porcelain")))
            throw new InvalidOperationException("TARGET_WORKTREE_DIRTY");
    }

    private static string RunGit(string worktree, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = worktree,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("GIT_FAILED: " + process.StandardError.ReadToEnd());
        return output;
    }

    private static Dictionary<string, object?> ExportDiagnostic(Assembly core, CorpusItem item, string docx, string pdf, string revision)
    {
        var input = BuildInput(core, docx, legacy: revision.StartsWith("3b35054", StringComparison.OrdinalIgnoreCase));
        var runner = core.GetType("DocxHeaderExtractor.Core.Pipeline.DocumentDiagnosticRunner")!;
        var analyze = runner.GetMethods(BindingFlags.Public | BindingFlags.Static).Single(m => m.Name == "Analyze");
        var report = analyze.Invoke(null, [input.Document, input.Mode])!;
        var style = Prop(report, "Style");
        var layout = Prop(report, "Layout");
        var candidates = AsObjects(Prop(report, "Candidates"));
        var result = Base(item, revision, docx, pdf, "diagnostic");
        result["runtimeAssemblySha256"] = Sha256(core.Location);
        result["status"] = Prop(report, "Status");
        result["reason"] = Prop(report, "Reason");
        result["styleSignal"] = Project(style, "StyledCount", "SuspectRatio", "Density", "DistinctLevels", "NumberedDisagreeRatio", "SelectionTrusted", "LevelTrusted", "Mixed");
        result["layoutSignal"] = Project(layout, "MergedParagraphs", "MergedMarkers", "TableOfContentsParagraphs", "TypedNumberSegments");
        result["candidateDiagnostics"] = candidates.Select(candidate => Project(candidate, "Route", "Accepted", "Reason", "HeadingCount", "DuplicateRate", "TitlePollutionRate", "LevelJumpRate", "BodyAnchorRatio", "TocCoverage")).ToArray();
        return result;
    }

    private static Dictionary<string, object?> ExportPdf(Assembly core, CorpusItem item, string docx, string pdf, string revision)
    {
        var legacy = revision.StartsWith("a920b2a", StringComparison.OrdinalIgnoreCase);
        var input = BuildInput(core, docx, legacy);
        var layout = core.GetType("DocxHeaderExtractor.Core.Pipeline.PdfLayoutEvidenceOutline")
                     ?? throw new InvalidOperationException("PDF_LAYOUT_TYPE_MISSING");
        var rankingMethod = layout.GetMethod("BuildCandidateRankingAudit", BindingFlags.Public | BindingFlags.Static)
                            ?? throw new InvalidOperationException("PDF_RANKING_METHOD_MISSING");
        var stage = Directory.CreateTempSubdirectory("r4-pdf-");
        var stagedDocx = Path.Combine(stage.FullName, "input.docx");
        var stagedPdf = Path.Combine(stage.FullName, "input.pdf");
        File.Copy(docx, stagedDocx);
        File.Copy(pdf, stagedPdf);
        var ranking = rankingMethod.Invoke(null, [stagedDocx])
                      ?? throw new InvalidOperationException("PDF_RANKING_RESULT_MISSING");
        var blocks = AsObjects(Prop(ranking, "Candidates"));
        var ids = blocks.Select(block => Convert.ToString(Prop(block, "SourceId")) ?? Convert.ToString(Prop(block, "Id")) ?? "").ToArray();
        var visual = core.GetType("DocxHeaderExtractor.Core.Pipeline.PdfVisualTextRecovery")
                      ?? throw new InvalidOperationException("PDF_VISUAL_TYPE_MISSING");
        var visualRows = new List<object>();
        foreach (var text in item.VisualProbeTexts)
        {
            var method = visual.GetMethod("InspectSourceForAudit", BindingFlags.Public | BindingFlags.Static)
                        ?? throw new InvalidOperationException("PDF_INSPECT_METHOD_MISSING");
            var audit = method.Invoke(null, [input.Document, text])
                        ?? throw new InvalidOperationException("PDF_INSPECT_RESULT_MISSING");
            visualRows.Add(new Dictionary<string, object?>
            {
                ["observedText"] = text,
                ["canonical"] = Prop(audit, "Canonical"),
                ["matchCount"] = Prop(audit, "MatchCount"),
                ["matchingParagraphIndexes"] = Prop(audit, "MatchingParagraphIndexes"),
                ["paragraphCount"] = Prop(audit, "ParagraphCount"),
            });
        }
        var alignmentRows = Array.Empty<object>();
        var productRows = Array.Empty<object>();
        if (ids.Length > 0 && layout.GetMethod("BuildBroadAlignmentForCandidateIds", BindingFlags.NonPublic | BindingFlags.Static) is { } alignmentMethod)
        {
            var alignment = alignmentMethod.Invoke(null, [stagedDocx, input.Document, ids.ToHashSet(StringComparer.Ordinal), null]);
            if (alignment is not null)
            {
                var headings = AsObjects(Prop(alignment, "Headings")).ToArray();
                alignmentRows = headings.Select(ProjectHeading).ToArray();
                productRows = headings.Select(ProjectHeading).ToArray();
            }
        }
        var result = Base(item, revision, docx, pdf, "pdf");
        result["runtimeAssemblySha256"] = Sha256(core.Location);
        result["retrieval"] = new Dictionary<string, object?>
        {
            ["candidateIds"] = ids,
            ["candidateOrder"] = ids,
            ["selectedSourceIdentities"] = Array.Empty<object>(),
        };
        result["alignment"] = alignmentRows;
        result["visualMapping"] = visualRows;
        result["validatedStructures"] = Array.Empty<object>();
        result["product"] = productRows;
        stage.Delete(true);
        return result;
    }

    private static Dictionary<string, object?> ProjectHeading(object heading)
    {
        var span = Prop(heading, "HeadingSpan");
        return new Dictionary<string, object?>
        {
            ["blockId"] = Prop(heading, "SourceId"), ["paragraphIndex"] = Prop(heading, "Index"),
            ["sourceId"] = Prop(heading, "SourceId"), ["stableId"] = Prop(heading, "StableId"),
            ["spanStart"] = span is null ? null : Prop(span, "Start"), ["spanEnd"] = span is null ? null : Prop(span, "End"),
            ["matchBranch"] = Prop(heading, "ConfidenceBasis"), ["index"] = Prop(heading, "Index"),
            ["text"] = Prop(heading, "Text"), ["level"] = Prop(heading, "Level"),
            ["boundarySource"] = Prop(heading, "BoundarySource"), ["decisionStatus"] = Prop(heading, "DecisionStatus"),
        };
    }

    private static InputObjects BuildInput(Assembly core, string docx, bool legacy)
    {
        var models = core.GetType("DocxHeaderExtractor.Core.Models.SlimDocument")!;
        var options = Activator.CreateInstance(core.GetType("DocxHeaderExtractor.Core.OpenXmlLayer.ExtractionOptions")!)!;
        object document;
        object mode;
        if (legacy)
        {
            var extractor = Activator.CreateInstance(core.GetType("DocxHeaderExtractor.Core.OpenXmlLayer.DocxSlimExtractor")!, options)!;
            document = extractor.GetType().GetMethod("Extract")!.Invoke(extractor, [docx])!;
            mode = Prop(document, "Mode")!;
        }
        else
        {
            var source = Activator.CreateInstance(core.GetType("DocxHeaderExtractor.Core.OpenXmlLayer.OpenXmlDocumentSource")!, options)!;
            var sourceDocument = source.GetType().GetMethod("Read")!.Invoke(source, [docx])!;
            var featuresType = core.GetType("DocxHeaderExtractor.Core.OpenXmlLayer.NumberingStyleFeatures")!;
            var features = featuresType.GetMethod("FromSourceDocument")!.Invoke(null, [sourceDocument])!;
            var deriver = Activator.CreateInstance(core.GetType("DocxHeaderExtractor.Core.Application.Features.DocumentFeatureDeriver")!)!;
            var derived = deriver.GetType().GetMethod("Derive")!.Invoke(deriver, [sourceDocument])!;
            var builder = core.GetType("DocxHeaderExtractor.Core.Application.Policy.DocxPolicyStateBuilder")!;
            document = builder.GetMethod("Build")!.Invoke(null, [sourceDocument, features, derived, options])!;
            var paragraphs = Prop(document, "Paragraphs")!;
            var policyParagraph = core.GetType("DocxHeaderExtractor.Core.Application.Policy.IPolicyParagraph")!;
            var array = Array.CreateInstance(policyParagraph, ((System.Collections.IEnumerable)paragraphs).Cast<object>().Count());
            var index = 0;
            foreach (var paragraph in (System.Collections.IEnumerable)paragraphs) array.SetValue(paragraph, index++);
            var classifier = core.GetType("DocxHeaderExtractor.Core.OpenXmlLayer.DocumentModeClassifier")!;
            mode = classifier.GetMethod("Measure")!.Invoke(null, [array])!;
        }
        return new InputObjects(document, mode);
    }

    private static Dictionary<string, object?> Base(CorpusItem item, string revision, string docx, string pdf, string mode) => new()
    {
        ["schemaVersion"] = 1, ["documentId"] = item.Id, ["revision"] = revision, ["mode"] = mode,
        ["docxSha256"] = Sha256(docx), ["pdfSha256"] = Sha256(pdf), ["providerCalls"] = 0,
        ["networkEnabled"] = false, ["liveLlm"] = false, ["liveVlm"] = false,
    };

    private static Dictionary<string, object?> Project(object? value, params string[] properties) => properties.ToDictionary(
        property => JsonName(property), property => value is null ? null : Prop(value, property), StringComparer.Ordinal);

    private static string JsonName(string name) => name switch
    {
        "TableOfContentsParagraphs" => "tocAdjacentCount", "TypedNumberSegments" => "typedSegments",
        "TitlePollutionRate" => "pollutionRate", "LevelJumpRate" => "jumpRate", _ => char.ToLowerInvariant(name[0]) + name[1..]
    };

    private static object? Prop(object value, string name) => value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(value);
    private static IEnumerable<object> AsObjects(object? value) => value is System.Collections.IEnumerable enumerable ? enumerable.Cast<object>() : [];
    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private sealed record InputObjects(object Document, object Mode);
    private sealed record Corpus(CorpusItem[] Items);
    private sealed record CorpusItem(string Id, string Docx, string Pdf, string DocxSha256, string PdfSha256, string[] EnabledFor, string[] VisualProbeTexts);
}
