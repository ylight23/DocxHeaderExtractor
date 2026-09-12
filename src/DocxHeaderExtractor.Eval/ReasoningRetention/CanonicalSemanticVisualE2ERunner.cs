using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Vision;
using DocxHeaderExtractor.Infrastructure.AI;
using UglyToad.PdfPig;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>Real-source v6 visual exercise. This is a capability/lineage probe, not a Gold
/// benchmark: sources are selected by modality shape and Gold is never read. Every model call is
/// made by CanonicalSemanticProductionEntryPoint.RunAsync.</summary>
public static class CanonicalSemanticVisualE2ERunner
{
    private const string Model = "qwen/qwen3.7-flash";
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";
    private const string OutputRoot = "eval/a99-closed-loop/production-v6-visual-e2e";
    private const int MaxVisualPagesPerCase = 2;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static readonly CaseDefinition[] Cases =
    [
        new("DOCX_TEXT_STANDARD", "DOC-0205", "todo10_8/heading_corpus_95_word/01_phap_quy/025_ND_47-2020_Chia_se_du_lieu_so.docx"),
        new("DOCX_VISUAL_ONLY", "DOC-0202", "todo10_8/heading_corpus_95_word/01_phap_quy/022_ND_01-2021_Dang_ky_doanh_nghiep.docx"),
        new("PDF_SCANNED", "PDF-SCAN-022", "todo10_8/heading_corpus_100/01_phap_quy/022_ND_01-2021_Dang_ky_doanh_nghiep.pdf"),
        new("PDF_HYBRID", "PDF-HYBRID-041", "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/041_IBRD_Financial_Statements_June_2025.pdf"),
        new("DOCX_TEXT_VISUAL_DUPLICATE", "DOC-0123", "todo10_8/heading_corpus_100/02_hop_dong_mua_sam/038_WB_Works_DB_SingleStage_NoSEASH_2025.docx"),
        new("DOCX_MULTILINE_VISUAL_LAYOUT", "DOC-0202", "todo10_8/heading_corpus_95_word/01_phap_quy/022_ND_01-2021_Dang_ky_doanh_nghiep.docx"),
    ];

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(output);
        var cases = SelectCases();
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            await WriteJson(output, "summary.v1.json", new { status = "BLOCKED", reason = "OPENROUTER_API_KEY_MISSING", model = Model, goldRead = false }, ct);
            return 1;
        }

        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
        var options = new RemoteInferenceOptions
        {
            Endpoint = new Uri(Endpoint), Model = Model, ApiKey = key, ContextSize = 1_000_000,
            MaxOutputTokens = 48_000, RequestTimeoutSeconds = 600, TransientRequestRetries = 0,
            MaxParallelRequests = 1, SendChatTemplateKwargs = false, OpenRouterAllowNonZdrPublicBenchmark = true,
        };
        var capability = await OpenRouterModelCapabilityResolver.ResolveAsync(options, http, ct);
        if (!capability.Available || capability.Capability is null ||
            !string.Equals(capability.Capability.ModelId, Model, StringComparison.Ordinal) ||
            !capability.Capability.ReasoningSupported || !capability.Capability.StructuredOutputSupported)
        {
            await WriteJson(output, "summary.v1.json", new { status = "BLOCKED", reason = "MODEL_CAPABILITY_MISMATCH", model = Model, capability, goldRead = false }, ct);
            return 1;
        }

        await WriteJson(output, "manifest.v1.json", new
        {
            schemaVersion = "a99-v6-visual-e2e-manifest-v1", model = Model, provider = "OpenRouter",
            cases = cases.Select(item => new { item.CaseId, item.DocumentId, item.RelativePath }),
            maxVisualPagesPerCase = MaxVisualPagesPerCase,
            causalBoundary = "SOURCE_TO_MODEL_TO_VISUAL_TO_CANONICAL;NO_GOLD;NO_PROMPT_TUNING",
            goldReadBeforeFreeze = false,
        }, ct);

        using var lease = await A99OpenRouterLiveProviderLease.AcquireAsync(repoRoot, "production-v6-visual-e2e", string.Join(',', cases.Select(item => item.CaseId)), ct);
        using var model = new OpenRouterCeilingReasoningModel(options, capability.Capability, http);
        var reports = new List<object>();
        foreach (var definition in cases)
        {
            ct.ThrowIfCancellationRequested();
            var report = await RunCaseAsync(repoRoot, output, definition, model, ct);
            reports.Add(report);
        }
        await WriteJson(output, "summary.v1.json", new
        {
            schemaVersion = "a99-v6-visual-e2e-summary-v1", status = "COMPLETE", model = Model,
            modelCalls = model.ProviderCalls, textModelCalls = reports.Sum(item => JsonInt(item, "textModelCalls")),
            visualModelCalls = reports.Sum(item => JsonInt(item, "visualModelCalls")),
            visualRecovered = reports.Sum(item => JsonInt(item, "visualRecovered")),
            visualRegionBindings = reports.Sum(item => JsonInt(item, "visualRegionBindings")),
            crossModalInput = reports.Sum(item => JsonInt(item, "crossModalInput")),
            canonicalOccurrences = reports.Sum(item => JsonInt(item, "canonicalOccurrences")),
            goldReadBeforeFreeze = false, cases = reports,
        }, ct);
        return 0;
    }

    private static async Task<object> RunCaseAsync(string repoRoot, string output, CaseDefinition definition,
        OpenRouterCeilingReasoningModel model, CancellationToken ct)
    {
        var path = Path.Combine(repoRoot, definition.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return new { definition.CaseId, definition.DocumentId, status = "BLOCKED", reason = "SOURCE_MISSING", textModelCalls = 0, visualModelCalls = 0, visualRecovered = 0, visualRegionBindings = 0, crossModalInput = 0, canonicalOccurrences = 0 };
        var sourceSha = Sha256File(path);
        var prepared = await VisualSourceEvidenceBuilder.BuildAsync(path, MaxVisualPagesPerCase, ct);
        var requestRoot = Path.Combine(output, definition.CaseId);
        Directory.CreateDirectory(requestRoot);
        var input = new CanonicalSemanticProductionInput(
            prepared.Catalog, null, sourceSha, prepared.Pages, [], [], [], [],
            null, null, prepared.VisualPages, sourceSha, definition.DocumentId);
        var requestId = $"A99-V6-VISUAL-E2E:{definition.CaseId}:{sourceSha}";
        var text = new OpenRouterCanonicalSemanticTextModel(model);
        var visual = prepared.VisualPages.Count > 0 ? new OpenRouterCanonicalSemanticVisualModel(model) : null;
        CanonicalSemanticProductionResult result;
        try
        {
            result = await CanonicalSemanticProductionEntryPoint.RunAsync(input, text, visual, requestId, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            var blocked = new { definition.CaseId, definition.DocumentId, status = "BLOCKED", failure = error.GetType().Name + ":" + error.Message,
                modality = prepared.Pages.Count == 0 ? "NONE" : ModalityProfiler.Profile(prepared.Pages).DocumentModality.ToString(),
                sourceSha256 = sourceSha, pageCount = prepared.VisualPages.Count, textModelCalls = model.Telemetry.Count(x => x.PassType == "SEMANTIC" && x.DocumentId == definition.DocumentId),
                visualModelCalls = model.Telemetry.Count(x => x.PassType == "VISUAL_RECOVERY" && x.DocumentId == definition.DocumentId), visualRecovered = 0, visualRegionBindings = 0, crossModalInput = 0, canonicalOccurrences = 0 };
            await WriteJson(requestRoot, "blocked.v1.json", blocked, ct);
            return blocked;
        }

        var final = new
        {
            definition.CaseId, definition.DocumentId, status = "SUCCESS", sourceSha256 = sourceSha,
            modality = result.ModalityProfile.DocumentModality.ToString(), pageCount = prepared.Pages.Count,
            sourcePages = prepared.Pages, visualPageEvidence = prepared.VisualPages.Select(page => new { page.PageId, page.ImageSha256, page.Width, page.Height }),
            textModelCalls = result.TextModelCalls, visualModelCalls = result.VisualModelCalls,
            visualRecovered = result.VisualOccurrences.Count, visualRegionBindings = result.VisualHeadings.Sum(item => item.Bindings.Count),
            crossModalInput = result.UnifiedOccurrences.Count(item => item.TextEvidence.Count > 0 && item.VisualEvidence.Count > 0),
            canonicalOccurrences = result.CanonicalOccurrences.Count, projectionCount = result.Projection.Count,
            textTelemetry = result.TextModelTelemetry, visualTelemetry = result.VisualModelTelemetry,
            coordinateAudit = BuildCoordinateAudit(result, prepared.Catalog),
            stageLedger = result.StageLedger, goldReadBeforeFreeze = false,
        };
        await WriteJson(requestRoot, "freeze.v1.json", final, ct);
        return final;
    }

    private static object BuildCoordinateAudit(
        CanonicalSemanticProductionResult result,
        DocumentSourceCatalog catalog)
    {
        var text = result.UnifiedOccurrences.SelectMany(item => item.TextEvidence)
            .Select(item =>
            {
                var unit = catalog.Units.SingleOrDefault(source => source.SourceId == item.SourceId);
                return new
                {
                    item.SourceId,
                    item.PageId,
                    pdfBox = unit?.SourceAnchor.BoundingBox,
                    normalizedRasterBox = item.BoundingBox,
                    transcriptText = item.Text,
                };
            }).ToArray();
        var visual = result.UnifiedOccurrences.SelectMany(item => item.VisualEvidence)
            .Select(item => new
            {
                item.VisualAlias,
                item.PageId,
                visualBox = item.BoundingBox,
                transcriptVisual = item.RecoveredTranscript,
            }).ToArray();
        var comparisons = text.SelectMany(textItem => visual
            .Where(visualItem => string.Equals(textItem.PageId, visualItem.PageId, StringComparison.OrdinalIgnoreCase))
            .Select(visualItem => new
            {
                page = textItem.PageId,
                textItem.pdfBox,
                textItem.normalizedRasterBox,
                visualItem.visualBox,
                textItem.transcriptText,
                visualItem.transcriptVisual,
                transcriptCompatible = string.Equals(textItem.transcriptText, visualItem.transcriptVisual, StringComparison.Ordinal),
                overlapIoU = IntersectionOverUnion(textItem.normalizedRasterBox, visualItem.visualBox),
                samePhysicalOccurrence = string.Equals(textItem.transcriptText, visualItem.transcriptVisual, StringComparison.Ordinal) &&
                    IntersectionOverUnion(textItem.normalizedRasterBox, visualItem.visualBox) > 0,
            })).ToArray();
        var physicalGroups = result.UnifiedOccurrences
            .Where(item => item.TextEvidence.Count > 0 && item.VisualEvidence.Count > 0)
            .Select((item, index) => new
            {
                group = index + 1,
                textSourceIds = item.TextEvidence.Select(evidence => evidence.SourceId).ToArray(),
                textTranscript = string.Concat(item.TextEvidence.Select(evidence => evidence.Text)),
                normalizedRasterBoxes = item.TextEvidence.Select(evidence => evidence.BoundingBox).ToArray(),
                visualAliases = item.VisualEvidence.Select(evidence => evidence.VisualAlias).ToArray(),
                visualTranscript = string.Concat(item.VisualEvidence.Select(evidence => evidence.RecoveredTranscript)),
                visualBoxes = item.VisualEvidence.Select(evidence => evidence.BoundingBox).ToArray(),
                pageIds = item.TextEvidence.Select(evidence => evidence.PageId)
                    .Concat(item.VisualEvidence.Select(evidence => evidence.PageId))
                    .Where(pageId => pageId is not null)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                overlapIoU = IntersectionOverUnion(
                    UnionBoxes(item.TextEvidence.Select(evidence => evidence.BoundingBox)),
                    UnionBoxes(item.VisualEvidence.Select(evidence => evidence.BoundingBox))),
                samePhysicalOccurrence = true,
            }).ToArray();
        return new { text, visual, comparisons, physicalGroups };
    }

    private static CanonicalSemanticVisualBoundingBox? UnionBoxes(
        IEnumerable<CanonicalSemanticVisualBoundingBox?> boxes)
    {
        var valid = boxes.Where(box => box is not null).Select(box => box!).ToArray();
        if (valid.Length == 0) return null;
        var left = valid.Min(box => box.Left);
        var top = valid.Min(box => box.Top);
        var right = valid.Max(box => box.Left + box.Width);
        var bottom = valid.Max(box => box.Top + box.Height);
        return new(left, top, right - left, bottom - top);
    }

    private static double IntersectionOverUnion(
        CanonicalSemanticVisualBoundingBox? left,
        CanonicalSemanticVisualBoundingBox? right)
    {
        if (left is null || right is null) return 0;
        var leftRight = left.Left + left.Width;
        var rightRight = right.Left + right.Width;
        var leftBottom = left.Top + left.Height;
        var rightBottom = right.Top + right.Height;
        var intersectionWidth = Math.Max(0, Math.Min(leftRight, rightRight) - Math.Max(left.Left, right.Left));
        var intersectionHeight = Math.Max(0, Math.Min(leftBottom, rightBottom) - Math.Max(left.Top, right.Top));
        var intersection = intersectionWidth * intersectionHeight;
        var union = left.Width * left.Height + right.Width * right.Height - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    private static int JsonInt(object value, string name) =>
        value.GetType().GetProperty(name)?.GetValue(value) is int number ? number : 0;

    private static IReadOnlyList<CaseDefinition> SelectCases()
    {
        var filter = Environment.GetEnvironmentVariable("A99_V6_VISUAL_E2E_CASES");
        if (string.IsNullOrWhiteSpace(filter)) return Cases;
        var requested = filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = Cases.Where(item => requested.Contains(item.CaseId)).ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("A99_V6_VISUAL_E2E_CASES_MATCHED_NONE");
        return selected;
    }

    private static async Task WriteJson(string directory, string name, object value, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, name), JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, ct);
    }

    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private sealed record CaseDefinition(string CaseId, string DocumentId, string RelativePath);
}

internal sealed record VisualSourcePreparation(
    DocumentSourceCatalog Catalog,
    IReadOnlyList<CanonicalSemanticPageEvidence> Pages,
    IReadOnlyList<CanonicalSemanticVisualPageEvidence> VisualPages);

internal static class VisualSourceEvidenceBuilder
{
    public static async Task<VisualSourcePreparation> BuildAsync(string path, int maxPages, CancellationToken ct)
    {
        if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return await BuildPdfAsync(path, maxPages, ct);
        return await BuildDocxAsync(path, maxPages, ct);
    }

    private static async Task<VisualSourcePreparation> BuildDocxAsync(string path, int maxPages, CancellationToken ct)
    {
        var source = new OpenXmlDocumentSource().Read(path);
        var catalog = DocumentSourceCatalogBuilder.FromSourceDocument(source);
        var media = ReadMedia(path).Take(maxPages).ToArray();
        var hasText = catalog.Units.Any(unit => !string.IsNullOrWhiteSpace(unit.Text));
        var pages = media.Length == 0
            ? [new CanonicalSemanticPageEvidence("P0001", hasText, 0, "DOCX_XML")]
            : media.Select(item => new CanonicalSemanticPageEvidence(item.PageId, hasText, 1, "DOCX_OOXML_MEDIA")).ToArray();
        var visualPages = media.Select(item => new CanonicalSemanticVisualPageEvidence(item.PageId, item.Hash, item.Bytes, item.Width, item.Height, item.MimeType)).ToArray();
        return await Task.FromResult(new VisualSourcePreparation(catalog, pages, visualPages));
    }

    private static async Task<VisualSourcePreparation> BuildPdfAsync(string path, int maxPages, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct);
        var ascii = Encoding.ASCII.GetString(bytes);
        var hasImageObjects = Regex.IsMatch(ascii, @"/Subtype\s*/Image", RegexOptions.CultureInvariant);
        using var pdf = PdfDocument.Open(path);
        var pageRows = pdf.GetPages().Take(maxPages).Select((page, index) =>
        {
            var text = page.Text ?? string.Empty;
            var usable = text.Trim().Length >= 80;
            return (PageId: $"P{index + 1:0000}", Text: text, Usable: usable);
        }).ToArray();
        var sourceUnits = new List<DocumentSourceUnit>();
        foreach (var page in pdf.GetPages().Take(maxPages))
        {
            var pageId = $"P{page.Number:0000}";
            var lines = ExtractPdfLineEvidence(page);
            if (lines.Count == 0 && page.Text.Trim().Length > 0)
                lines = [new PdfLineEvidence(page.Text, 0, 0, 1, 1)];
            var lineOrdinal = 0;
            foreach (var line in lines)
            {
                lineOrdinal++;
                var sourceId = $"{pageId}:L{lineOrdinal:0000}";
                sourceUnits.Add(new DocumentSourceUnit(
                    sourceId,
                    sourceUnits.Count,
                    line.Text,
                    new SourceAnchor
                    {
                        SourceType = "PDF",
                        ParagraphId = sourceId,
                        ParagraphIndex = sourceUnits.Count,
                        Page = page.Number,
                        BoundingBox = new PdfBoundingBox(line.Left, line.Bottom, line.Right, line.Top)
                    },
                    new StructuralSpan(0, line.Text.Length)));
            }
        }
        var catalog = new DocumentSourceCatalog(sourceUnits);
        var pages = new List<CanonicalSemanticPageEvidence>();
        var visualPages = new List<CanonicalSemanticVisualPageEvidence>();
        for (var i = 0; i < pageRows.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var pageNumber = i + 1;
            var bounds = PdfRegionRasterizer.GetPageBounds(path, pageNumber);
            var png = PdfRegionRasterizer.RenderCropPng(path, pageNumber, 0, 0, bounds.Width, bounds.Height, 110);
            var imageHash = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();
            var rasterWidth = Math.Max(1, (int)Math.Round(bounds.Width * 110 / 72));
            var rasterHeight = Math.Max(1, (int)Math.Round(bounds.Height * 110 / 72));
            pages.Add(new CanonicalSemanticPageEvidence(pageRows[i].PageId, pageRows[i].Usable,
                hasImageObjects ? 1 : 0, "PDF_TEXT_AND_RENDER", bounds.Width, bounds.Height,
                rasterWidth, rasterHeight, "PDF_POINTS_BOTTOM_LEFT_TO_RASTER_PIXELS_TOP_LEFT"));
            visualPages.Add(new CanonicalSemanticVisualPageEvidence(pageRows[i].PageId, imageHash, png, rasterWidth, rasterHeight));
        }
        return new VisualSourcePreparation(catalog, pages, visualPages);
    }

    private static IReadOnlyList<PdfLineEvidence> ExtractPdfLineEvidence(UglyToad.PdfPig.Content.Page page)
    {
        var words = page.GetWords()
            .Where(word => !string.IsNullOrWhiteSpace(word.Text))
            .Select(word => new PdfWordEvidence(
                word.Text,
                word.BoundingBox.Left,
                word.BoundingBox.Bottom,
                word.BoundingBox.Right,
                word.BoundingBox.Top,
                (word.BoundingBox.Bottom + word.BoundingBox.Top) / 2.0))
            .OrderByDescending(word => word.MidY)
            .ThenBy(word => word.Left)
            .ToArray();
        var buckets = new List<List<PdfWordEvidence>>();
        foreach (var word in words)
        {
            var bucket = buckets.LastOrDefault(existing => Math.Abs(existing[0].MidY - word.MidY) <= 3.0);
            if (bucket is null) buckets.Add([word]);
            else bucket.Add(word);
        }
        return buckets
            .Select(bucket => bucket.OrderBy(word => word.Left).ToArray())
            .Select(bucket => new PdfLineEvidence(
                string.Join(" ", bucket.Select(word => word.Text)),
                bucket.Min(word => word.Left),
                bucket.Min(word => word.Bottom),
                bucket.Max(word => word.Right),
                bucket.Max(word => word.Top)))
            .Where(line => line.Text.Length > 0)
            .ToArray();
    }

    private static IEnumerable<MediaPage> ReadMedia(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var entries = archive.Entries.Where(entry => entry.FullName.StartsWith("word/media/", StringComparison.OrdinalIgnoreCase)
                && (entry.FullName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                    || entry.FullName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                    || entry.FullName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(entry => entry.FullName, StringComparer.Ordinal).ToArray();
        for (var i = 0; i < entries.Length; i++)
        {
            using var stream = entries[i].Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var bytes = memory.ToArray();
            var dimensions = ImageDimensions(bytes);
            var mimeType = entries[i].FullName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
            yield return new MediaPage($"P{i + 1:0000}", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes, dimensions.Width, dimensions.Height, mimeType);
        }
    }

    private static (int Width, int Height) ImageDimensions(byte[] bytes)
    {
        if (bytes.Length >= 24 && bytes[0] == 137 && bytes[1] == 80 && bytes[2] == 78 && bytes[3] == 71)
            return (ReadBigEndian(bytes, 16), ReadBigEndian(bytes, 20));
        if (bytes.Length >= 4 && bytes[0] == 0xff && bytes[1] == 0xd8)
        {
            for (var i = 2; i + 9 < bytes.Length; i++)
                if (bytes[i] == 0xff && bytes[i + 1] is >= 0xc0 and <= 0xc3)
                    return ((bytes[i + 5] << 8) | bytes[i + 6], (bytes[i + 3] << 8) | bytes[i + 4]);
        }
        return (1, 1);
    }

    private static int ReadBigEndian(byte[] bytes, int offset) =>
        (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];
    private sealed record MediaPage(string PageId, string Hash, byte[] Bytes, int Width, int Height, string MimeType);
    private sealed record PdfWordEvidence(string Text, double Left, double Bottom, double Right, double Top, double MidY);
    private sealed record PdfLineEvidence(string Text, double Left, double Bottom, double Right, double Top);
}
