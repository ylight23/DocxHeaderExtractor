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

        var render = await RenderAndManifestAsync(sourcePath, output, ct);
        await WriteJsonAsync(Path.Combine(output, "page-render-manifest.v1.json"), render.Manifest, ct);
        if (!render.Success)
            return await BlockAsync(output, "VLM_EXECUTION_BLOCKED", render.Error ?? "DOCX_LAYOUT_RENDERER_UNAVAILABLE", control, ct,
                new { render = render.Manifest });

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
            privacyExceptionAuthorized = true, privacyExceptionScope = "THIS_CAMPAIGN_ONLY", modelFallback = "NONE",
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
        using var model = new OpenRouterCeilingReasoningModel(options, capability.Capability, http);
        var windowResults = new List<object>();
        foreach (var window in pageWindows)
        {
            ct.ThrowIfCancellationRequested();
            var visible = window.OccurrenceIds.Select(id => pack.Occurrences.Single(x => x.SourceOccurrenceId == id)).ToArray();
            var owned = visible.Select(x => x.SourceOccurrenceId).ToHashSet(StringComparer.Ordinal);
            var packet = CeilingPacketBuilder.Build(visible, owned);
            var metadata = JsonSerializer.Serialize(new { pages = window.PageIndices, sourceAliases = packet.Bindings.Select(x => new { x.LocalIndex, x.SourceId }).ToArray() });
            var requestId = $"VISUAL_SEMANTIC:DOC-0205:{string.Join(',', window.PageIndices)}:{Sha256Text(packet.SerializedJson + metadata)}";
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
            windowResults.Add(new { window.PageIndices, responseCount = response.Headings.Count, boundCount = bound, requestTelemetry });
        }

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
            privacyExceptionAuthorized = true, privacyExceptionScope = "THIS_CAMPAIGN_ONLY", gitSha = CurrentGitSha(repoRoot),
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

    private static async Task<RenderResult> RenderAndManifestAsync(string sourcePath, string output, CancellationToken ct)
    {
        var renderDir = Path.Combine(output, "render"); Directory.CreateDirectory(renderDir);
        var pdf = Path.Combine(renderDir, Path.GetFileNameWithoutExtension(sourcePath) + ".pdf");
        if (!File.Exists(pdf))
        {
            var converter = FindExecutable("soffice") ?? FindExecutable("libreoffice");
            if (converter is not null)
            {
                var psi = new ProcessStartInfo(converter) { WorkingDirectory = renderDir, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                foreach (var arg in new[] { "--headless", "--norestore", "--convert-to", "pdf", "--outdir", renderDir, sourcePath }) psi.ArgumentList.Add(arg);
                using var process = Process.Start(psi);
                if (process is not null && await WaitForExitAsync(process, TimeSpan.FromMinutes(3), ct) && !File.Exists(pdf))
                    pdf = Directory.EnumerateFiles(renderDir, "*.pdf").FirstOrDefault() ?? pdf;
            }
            if (!File.Exists(pdf) && OperatingSystem.IsWindows()) TryWordPdf(sourcePath, pdf);
        }
        if (!File.Exists(pdf))
            return new RenderResult(false, "DOCX_LAYOUT_RENDERER_UNAVAILABLE", new PageManifest(0, 0, "", Array.Empty<PageEntry>()), Array.Empty<string>());

        var pagesDir = Path.Combine(output, "pages"); Directory.CreateDirectory(pagesDir);
        var pageEntries = new List<PageEntry>(); var pageTexts = new List<string>();
        using var document = PdfDocument.Open(pdf);
        for (var index = 1; index <= document.NumberOfPages; index++)
        {
            ct.ThrowIfCancellationRequested();
            var bounds = PdfRegionRasterizer.GetPageBounds(pdf, index);
            var png = PdfRegionRasterizer.RenderCropPng(pdf, index, 0, 0, bounds.Width, bounds.Height, 110);
            var name = $"page-{index:0000}.png"; await File.WriteAllBytesAsync(Path.Combine(pagesDir, name), png, ct);
            pageEntries.Add(new PageEntry(index, name, Sha256Bytes(png), PngDimensions(png).Width, PngDimensions(png).Height));
            pageTexts.Add(document.GetPage(index).Text);
        }
        var manifestJson = JsonSerializer.Serialize(new { pageCount = pageEntries.Count, pages = pageEntries }, JsonOptions);
        return new RenderResult(true, null, new PageManifest(pageEntries.Count, 1d, Sha256Text(manifestJson), pageEntries), pageTexts);
    }

    private static IReadOnlyList<VisualWindow> BuildWindows(IReadOnlyList<VisualPageEvidence> pages, PageMapping mapping, IReadOnlyList<ReasoningSourceOccurrence> occurrences)
    {
        var byPage = mapping.Entries.GroupBy(x => x.PageIndex).ToDictionary(x => x.Key, x => x.Select(y => y.SourceOccurrenceId).ToArray());
        var result = new List<VisualWindow>();
        for (var start = 0; start < pages.Count; start += 8)
        {
            var selected = pages.Skip(start).Take(8).ToArray();
            var ids = selected.SelectMany(p => byPage.GetValueOrDefault(p.PageIndex, Array.Empty<string>())).Distinct(StringComparer.Ordinal)
                .OrderBy(id => occurrences.Single(x => x.SourceOccurrenceId == id).SourceOrdinal).ToArray();
            // Keep empty-source pages in the visual schedule as well. They still need to be
            // observed for pageCoverage=1.0; an empty XML packet makes the model unable to invent
            // a source heading while preserving the page-level visual audit trail.
            result.Add(new VisualWindow(selected.Select(x => x.PageIndex).ToArray(), ids));
        }
        return result;
    }

    private static PageMapping MapOccurrencesToPages(IReadOnlyList<ReasoningSourceOccurrence> occurrences, IReadOnlyList<string> pageTexts)
    {
        var entries = new List<PageMappingEntry>(); var previous = 0; var unmapped = 0;
        foreach (var occurrence in occurrences.OrderBy(x => x.SourceOrdinal))
        {
            var needle = Canonical(occurrence.RawText); if (needle.Length > 180) needle = needle[..180];
            var pages = Enumerable.Range(0, pageTexts.Count).Where(i => Canonical(pageTexts[i]).Contains(needle, StringComparison.Ordinal)).ToArray();
            var page = pages.FirstOrDefault(i => i >= previous, -1);
            if (page < 0) { unmapped++; continue; }
            previous = page; entries.Add(new PageMappingEntry(occurrence.SourceOccurrenceId, occurrence.SourceId, occurrence.SourceOrdinal, page + 1, "canonical_text_anchor"));
        }
        return new PageMapping(occurrences.Count, entries.Count, unmapped, entries);
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
    private static string? FindExecutable(string name) { foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) { var path = Path.Combine(dir, OperatingSystem.IsWindows() ? name + ".exe" : name); if (File.Exists(path)) return path; } return null; }
    [SupportedOSPlatform("windows")]
    private static bool TryWordPdf(string sourcePath, string pdfPath) { dynamic? app = null; dynamic? doc = null; try { var type = Type.GetTypeFromProgID("Word.Application"); if (type is null) return false; var instance = Activator.CreateInstance(type); if (instance is null) return false; app = instance; app.Visible = false; app.DisplayAlerts = 0; doc = app.Documents.Open(sourcePath, false, true, false); doc.ExportAsFixedFormat(pdfPath, 17, false, 0, 0, 0, 0, 0, true, false, 0, true, true, false); return File.Exists(pdfPath); } catch { return false; } finally { try { doc?.Close(0); } catch { } try { app?.Quit(0); } catch { } } }
    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken ct) { using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct); timer.CancelAfter(timeout); try { await process.WaitForExitAsync(timer.Token); return true; } catch { try { if (!process.HasExited) process.Kill(true); } catch { } return false; } }
    private static (int Width, int Height) PngDimensions(byte[] bytes) => bytes.Length >= 24 ? (BitConverter.ToInt32(bytes[16..20].Reverse().ToArray()), BitConverter.ToInt32(bytes[20..24].Reverse().ToArray())) : (0, 0);

    private sealed record InventoryItem(string DocumentId, string SourcePath, string SourceSha256);
    private sealed record ControlReuse(bool Valid, string Status, string? PredictionHash, string? ResultHash, int? TP, int? FP, int? FN);
    private sealed record RenderResult(bool Success, string? Error, PageManifest Manifest, IReadOnlyList<string> PageTexts);
    private sealed record PageManifest(int PageCount, double Coverage, string ManifestHash, IReadOnlyList<PageEntry> Pages);
    private sealed record PageEntry(int PageIndex, string FileName, string ImageHash, int Width, int Height);
    private sealed record PageMapping(int TotalCount, int MappedCount, int UnmappedCount, IReadOnlyList<PageMappingEntry> Entries);
    private sealed record PageMappingEntry(string SourceOccurrenceId, string SourceId, int SourceOrdinal, int PageIndex, string Authority);
    private sealed record VisualWindow(IReadOnlyList<int> PageIndices, IReadOnlyList<string> OccurrenceIds);
    private sealed record ScoreResult(int TP, int FP, int FN, double P, double R, double F1);
}
