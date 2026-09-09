using System.Diagnostics;
using System.Drawing;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Policy;
using DocxHeaderExtractor.DocumentProcessing.Vision;
using DocxHeaderExtractor.Eval.StrictGoldOccurrence;
using DocxHeaderExtractor.Infrastructure.AI;
using UglyToad.PdfPig;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>DOC-0205 causal visual-evidence experiment. The text-only control is disk-reused;
/// this route adds only rendered page evidence and keeps XML/source spans authoritative.</summary>
public static class OpenRouterQwen37VisualCeilingRunner
{
    private const string Model = "qwen/qwen3.7-flash";
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const string InventoryPath = "eval/a99-dataset/document-inventory.v1.json";
    private const string OutputRoot = "eval/a99-closed-loop/qwen37-flash-visual-ceiling";
    private const string ControlRoot = "eval/a99-closed-loop/qwen37-flash-reasoning-ceiling/DOC-0205/r1-ceiling";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public const string VisualPromptContract = "VISUAL_EVIDENCE_SUPPLEMENTS_XML_SOURCE;SOURCE_XML_IS_CANONICAL;NO_GOLD;NO_CANDIDATE_FILTERING;NO_HEURISTIC_HEADING_RULES";

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(output);
        var item = ReadInventory(Path.Combine(repoRoot, InventoryPath), "DOC-0205");
        var control = VerifyFrozenControl(repoRoot);
        await WriteJsonAsync(Path.Combine(output, "text-control-reuse.v1.json"), control, ct);
        if (!control.Valid)
            return await BlockAsync(output, "VLM_EXECUTION_BLOCKED", "TEXT_CONTROL_HASH_OR_SCORE_MISMATCH", control, ct);

        var sourcePath = Path.Combine(repoRoot, item.SourcePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(sourcePath) || !string.Equals(Sha256File(sourcePath), item.SourceSha256, StringComparison.OrdinalIgnoreCase))
            return await BlockAsync(output, "VLM_EXECUTION_BLOCKED", "SOURCE_HASH_MISMATCH", control, ct);

        var renderer = DiscoverRenderer();
        await WriteJsonAsync(Path.Combine(output, "renderer.v1.json"), renderer, ct);
        var render = await RenderAndManifestAsync(sourcePath, output, renderer, ct);
        await WriteJsonAsync(Path.Combine(output, "page-render-manifest.v1.json"), render.Manifest, ct);
        if (!render.Success)
            return await BlockAsync(output, "DOCX_RENDERER_INSTALLATION_REQUIRED", render.Error ?? "DOCX_LAYOUT_RENDERER_UNAVAILABLE", control, ct,
                new { renderer, render = render.Manifest });

        var source = new OpenXmlDocumentSource().Read(sourcePath) with { DocumentId = item.DocumentId };
        var features = NumberingStyleFeatures.FromSourceDocument(source);
        var derived = new DocumentFeatureDeriver().Derive(source);
        var policy = DocxPolicyStateBuilder.Build(source, features, derived, new PipelineOptions { DisableLlm = false }.Extraction);
        var maxPrompt = 180_000;
        var pack = ReasoningContextBuilder.Build(source, policy, maxPrompt, maxPrompt, expandOwnedPerOccurrence: false);
        var mapping = MapOccurrencesToPages(pack.Occurrences, render.PageTexts);
        await WriteJsonAsync(Path.Combine(output, "source-page-mapping.v1.json"), mapping, ct);
        if (mapping.UnmappedCount > 0)
            return await BlockAsync(output, "VLM_EXECUTION_BLOCKED", "SOURCE_PAGE_MAPPING_INCOMPLETE", control, ct,
                new { render = render.Manifest, mapping });

        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            return await BlockAsync(output, "VLM_EXECUTION_BLOCKED", "OPENROUTER_API_KEY_MISSING", control, ct,
                new { render = render.Manifest, mapping });

        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(repoRoot, "qwen37-flash-visual-ceiling", "DOC-0205", ct);
        var options = new RemoteInferenceOptions
        {
            Endpoint = new Uri(Endpoint), Model = Model, ApiKey = key, ContextSize = 1_000_000,
            MaxOutputTokens = 48_000, RequestTimeoutSeconds = 600, TransientRequestRetries = 0,
            MaxParallelRequests = 1, SendChatTemplateKwargs = false, OpenRouterAllowNonZdrPublicBenchmark = true,
        };
        var capability = await OpenRouterModelCapabilityResolver.ResolveAsync(options, http, ct);
        await WriteJsonAsync(Path.Combine(output, "campaign-manifest.v1.json"), new
        {
            schemaVersion = "a99-qwen37-flash-visual-ceiling-v1", model = Model, provider = "OpenRouter",
            documentId = "DOC-0205", dataClassification = "PUBLIC", zdrRequested = false,
            privacyExceptionAuthorized = true, privacyExceptionScope = "FLASH_VISUAL_CEILING_ONLY", modelFallback = "NONE",
            visualPromptContract = VisualPromptContract, pageCount = render.Manifest.PageCount,
            pageCoverage = 1d, sourceOwnershipCoverage = mapping.MappedCount / (double)Math.Max(1, mapping.TotalCount),
            capability, goldReadBeforeFreeze = false, startedUtc = DateTimeOffset.UtcNow,
        }, ct);
        if (!capability.Available || capability.Capability is null || !capability.Capability.ReasoningSupported ||
            !capability.Capability.StructuredOutputSupported || !string.Equals(capability.Capability.ModelId, Model, StringComparison.Ordinal))
            return await BlockAsync(output, "VLM_EXECUTION_BLOCKED", "MODEL_CAPABILITY_MISMATCH", control, ct,
                new { render = render.Manifest, mapping, capability });

        var pages = render.Manifest.Pages.Select(page => new VisualPageEvidence(page.PageIndex, page.ImageHash,
            File.ReadAllBytes(Path.Combine(output, "pages", page.FileName)))).ToArray();
        var proposals = new List<ReasoningHeadingProposal>();
        var telemetry = new List<RequestPacketTelemetry>();
        var pageWindows = BuildWindows(pages, mapping, pack.Occurrences);
        var packetAudit = BuildPacketAudit(pageWindows, pages, mapping, pack.Occurrences);
        await WriteJsonAsync(Path.Combine(output, "visual-packet-audit.v1.json"), new
        {
            invariant = "8_PAGE_WINDOW_TO_1_PAGE_WINDOW_MUST_REDUCE_MODEL_VISIBLE_WORKLOAD",
            windows = packetAudit,
            goldReadBeforeFreeze = false,
        }, ct);
        using var model = new OpenRouterCeilingReasoningModel(options, capability.Capability, http);
        var windowResults = new List<object>();
        var unresolvedWindows = new List<object>();
        var pendingWindows = new Queue<VisualWindow>(pageWindows);
        while (pendingWindows.Count > 0)
        {
            var window = pendingWindows.Dequeue();
            ct.ThrowIfCancellationRequested();
            var visible = window.OccurrenceIds.Select(id => pack.Occurrences.Single(x => x.SourceOccurrenceId == id)).ToArray();
            var owned = visible.Select(x => x.SourceOccurrenceId).ToHashSet(StringComparer.Ordinal);
            var packet = CeilingPacketBuilder.Build(visible, owned, window.VisibleRanges, window.OwnedRanges);
            var metadata = JsonSerializer.Serialize(new { pages = window.PageIndices, sourceAliases = packet.Bindings.Select(x => new { x.LocalIndex, x.SourceId }).ToArray() });
            var requestId = $"VISUAL_SEMANTIC:DOC-0205:{string.Join(',', window.PageIndices)}:{Sha256Text(packet.SerializedJson + metadata)}";
            var pageEvidence = pages.Where(x => window.PageIndices.Contains(x.PageIndex)).ToArray();
            var packetMetrics = BuildPacketMetrics(window, packet, pageEvidence);
            try
            {
                var (response, requestTelemetry) = await model.CompleteVisualSemanticAsync("DOC-0205", ReasoningRoute.ModelCapabilityCeiling.ToString(),
                    requestId, packet.SerializedJson + "\nVISUAL_WINDOW_METADATA=" + metadata, pages.Where(x => window.PageIndices.Contains(x.PageIndex)).ToArray(),
                    packet.SourceTextCharacters, owned.Count, visible.Length, ct);
                telemetry.Add(requestTelemetry);
                var bound = 0;
                foreach (var heading in response.Headings)
                {
                    var binding = CeilingProposalBinder.ResolveBinding(heading.I, packet.Bindings, owned);
                    if (binding is null || !binding.TryBind(heading.Start, heading.End, out var start, out var end, out var isOwned) || !isOwned) continue;
                    var occurrence = visible.Single(x => x.SourceOccurrenceId == binding.SourceOccurrenceId);
                    proposals.Add(new ReasoningHeadingProposal
                    {
                        SourceId = occurrence.SourceId, HeadingSpan = new StructuralSpan(start, end),
                        Text = occurrence.RawText[start..end], SemanticRole = heading.Role, Confidence = 1,
                    });
                    bound++;
                }
                windowResults.Add(new { window.PageIndices, status = "SUCCESS", packetMetrics, responseCount = response.Headings.Count, boundCount = bound, requestTelemetry });
                await WriteJsonAsync(Path.Combine(output, "windows", $"window-{window.PageIndices[0]:0000}-{window.PageIndices[^1]:0000}.v1.json"),
                    new { pageIndices = window.PageIndices, sourceOccurrenceIds = window.OccurrenceIds, packetMetrics, response.Headings, boundCount = bound, requestTelemetry, goldReadBeforeFreeze = false }, ct);
            }
            catch (Exception ex) when (ex is ReasoningCompletionException or FormatException or HttpRequestException or JsonException or InvalidOperationException)
            {
                var requestTelemetry = model.Telemetry.LastOrDefault();
                var failureTelemetry = BuildFailureTelemetry(requestTelemetry, ex);
                if (window.PageIndices.Count > 1)
                {
                    foreach (var pageIndex in window.PageIndices)
                    {
                        pendingWindows.Enqueue(BuildSinglePageWindow(pageIndex, pages, mapping, pack.Occurrences));
                    }
                }
                else
                {
                    unresolvedWindows.Add(new { pageIndices = window.PageIndices, status = "FAILED", error = ex.Message, packetMetrics, failureTelemetry });
                }
                windowResults.Add(new { window.PageIndices, status = "FAILED", packetMetrics, error = ex.Message, recovery = window.PageIndices.Count > 1 ? "SPLIT_TO_SINGLE_PAGE" : "UNRESOLVED", failureTelemetry });
                await WriteJsonAsync(Path.Combine(output, "windows", $"window-{window.PageIndices[0]:0000}-{window.PageIndices[^1]:0000}.failed.v1.json"),
                    new { pageIndices = window.PageIndices, status = "FAILED", packetMetrics, error = ex.Message, recovery = window.PageIndices.Count > 1 ? "SPLIT_TO_SINGLE_PAGE" : "UNRESOLVED", failureTelemetry, goldReadBeforeFreeze = false }, ct);
            }
        }
        if (unresolvedWindows.Count > 0)
            return await BlockAsync(output, "VLM_EXECUTION_BLOCKED", "VISUAL_WINDOWS_UNRESOLVED", control, ct,
                new { renderer, render = render.Manifest, mapping, unresolvedWindows, windowResults, telemetry = model.Telemetry, goldReadBeforeFreeze = false });

        var deduped = proposals.GroupBy(x => $"{x.SourceId}:{x.HeadingSpan.Start}:{x.HeadingSpan.End}", StringComparer.Ordinal)
            .Select(x => x.OrderByDescending(p => p.Confidence).ThenBy(p => p.SemanticRole, StringComparer.Ordinal).First()).ToArray();
        var materialized = ReasoningProposalMaterializer.Materialize(source, policy, deduped);
        var structureProjection = ReasoningTaskProjection.Project(materialized.Structure).ToDictionary(x => x.ProposalId, StringComparer.Ordinal);
        var projection = materialized.Validated.Select(row => structureProjection.GetValueOrDefault(row.ElementId) ??
            new ReasoningProjectionDecision(row.ElementId, ReasoningTaskProjection.Excluded, "VALIDATION_REJECTED")).ToArray();
        var finalElements = ReasoningTaskProjection.ProjectContentHeadings(materialized.Structure)
            .OrderBy(x => x.Sources.Single().SourceOrdinal).ThenBy(x => x.Sources.Single().Span.Start).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
        var prediction = new
        {
            documentId = "DOC-0205", model = Model, mode = "TEXT_PLUS_VISUAL", executionMode = "PAGE_WINDOW_FULL_COVERAGE",
            sourceSha256 = item.SourceSha256, pageRenderManifestSha256 = Sha256File(Path.Combine(output, "page-render-manifest.v1.json")),
            mappingSha256 = Sha256File(Path.Combine(output, "source-page-mapping.v1.json")), visualPromptContract = VisualPromptContract,
            rawProposalCount = proposals.Count, validatedSemanticCount = materialized.Validated.Count(x => x.Accepted),
            proposals = deduped, projection, headings = finalElements, goldReadBeforeFreeze = false,
        };
        var result = new { documentId = "DOC-0205", model = Model, mode = "TEXT_PLUS_VISUAL", headings = finalElements, goldReadBeforeFreeze = false };
        var predictionPath = Path.Combine(output, "DOC-0205", "visual", "prediction.v1.json");
        var resultPath = Path.Combine(output, "DOC-0205", "visual", "result.v1.json");
        Directory.CreateDirectory(Path.GetDirectoryName(predictionPath)!);
        await WriteJsonAsync(predictionPath, prediction, ct); await WriteJsonAsync(resultPath, result, ct);
        var predictionHash = Sha256File(predictionPath); var resultHash = Sha256File(resultPath);
        var freeze = new
        {
            documentId = "DOC-0205", model = Model, actualProvider = telemetry.Select(x => x.ProviderRoute).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "NOT_EXPOSED",
            reasoningConfiguration = new { requested = true, enabled = true, exclude = true }, dataClassification = "PUBLIC", zdrRequested = false,
            privacyExceptionAuthorized = true, privacyExceptionScope = "FLASH_VISUAL_CEILING_ONLY", gitSha = CurrentGitSha(repoRoot),
            sourceSha256 = item.SourceSha256, pageRenderManifestSha256 = Sha256File(Path.Combine(output, "page-render-manifest.v1.json")),
            mappingSha256 = Sha256File(Path.Combine(output, "source-page-mapping.v1.json")), predictionSha256 = predictionHash,
            resultSha256 = resultHash, promptHash = Sha256Text(CeilingSemanticPrompt.ProtocolVersion + "\n" + CeilingSemanticPrompt.System + "\n" + VisualPromptContract),
            providerAttempts = model.ProviderCalls, reasoningTokens = telemetry.Sum(x => x.ReportedReasoningTokens ?? 0),
            inputTokens = telemetry.Sum(x => x.ReportedInputTokens ?? 0), outputTokens = telemetry.Sum(x => x.ReportedOutputTokens ?? 0),
            finishReasons = telemetry.Select(x => x.FinishReason).ToArray(), telemetry, goldReadBeforeFreeze = false, frozenUtc = DateTimeOffset.UtcNow,
        };
        var freezePath = Path.Combine(output, "DOC-0205", "visual", "freeze.v1.json");
        await WriteJsonAsync(freezePath, freeze, ct);
        if (Sha256File(predictionPath) != predictionHash || Sha256File(resultPath) != resultHash)
            return await BlockAsync(output, "VLM_EXECUTION_BLOCKED", "FREEZE_HASH_VERIFICATION_FAILED", control, ct);

        var goldPath = Path.Combine(repoRoot, "eval", "a99-closed-loop", "strict-gold-occurrence-v1", "DOC-0205.occurrence-gold-v1.json");
        var gold = ReasoningGoldArtifactLoader.LoadOccurrence(goldPath);
        var goldKeys = gold.Where(x => x.HeadingSpan is not null).Select(x => Key(x.SourceId, x.HeadingSpan!)).ToArray();
        var predictionKeys = finalElements.Select(x => Key(x.Sources.Single().SourceId, x.Sources.Single().Span)).ToArray();
        var score = Score(goldKeys, predictionKeys);
        var losses = ClassifyLosses(gold, deduped, materialized, finalElements, projection).GroupBy(x => x).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        var systemLoss = losses.Where(x => x.Key.StartsWith("SYSTEM_", StringComparison.Ordinal)).Sum(x => x.Value);
        await WriteJsonAsync(Path.Combine(output, "DOC-0205", "visual", "score.v1.json"), new
        {
            documentId = "DOC-0205", exactStatus = "EVALUABLE", tp = score.TP, fp = score.FP, fn = score.FN,
            precision = score.P, recall = score.R, f1 = score.F1, lossCounts = losses, systemVisualAlignmentLoss = 0,
            systemBindingLoss = losses.GetValueOrDefault("SYSTEM_BINDING_LOSS"), systemValidatorLoss = losses.GetValueOrDefault("SYSTEM_VALIDATOR_LOSS"),
            systemProjectionLoss = losses.GetValueOrDefault("SYSTEM_PROJECTION_LOSS"), systemLossCount = systemLoss, goldReadBeforeFreeze = false,
        }, ct);
        await WriteJsonAsync(Path.Combine(output, "DOC-0205", "visual", "first-loss.v1.json"), new { documentId = "DOC-0205", losses, goldReadBeforeFreeze = false }, ct);
        var delta = new { deltaTP = score.TP, deltaFP = score.FP - 73, deltaFN = score.FN - 71, deltaRecall = score.R - 0d, deltaF1 = score.F1, deltaModelOmission = losses.GetValueOrDefault("MODEL_OMISSION") - 70 };
        var classification = score.TP >= 10 && score.FN + 10 < 71 && score.F1 > 0.1
            ? "VISUAL_EVIDENCE_RECOVERS_STRUCTURE"
            : score.TP > 0 && score.F1 < .769231 ? "VISUAL_EVIDENCE_RECALL_UP_PRECISION_TRADEOFF" : "VISUAL_EVIDENCE_NO_MATERIAL_GAIN";
        await WriteJsonAsync(Path.Combine(output, "comparison.v1.json"), new
        {
            schemaVersion = "a99-qwen37-flash-visual-ceiling-v1", textControl = new { tp = 0, fp = 73, fn = 71, f1 = 0, modelOmission = 70, spanError = 1, systemLoss = 0, frozen = true },
            visual = new { tp = score.TP, fp = score.FP, fn = score.FN, precision = score.P, recall = score.R, f1 = score.F1, lossCounts = losses },
            delta, pageCoverage = 1d, sourceOwnershipCoverage = 1d, unmappedVisualRegions = 0, providerNotHeldConstant = false,
            classification, goldReadBeforeFreeze = false, windowResults,
        }, ct);
        await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new
        {
            schemaVersion = "a99-qwen37-flash-visual-ceiling-v1", primaryClassification = classification, model = Model,
            textControl = new { tp = 0, fp = 73, fn = 71, f1 = 0, modelOmission = 70, spanError = 1, systemLoss = 0 },
            visual = new { tp = score.TP, fp = score.FP, fn = score.FN, precision = score.P, recall = score.R, f1 = score.F1, modelOmission = losses.GetValueOrDefault("MODEL_OMISSION"), spanError = losses.GetValueOrDefault("MODEL_SPAN_ERROR"), systemLoss },
            delta, pageCount = render.Manifest.PageCount, pageCoverage = 1d, sourceOwnershipCoverage = 1d, unmappedVisualRegions = 0,
            reasoningTokens = telemetry.Sum(x => x.ReportedReasoningTokens ?? 0), wallTimeMs = telemetry.Sum(x => x.ElapsedMs),
            goldReadBeforeFreeze = false, completedUtc = DateTimeOffset.UtcNow,
        }, ct);
        Console.WriteLine($"FINAL_CLASSIFICATION={classification}");
        return 0;
    }

    public static IReadOnlyList<string> DeduplicateCanonicalKeys(IEnumerable<string> keys) => keys.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
    public static bool VisualPromptKeepsGoldOut(string prompt) => !prompt.Contains("gold", StringComparison.OrdinalIgnoreCase);

    private static async Task<RenderResult> RenderAndManifestAsync(string sourcePath, string output, RendererAudit renderer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(renderer.Executable))
            return new RenderResult(false, "DOCX_LAYOUT_RENDERER_UNAVAILABLE", EmptyManifest(sourcePath), Array.Empty<string>(), renderer);
        var renderDir = Path.Combine(output, "render"); Directory.CreateDirectory(renderDir);
        var firstDir = Path.Combine(renderDir, "pass-1"); var secondDir = Path.Combine(renderDir, "pass-2");
        var firstPdf = await RenderOnceAsync(sourcePath, firstDir, renderer, ct);
        var secondPdf = await RenderOnceAsync(sourcePath, secondDir, renderer, ct);
        if (firstPdf is null || secondPdf is null)
            return new RenderResult(false, "DOCX_RENDERER_EXECUTION_FAILED", EmptyManifest(sourcePath), Array.Empty<string>(), renderer);

        var pagesDir = Path.Combine(output, "pages"); Directory.CreateDirectory(pagesDir);
        var repeatDir = Path.Combine(renderDir, "repeat-pages"); Directory.CreateDirectory(repeatDir);
        var first = await RenderPagesAsync(firstPdf, pagesDir, "page", ct);
        var second = await RenderPagesAsync(secondPdf, repeatDir, "page", ct);
        var deterministic = first.Entries.Count > 0 && first.Entries.Count == second.Entries.Count && first.Entries.Zip(second.Entries).All(pair =>
            pair.First.Width == pair.Second.Width && pair.First.Height == pair.Second.Height && pair.First.ImageHash == pair.Second.ImageHash);
        var reason = deterministic ? "same_page_count_dimensions_and_pixel_hashes" : "page_count_dimensions_or_pixel_hash_mismatch";
        var manifestJson = JsonSerializer.Serialize(new { sourceDocxSha256 = Sha256File(sourcePath), renderedPdfSha256 = Sha256File(firstPdf), pageCount = first.Entries.Count, pages = first.Entries }, JsonOptions);
        var manifest = new PageManifest(first.Entries.Count, first.Entries.Count > 0 ? 1d : 0d, Sha256Text(manifestJson), first.Entries,
            Sha256File(sourcePath), Sha256File(firstPdf), deterministic, reason);
        if (!deterministic)
            return new RenderResult(false, "RENDER_INTEGRITY_FAILURE", manifest, first.PageTexts, renderer);
        return new RenderResult(true, null, manifest, first.PageTexts, renderer);
    }

    private static PageManifest EmptyManifest(string sourcePath) => new(0, 0, "", Array.Empty<PageEntry>(), File.Exists(sourcePath) ? Sha256File(sourcePath) : "", "", false, "renderer_unavailable");

    private static async Task<string?> RenderOnceAsync(string sourcePath, string outputDir, RendererAudit renderer, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir);
        var expected = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(sourcePath) + ".pdf");
        if (renderer.Name == "LibreOffice")
        {
            var psi = new ProcessStartInfo(renderer.Executable!) { WorkingDirectory = outputDir, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "--headless", "--norestore", "--convert-to", "pdf", "--outdir", outputDir, sourcePath }) psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi);
            if (process is null || !await WaitForExitAsync(process, TimeSpan.FromMinutes(3), ct)) return null;
        }
        else if (renderer.Name == "Microsoft Word" && OperatingSystem.IsWindows())
        {
            if (!TryWordPdf(sourcePath, expected)) return null;
        }
        else if (renderer.Name == "Microsoft Word") return null;
        return File.Exists(expected) ? expected : Directory.EnumerateFiles(outputDir, "*.pdf").FirstOrDefault();
    }

    private static async Task<RenderedPages> RenderPagesAsync(string pdf, string outputDir, string prefix, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir); var entries = new List<PageEntry>(); var texts = new List<string>();
        using var document = PdfDocument.Open(pdf);
        for (var index = 1; index <= document.NumberOfPages; index++)
        {
            ct.ThrowIfCancellationRequested(); var bounds = PdfRegionRasterizer.GetPageBounds(pdf, index);
            var png = PdfRegionRasterizer.RenderCropPng(pdf, index, 0, 0, bounds.Width, bounds.Height, 110);
            var name = $"{prefix}-{index:0000}.png"; await File.WriteAllBytesAsync(Path.Combine(outputDir, name), png, ct);
            var dimensions = PngDimensions(png); entries.Add(new PageEntry(index, name, Sha256Bytes(png), dimensions.Width, dimensions.Height)); texts.Add(document.GetPage(index).Text);
        }
        return new RenderedPages(entries, texts);
    }

    private static IReadOnlyList<VisualWindow> BuildWindows(IReadOnlyList<VisualPageEvidence> pages, PageMapping mapping, IReadOnlyList<ReasoningSourceOccurrence> occurrences)
    {
        var result = new List<VisualWindow>();
        for (var start = 0; start < pages.Count; start += 8)
        {
            var selected = pages.Skip(start).Take(8).ToArray();
            var entries = mapping.Entries.Where(x => selected.Any(p => p.PageIndex == x.PageIndex)).ToArray();
            var ranges = entries.GroupBy(x => x.SourceOccurrenceId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (g.Min(x => x.VisibleStartCharacter), g.Max(x => x.VisibleEndCharacter)), StringComparer.Ordinal);
            var ids = ranges.Keys.OrderBy(id => occurrences.Single(x => x.SourceOccurrenceId == id).SourceOrdinal, Comparer<int>.Default).ToArray();
            // Keep empty-source pages in the visual schedule as well. They still need to be
            // observed for pageCoverage=1.0; an empty XML packet makes the model unable to invent
            // a source heading while preserving the page-level visual audit trail.
            result.Add(new VisualWindow(selected.Select(x => x.PageIndex).ToArray(), ids, ranges, ranges));
        }
        return result;
    }

    private static PageMapping MapOccurrencesToPages(IReadOnlyList<ReasoningSourceOccurrence> occurrences, IReadOnlyList<string> pageTexts)
    {
        var entries = new List<PageMappingEntry>(); var unmapped = 0;
        foreach (var occurrence in occurrences.OrderBy(x => x.SourceOrdinal))
        {
            var canonicalRaw = CanonicalWithOffsets(occurrence.RawText);
            var occurrenceEntries = new List<PageMappingEntry>();
            var cursor = 0;
            for (var page = 0; page < pageTexts.Count; page++)
            {
                var canonicalPage = Canonical(pageTexts[page]);
                if (canonicalPage.Length == 0) continue;
                var anchor = FindAnchor(canonicalRaw.Text, canonicalPage, cursor);
                if (anchor is null) continue;
                var nextCursor = anchor.Value.EndCanonical;
                var rawStart = canonicalRaw.RawOffsets[anchor.Value.StartCanonical];
                var rawEnd = nextCursor < canonicalRaw.RawOffsets.Count ? canonicalRaw.RawOffsets[nextCursor] : occurrence.RawText.Length;
                if (rawEnd <= rawStart) continue;
                occurrenceEntries.Add(new PageMappingEntry(occurrence.SourceOccurrenceId, occurrence.SourceId, occurrence.SourceOrdinal,
                    page + 1, "canonical_text_page_anchor", rawStart, rawEnd, rawStart, rawEnd));
                cursor = Math.Max(cursor, nextCursor);
            }

            if (occurrenceEntries.Count == 0)
            {
                var needle = canonicalRaw.Text.Length > 180 ? canonicalRaw.Text[..180] : canonicalRaw.Text;
                var page = Enumerable.Range(0, pageTexts.Count).FirstOrDefault(i => Canonical(pageTexts[i]).Contains(needle, StringComparison.Ordinal));
                if (needle.Length == 0 || !Canonical(pageTexts[page]).Contains(needle, StringComparison.Ordinal)) { unmapped++; continue; }
                occurrenceEntries.Add(new PageMappingEntry(occurrence.SourceOccurrenceId, occurrence.SourceId, occurrence.SourceOrdinal,
                    page + 1, "canonical_text_anchor", 0, occurrence.RawText.Length, 0, occurrence.RawText.Length));
            }
            entries.AddRange(occurrenceEntries);
        }
        var mapped = entries.Select(x => x.SourceOccurrenceId).Distinct(StringComparer.Ordinal).Count();
        return new PageMapping(occurrences.Count, mapped, unmapped, entries);
    }

    private static VisualWindow BuildSinglePageWindow(int pageIndex, IReadOnlyList<VisualPageEvidence> pages, PageMapping mapping, IReadOnlyList<ReasoningSourceOccurrence> occurrences)
    {
        var entries = mapping.Entries.Where(x => x.PageIndex == pageIndex).ToArray();
        var ranges = entries.GroupBy(x => x.SourceOccurrenceId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (g.Min(x => x.VisibleStartCharacter), g.Max(x => x.VisibleEndCharacter)), StringComparer.Ordinal);
        var ids = ranges.Keys.OrderBy(id => occurrences.Single(x => x.SourceOccurrenceId == id).SourceOrdinal, Comparer<int>.Default).ToArray();
        return new VisualWindow([pageIndex], ids, ranges, ranges);
    }

    private static object BuildPacketMetrics(VisualWindow window, CeilingPacketResult packet, IReadOnlyList<VisualPageEvidence> pages) => new
    {
        pageCountInRequest = window.PageIndices.Count,
        imageCount = pages.Count,
        mappedSourceOccurrences = window.OccurrenceIds.Count,
        visibleSourceChars = packet.SourceTextCharacters,
        ownedSourceChars = window.OwnedRanges.Values.Sum(x => Math.Max(0, x.End - x.Start)),
        packetChars = packet.PacketCharacters,
        estimatedInputTokens = ReasoningTokenBudget.EstimateTokens(packet.PacketCharacters),
        imageDimensions = pages.Select(x => new { pageIndex = x.PageIndex, dimensions = PngDimensionObject(x.PngBytes) }).ToArray(),
    };

    private static IReadOnlyList<object> BuildPacketAudit(IReadOnlyList<VisualWindow> windows,
        IReadOnlyList<VisualPageEvidence> pages, PageMapping mapping, IReadOnlyList<ReasoningSourceOccurrence> occurrences)
    {
        var planned = windows.Select(window => BuildPacketAuditEntry(window, "PLANNED_WINDOW", pages, occurrences));
        var singlePageProbes = pages.Select(page => BuildSinglePageWindow(page.PageIndex, pages, mapping, occurrences)
            ).Select(window => BuildPacketAuditEntry(window, "SINGLE_PAGE_PROBE", pages, occurrences));
        return planned.Concat(singlePageProbes).ToArray();
    }

    private static object BuildPacketAuditEntry(VisualWindow window, string kind,
        IReadOnlyList<VisualPageEvidence> pages, IReadOnlyList<ReasoningSourceOccurrence> occurrences)
    {
        var visible = window.OccurrenceIds.Select(id => occurrences.Single(x => x.SourceOccurrenceId == id)).ToArray();
        var packet = CeilingPacketBuilder.Build(visible, visible.Select(x => x.SourceOccurrenceId).ToHashSet(StringComparer.Ordinal), window.VisibleRanges, window.OwnedRanges);
        return new
        {
            kind,
            pageIndices = window.PageIndices,
            packetMetrics = BuildPacketMetrics(window, packet, pages.Where(x => window.PageIndices.Contains(x.PageIndex)).ToArray()),
            sourceRanges = window.VisibleRanges.Select(x => new { sourceOccurrenceId = x.Key, visibleStart = x.Value.Start, visibleEnd = x.Value.End,
                ownedStart = window.OwnedRanges[x.Key].Start, ownedEnd = window.OwnedRanges[x.Key].End }).ToArray(),
        };
    }

    private static object BuildFailureTelemetry(RequestPacketTelemetry? telemetry, Exception error)
    {
        var finishReason = telemetry?.FinishReason;
        var outputLimitDetected = IsOutputLimit(finishReason) || string.Equals(telemetry?.FailureClass, ReasoningCompletionFailureClass.ProviderOutputLimit, StringComparison.Ordinal);
        var responseContentPresent = telemetry?.ResponseContentPresent;
        var structuredParsed = telemetry?.StructuredOutputParsed;
        var failureClass = outputLimitDetected ? "PROVIDER_OUTPUT_LIMIT"
            : telemetry?.TimeoutDetected == true || telemetry?.StreamStallDetected == true ? "TIMEOUT/STREAM_STALL"
            : responseContentPresent == true && structuredParsed != true ? "STRUCTURED_OUTPUT_TRUNCATED"
            : responseContentPresent == false ? "INCOMPLETE_PROVIDER_RESPONSE"
            : telemetry?.FailureClass ?? error.GetType().Name;
        return new
        {
            finishReason,
            reasoningTokens = telemetry?.ReportedReasoningTokens,
            outputTokens = telemetry?.ReportedOutputTokens,
            responseContentPresent,
            structuredOutputParsed = structuredParsed,
            outputLimitDetected,
            actualProvider = telemetry?.ProviderRoute,
            elapsedMs = telemetry?.ElapsedMs,
            failureClass,
        };
    }

    private static bool IsOutputLimit(string? finishReason) => finishReason is "length" or "max_tokens" or "max_output_tokens";

    private static object PngDimensionObject(byte[] bytes)
    {
        var (width, height) = PngDimensions(bytes);
        return new { width, height };
    }

    private static (string Text, IReadOnlyList<int> RawOffsets) CanonicalWithOffsets(string text)
    {
        var canonical = new StringBuilder();
        var offsets = new List<int>();
        for (var i = 0; i < text.Length; i++)
        {
            foreach (var normalized in text[i].ToString().Normalize(NormalizationForm.FormD))
            {
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(normalized) == System.Globalization.UnicodeCategory.NonSpacingMark || !char.IsLetterOrDigit(normalized)) continue;
                canonical.Append(char.ToLowerInvariant(normalized)); offsets.Add(i);
            }
        }
        return (canonical.ToString(), offsets);
    }

    private static (int StartCanonical, int EndCanonical)? FindAnchor(string rawCanonical, string pageCanonical, int searchStart)
    {
        if (pageCanonical.Length == 0) return null;
        foreach (var length in new[] { 400, 300, 220, 160, 120, 80, 50 })
        {
            if (pageCanonical.Length < length) continue;
            for (var offset = 0; offset <= pageCanonical.Length - length; offset += Math.Max(1, length / 4))
            {
                var needle = pageCanonical.Substring(offset, length);
                var found = rawCanonical.IndexOf(needle, Math.Max(0, searchStart), StringComparison.Ordinal);
                if (found >= 0) return (found, found + length);
            }
        }
        return null;
    }

    private static ControlReuse VerifyFrozenControl(string repoRoot)
    {
        var root = Path.Combine(repoRoot, ControlRoot.Replace('/', Path.DirectorySeparatorChar));
        var freeze = Path.Combine(root, "freeze.v1.json"); var prediction = Path.Combine(root, "prediction.v1.json"); var result = Path.Combine(root, "result.v1.json"); var score = Path.Combine(root, "score.v1.json");
        if (!File.Exists(freeze) || !File.Exists(prediction) || !File.Exists(result) || !File.Exists(score)) return new(false, "MISSING_CONTROL_ARTIFACT", null, null, null, null, null);
        using var freezeJson = JsonDocument.Parse(File.ReadAllText(freeze)); using var scoreJson = JsonDocument.Parse(File.ReadAllText(score));
        var predHash = freezeJson.RootElement.GetProperty("predictionSha256").GetString(); var resultHash = freezeJson.RootElement.GetProperty("resultSha256").GetString();
        var ok = string.Equals(predHash, Sha256File(prediction), StringComparison.OrdinalIgnoreCase) && string.Equals(resultHash, Sha256File(result), StringComparison.OrdinalIgnoreCase) &&
            scoreJson.RootElement.GetProperty("tp").GetInt32() == 0 && scoreJson.RootElement.GetProperty("fp").GetInt32() == 73 && scoreJson.RootElement.GetProperty("fn").GetInt32() == 71 && !freezeJson.RootElement.GetProperty("goldReadBeforeFreeze").GetBoolean();
        return new(ok, ok ? "FROZEN_CONTROL_REUSED" : "CONTROL_MISMATCH", Sha256File(prediction), Sha256File(result), 0, 73, 71);
    }

    private static IEnumerable<string> ClassifyLosses(IReadOnlyList<ReasoningGoldOccurrence> gold, IReadOnlyList<ReasoningHeadingProposal> proposals, (ValidatedStructure Structure, IReadOnlyList<ReasoningValidatedProposal> Validated) materialized, IReadOnlyList<ValidatedStructuralElement> finalElements, IReadOnlyList<ReasoningProjectionDecision> projection)
    {
        var predicted = finalElements.Select(x => Key(x.Sources.Single().SourceId, x.Sources.Single().Span)).ToHashSet(StringComparer.Ordinal); var rows = materialized.Validated.ToDictionary(x => x.ElementId, StringComparer.Ordinal);
        foreach (var item in gold.Where(x => x.HeadingSpan is not null))
        {
            var key = Key(item.SourceId, item.HeadingSpan!); if (predicted.Contains(key)) continue;
            var exact = proposals.FirstOrDefault(p => Key(p.SourceId, p.HeadingSpan) == key);
            if (exact is null) { var near = proposals.Any(p => p.SourceId == item.SourceId && (p.HeadingSpan.Start == item.HeadingSpan!.Start || p.Text.Contains(item.ExactText, StringComparison.Ordinal))); yield return near ? "MODEL_SPAN_ERROR" : "MODEL_OMISSION"; continue; }
            var id = ReasoningProposalMaterializer.ElementId(exact); if (!rows.TryGetValue(id, out var row) || !row.Accepted) yield return "SYSTEM_VALIDATOR_LOSS"; else if (projection.Single(x => x.ProposalId == id).Status == ReasoningTaskProjection.Excluded) yield return "SYSTEM_PROJECTION_LOSS"; else yield return "SYSTEM_BINDING_LOSS";
        }
        foreach (var element in finalElements) if (!gold.Any(item => item.HeadingSpan is not null && Key(item.SourceId, item.HeadingSpan!) == Key(element.Sources.Single().SourceId, element.Sources.Single().Span))) yield return proposals.Any(p => Key(p.SourceId, p.HeadingSpan) == Key(element.Sources.Single().SourceId, element.Sources.Single().Span)) ? "MODEL_FALSE_POSITIVE" : "SYSTEM_BINDING_LOSS";
    }

    private static ScoreResult Score(IReadOnlyList<string> gold, IReadOnlyList<string> predicted) { var g = gold.ToHashSet(StringComparer.Ordinal); var p = predicted.ToHashSet(StringComparer.Ordinal); var tp = g.Intersect(p).Count(); var fp = p.Except(g).Count(); var fn = g.Except(p).Count(); var precision = tp + fp == 0 ? 0d : (double)tp / (tp + fp); var recall = tp + fn == 0 ? 0d : (double)tp / (tp + fn); return new(tp, fp, fn, precision, recall, precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall)); }
    private static string Key(string sourceId, StructuralSpan span) => $"{sourceId}:{span.Start}:{span.End}";
    private static string Canonical(string text) => new(text.Normalize(NormalizationForm.FormD).Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c)).Select(char.ToLowerInvariant).ToArray());
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256Bytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Sha256Text(string value) => Sha256Bytes(Encoding.UTF8.GetBytes(value));
    private static string CurrentGitSha(string root) { try { using var p = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD") { WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false }); return p?.StandardOutput.ReadToEnd().Trim() ?? "UNKNOWN"; } catch { return "UNKNOWN"; } }
    private static InventoryItem ReadInventory(string path, string id) { using var doc = JsonDocument.Parse(File.ReadAllText(path)); var x = doc.RootElement.GetProperty("documents").EnumerateArray().Single(x => x.GetProperty("documentId").GetString() == id); return new(id, x.GetProperty("sourcePath").GetString()!, x.GetProperty("sourceSha256").GetString()!); }
    private static async Task WriteJsonAsync(string path, object value, CancellationToken ct) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, ct); }
    private static async Task<int> BlockAsync(string output, string classification, string reason, ControlReuse control, CancellationToken ct, object? details = null) { await WriteJsonAsync(Path.Combine(output, "summary.v1.json"), new { schemaVersion = "a99-qwen37-flash-visual-ceiling-v1", primaryClassification = classification, reason, control, details, goldReadBeforeFreeze = false, completedUtc = DateTimeOffset.UtcNow }, ct); Console.WriteLine($"FINAL_CLASSIFICATION={classification}:{reason}"); return 1; }
    private static RendererAudit DiscoverRenderer()
    {
        var soffice = FindExecutable("soffice");
        var word = OperatingSystem.IsWindows() && IsWordComAvailable();
        var selected = soffice is not null ? ("LibreOffice", soffice) : word ? ("Microsoft Word", "Word.Application") : ("NONE", (string?)null);
        return new RendererAudit(
            selected.Item1,
            selected.Item2,
            selected.Item2 is not null && File.Exists(selected.Item2) ? FileVersionInfo.GetVersionInfo(selected.Item2).FileVersion : null,
            "NONE", false, false,
            selected.Item1 == "NONE" ? "INSTALLATION_REQUIRED" : "AVAILABLE_PENDING_DETERMINISM");
    }

    private static string? FindExecutable(string name)
    {
        var explicitPath = Environment.GetEnvironmentVariable("DOCX_RENDERER_PATH") ?? Environment.GetEnvironmentVariable("SOFFICE_PATH");
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitPath)) candidates.Add(explicitPath);
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, OperatingSystem.IsWindows() ? name + ".exe" : name)));
        if (OperatingSystem.IsWindows() && name.Equals("soffice", StringComparison.OrdinalIgnoreCase))
            candidates.AddRange([@"C:\Program Files\LibreOffice\program\soffice.exe", @"C:\Program Files (x86)\LibreOffice\program\soffice.exe"]);
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    [SupportedOSPlatform("windows")]
    private static bool IsWordComAvailable() => Type.GetTypeFromProgID("Word.Application") is not null;
    [SupportedOSPlatform("windows")]
    private static bool TryWordPdf(string sourcePath, string pdfPath) { dynamic? app = null; dynamic? doc = null; try { var type = Type.GetTypeFromProgID("Word.Application"); if (type is null) return false; var instance = Activator.CreateInstance(type); if (instance is null) return false; app = instance; app.Visible = false; app.DisplayAlerts = 0; doc = app.Documents.Open(sourcePath, false, true, false); doc.ExportAsFixedFormat(pdfPath, 17, false, 0, 0, 0, 0, 0, true, false, 0, true, true, false); return File.Exists(pdfPath); } catch { return false; } finally { try { doc?.Close(0); } catch { } try { app?.Quit(0); } catch { } } }
    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken ct) { using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct); timer.CancelAfter(timeout); try { await process.WaitForExitAsync(timer.Token); return true; } catch { try { if (!process.HasExited) process.Kill(true); } catch { } return false; } }
    private static (int Width, int Height) PngDimensions(byte[] bytes) => bytes.Length >= 24 ? (BitConverter.ToInt32(bytes[16..20].Reverse().ToArray()), BitConverter.ToInt32(bytes[20..24].Reverse().ToArray())) : (0, 0);

    private sealed record InventoryItem(string DocumentId, string SourcePath, string SourceSha256);
    private sealed record ControlReuse(bool Valid, string Status, string? PredictionHash, string? ResultHash, int? TP, int? FP, int? FN);
    private sealed record RendererAudit(string Name, string? Executable, string? Version, string GemBoxStatus, bool GemBoxDependencyPresent, bool GemBoxLicenseConfigured, string CapabilityStatus);
    private sealed record RenderResult(bool Success, string? Error, PageManifest Manifest, IReadOnlyList<string> PageTexts, RendererAudit Renderer);
    private sealed record RenderedPages(IReadOnlyList<PageEntry> Entries, IReadOnlyList<string> PageTexts);
    private sealed record PageManifest(int PageCount, double Coverage, string ManifestHash, IReadOnlyList<PageEntry> Pages, string SourceDocxSha256, string RenderedPdfSha256, bool Deterministic, string DeterminismReason);
    private sealed record PageEntry(int PageIndex, string FileName, string ImageHash, int Width, int Height);
    private sealed record PageMapping(int TotalCount, int MappedCount, int UnmappedCount, IReadOnlyList<PageMappingEntry> Entries);
    private sealed record PageMappingEntry(string SourceOccurrenceId, string SourceId, int SourceOrdinal, int PageIndex, string Authority,
        int VisibleStartCharacter, int VisibleEndCharacter, int OwnedStartCharacter, int OwnedEndCharacter);
    private sealed record VisualWindow(IReadOnlyList<int> PageIndices, IReadOnlyList<string> OccurrenceIds,
        IReadOnlyDictionary<string, (int Start, int End)> VisibleRanges,
        IReadOnlyDictionary<string, (int Start, int End)> OwnedRanges);
    private sealed record ScoreResult(int TP, int FP, int FN, double P, double R, double F1);
}
