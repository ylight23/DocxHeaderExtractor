using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Routing;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Preflight for the first PDF canary: is the request the right shape, before a provider call is
/// spent finding out that it is not.
/// <para>
/// This is not a quality measurement. It asks only whether the packet the PDF lane would send
/// matches the architecture - every source occurrence represented, ownership complete and
/// non-overlapping, no risk occurrence fused into a clean one, and no positional coordinate handed
/// to the model as something it owns.
/// </para>
/// <para>
/// Zero provider calls. Requests are captured at the provider boundary, fully formed; the empty
/// reply affects only what happens afterwards.
/// </para>
/// </summary>
public sealed class PdfCanaryPreflightTests
{
    private const string Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";

    [Fact]
    public async Task Preflight()
    {
        var root = TestRepository.Root();
        var path = Path.Combine(root, Pdf);
        var file = UploadedFile.FromLocalPath(path);

        var (capture, _) = await CaptureAsync(path);
        var source = ReadSourceLayer(path);
        var packets = capture.Requests.Select(request => Packet.Parse(request)).ToArray();

        // ---- source shaping -------------------------------------------------------------------
        var riskBlocks = source.Blocks
            .Where(block => block.Lines.Any(line => source.RiskLines.Contains(line)))
            .ToArray();
        var fused = riskBlocks.Where(block => block.Lines.Any(line => !source.RiskLines.Contains(line))).ToArray();

        // ---- ownership ------------------------------------------------------------------------
        var ownedAll = packets.SelectMany(packet => packet.Owned).ToArray();
        var ownedSet = ownedAll.ToHashSet(StringComparer.Ordinal);
        var catalogAliases = source.AliasBySourceId.Values.ToHashSet(StringComparer.Ordinal);

        // ---- contract -------------------------------------------------------------------------
        var schema = JsonSerializer.Serialize(CanonicalSemanticContract.Schema());
        var coordinateNames = new[]
        {
            "start", "end", "offset", "startOffset", "endOffset", "page", "pageNumber",
            "bbox", "boundingBox", "x", "y", "left", "right", "top", "bottom",
        };
        var coordinatesInSchema = coordinateNames
            .Where(name => schema.Contains($"\"{name}\"", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var report = new
        {
            artifactKind = "PDF_CANARY_072_PREFLIGHT",
            providerCalls = 0,
            capturedAt = "provider boundary, before any response",
            notCaptured = "the placement pass: it fires only when headings are unresolved, and an " +
                "empty reply produces none. Production would send it as an additional request.",
            source = new
            {
                file = Path.GetFileName(path),
                sha256 = file.Sha256,
                detectedType = file.DetectedType.ToString(),
                parserLines = source.Lines.Count,
                semanticBlocks = source.Blocks.Count,
                riskLines = source.RiskLines.Count,
                riskBlocks = riskBlocks.Length,
                riskBlocksThatAreSingletons = riskBlocks.Count(block => block.LineCount == 1),
                riskBlocksFusedWithCleanLines = fused.Length,
                sourceCatalogUnits = source.Catalog.Units.Count,
                aliases = catalogAliases.Count,
            },
            segmentation = new
            {
                ownedPerSegmentPolicy = 120,
                visibleMarginPolicy = 20,
                segments = packets.Length,
                owned = packets.Select(packet => packet.Owned.Length).ToArray(),
                visible = packets.Select(packet => packet.Visible.Length).ToArray(),
                ownedTotal = ownedAll.Length,
                ownedDistinct = ownedSet.Count,
                duplicateOwnership = ownedAll.Length - ownedSet.Count,
                aliasesNeverOwned = catalogAliases.Except(ownedSet, StringComparer.Ordinal).OrderBy(x => x).ToArray(),
                aliasesOwnedButNotInCatalog = ownedSet.Except(catalogAliases, StringComparer.Ordinal).OrderBy(x => x).ToArray(),
            },
            request = new
            {
                systemPromptSha256 = Sha256(capture.Requests[0].SystemPrompt),
                systemPromptIsTheSharedOne = capture.Requests[0].SystemPrompt == CanonicalSemanticEngine.SystemPrompt,
                userPayloadSha256 = capture.Requests.Select(request => Sha256(request.UserMessage)).ToArray(),
                chars = capture.Requests.Select(request => request.UserMessage.Length).ToArray(),
                bytes = capture.Requests.Select(request => Encoding.UTF8.GetByteCount(request.UserMessage)).ToArray(),
                largestRequestChars = capture.Requests.Max(request => request.UserMessage.Length),
                expectedItemCount = capture.Requests.Select(request => request.ExpectedItemCount).ToArray(),
            },
            evidenceShape = new
            {
                ownedEntryFields = packets[0].OwnedEntryFields,
                marginEntryFields = packets[0].MarginEntryFields,
            },
            contract = new
            {
                protocol = CanonicalSemanticContract.ProtocolVersion,
                coordinateNamesFoundInSchema = coordinatesInSchema,
                partialSpanCommunicated = MentionsPartialSpan(capture.Requests[0].SystemPrompt),
                relationFieldsCommunicated = capture.Requests[0].SystemPrompt.Contains("parent-node:", StringComparison.Ordinal),
            },
        };

        const string Frozen = "eval/a99-closed-loop/pdf-canary-072";
        FreezeArtifact.AssertJson(Frozen, "preflight.v1.json", report);
        for (var index = 0; index < capture.Requests.Count; index++)
            FreezeArtifact.AssertText(Frozen, $"request-{index}.json", capture.Requests[index].UserMessage);
        FreezeArtifact.AssertText(Frozen, "system-prompt.txt", capture.Requests[0].SystemPrompt);

        // ---- acceptance -----------------------------------------------------------------------
        Assert.NotEmpty(capture.Requests);
        Assert.Empty(fused);
        Assert.All(riskBlocks, block => Assert.Equal(1, block.LineCount));
        Assert.Equal(ownedAll.Length, ownedSet.Count);
        Assert.Equal(catalogAliases.Count, ownedSet.Count);
        Assert.Empty(catalogAliases.Except(ownedSet, StringComparer.Ordinal));
        Assert.Empty(coordinatesInSchema);
        Assert.True(report.request.systemPromptIsTheSharedOne);
    }

    [Fact]
    public async Task The_packet_is_byte_for_byte_deterministic_across_runs()
    {
        // If two runs on identical input disagree, nothing measured after a canary can be
        // attributed to anything, so this stops before the call rather than after it.
        var path = Path.Combine(TestRepository.Root(), Pdf);

        var (first, _) = await CaptureAsync(path);
        var (second, _) = await CaptureAsync(path);

        Assert.Equal(first.Requests.Count, second.Requests.Count);
        Assert.Equal(
            first.Requests.Select(request => Sha256(request.UserMessage)),
            second.Requests.Select(request => Sha256(request.UserMessage)));
        Assert.Equal(
            Sha256(first.Requests[0].SystemPrompt),
            Sha256(second.Requests[0].SystemPrompt));
    }

    [Fact]
    public async Task The_pdf_lane_sends_the_same_system_prompt_as_the_docx_lane()
    {
        // Shared engine, so this should be true by construction. Asserted anyway: a PDF-specific
        // prompt would make any DOCX-versus-PDF difference unattributable.
        var (capture, _) = await CaptureAsync(Path.Combine(TestRepository.Root(), Pdf));

        Assert.All(capture.Requests, request =>
            Assert.Equal(CanonicalSemanticEngine.SystemPrompt, request.SystemPrompt));
    }

    [Fact]
    public async Task No_layout_number_is_presented_as_something_the_model_returns()
    {
        // Layout facts may travel as evidence. What must not happen is the contract inviting the
        // model to hand back a coordinate, which is how positional authority leaks into the model.
        var (capture, _) = await CaptureAsync(Path.Combine(TestRepository.Root(), Pdf));
        var prompt = capture.Requests[0].SystemPrompt;

        Assert.Contains("Do not return offsets, spans, pages, boxes, coordinates", prompt, StringComparison.Ordinal);
        Assert.Contains("sourceAlias", prompt, StringComparison.Ordinal);
    }

    private static bool MentionsPartialSpan(string prompt) =>
        prompt.Contains("substring", StringComparison.OrdinalIgnoreCase) ||
        prompt.Contains("prefix", StringComparison.OrdinalIgnoreCase) ||
        prompt.Contains("part of", StringComparison.OrdinalIgnoreCase);

    private static async Task<(RequestCapturingClassifier Capture, object? Unused)> CaptureAsync(string path)
    {
        using var capture = new RequestCapturingClassifier();
        await CanonicalSemanticPdfAuthorityAdapter.RunAsync(path, capture, CancellationToken.None);
        return (capture, null);
    }

    /// <summary>
    /// Re-reads the source layer through the same parser stages the adapter uses. The alias set is
    /// cross-checked against the captured packets, so this cannot silently diverge from what was
    /// actually sent.
    /// </summary>
    private static SourceLayer ReadSourceLayer(string path)
    {
        IReadOnlyList<PdfLine> lines;
        using (var document = UglyToad.PdfPig.PdfDocument.Open(path))
        {
            lines = PdfLineExtraction.ExtractLines(document);
        }

        var annotations = PdfLineBlockFilter.Analyze(lines);
        var riskLines = annotations
            .Where(annotation => annotation.PageNumber || annotation.Repeated ||
                annotation.HeaderFooterZone || annotation.TableLike)
            .Select(annotation => annotation.Line)
            .ToHashSet();
        var blocks = PdfSemanticBlockGrouper.Build(annotations, includeRiskLines: true);
        var catalog = DocumentSourceCatalogBuilder.FromPdfParserBlocks(blocks, lines);
        var aliasBySourceId = SemanticSourceAliasCatalog.FromCatalog(catalog)
            .ToDictionary(item => item.SourceId, item => item.Alias, StringComparer.Ordinal);
        return new SourceLayer(lines, riskLines, blocks, catalog, aliasBySourceId);
    }

    private sealed record SourceLayer(
        IReadOnlyList<PdfLine> Lines,
        IReadOnlySet<PdfLine> RiskLines,
        IReadOnlyList<PdfSemanticBlock> Blocks,
        DocumentSourceCatalog Catalog,
        IReadOnlyDictionary<string, string> AliasBySourceId);

    private sealed record Packet(string[] Owned, string[] Visible, string[] OwnedEntryFields, string[] MarginEntryFields)
    {
        public static Packet Parse(CapturedRequest request)
        {
            var payload = request.UserMessage;
            var schemaAt = payload.IndexOf("\nSCHEMA=", StringComparison.Ordinal);
            using var document = JsonDocument.Parse(schemaAt < 0 ? payload : payload[..schemaAt]);
            var root = document.RootElement;
            var owned = root.GetProperty("ownedSourceAliases").EnumerateArray()
                .Select(item => item.GetString()!).ToArray();
            var evidence = root.GetProperty("sourceEvidence").EnumerateArray().ToArray();
            var ownedSet = owned.ToHashSet(StringComparer.Ordinal);
            string[] FieldsOf(bool isOwned) => evidence
                .Where(item => ownedSet.Contains(item.GetProperty("alias").GetString()!) == isOwned)
                .Take(1)
                .SelectMany(item => item.EnumerateObject().Select(property => property.Name))
                .ToArray();
            return new Packet(
                owned,
                evidence.Select(item => item.GetProperty("alias").GetString()!).ToArray(),
                FieldsOf(true),
                FieldsOf(false));
        }
    }

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

}
