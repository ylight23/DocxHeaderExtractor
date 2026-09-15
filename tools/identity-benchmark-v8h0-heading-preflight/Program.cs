using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using UglyToad.PdfPig;

namespace IdentityBenchmarkV8H0;

internal static class Program
{
    private const string V8A2Relative = "artifacts/identity-benchmark/v8a2/new-source-intake-v1";
    private const string OutputRelative = "artifacts/identity-benchmark/v8h0/heading-extraction-preflight-v1";
    private const string V8ProofAuthority = "ffa1110";
    private const string V8A2Authority = "579b3fd";
    private const string V8A3Authority = "c6ce41c";
    private const string V8BAuthority = "1550c2e";
    private const string V8B0Authority = "140f8ad";
    private const string DetectorVersion = "pdf-layout-evidence-production-role-span-v1";
    private const string BindingVersion = "v8h0-source-bound-heading-binding-v1";
    private const int RoleBatchSize = 8;
    private const int SpanBatchSize = 4;
    private const int MaxAnalystBlocks = 40;
    private const double BytesPerEstimatedToken = 4.0;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static int Main(string[] args)
    {
        try
        {
            var root = Path.GetFullPath(args.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal)) ?? Directory.GetCurrentDirectory());
            Run(root);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"V8H0_ERROR={ex.Message}");
            return 2;
        }
    }

    private static void Run(string root)
    {
        var input = Full(root, V8A2Relative);
        var output = Full(root, OutputRelative);
        Require(Directory.Exists(input), "V8A2_INPUT_NOT_FOUND");
        if (Directory.Exists(output) && Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).Any())
        {
            var priorManifestPath = Path.Combine(output, "manifest.json");
            var priorWasThisTool = File.Exists(priorManifestPath) &&
                File.ReadAllText(priorManifestPath).Contains("a99_identity_benchmark_v8h0_heading_extraction_preflight", StringComparison.Ordinal);
            if (!priorWasThisTool)
                throw new InvalidOperationException("V8H0_OUTPUT_ALREADY_EXISTS_NO_OVERWRITE");
            Directory.Delete(output, true);
        }
        Directory.CreateDirectory(output);

        var v8a2ManifestPath = Path.Combine(input, "manifest.json");
        var intakePath = Path.Combine(input, "new-source-intake.json");
        var v8a2Manifest = Load(v8a2ManifestPath);
        Require(v8a2Manifest.GetProperty("status").GetString() == "READY_FOR_V8_PROVIDER_EXECUTION", "V8A2_NOT_READY");
        Require(v8a2Manifest.GetProperty("providerCalls").GetInt32() == 0, "V8A2_PROVIDER_CALLS_NONZERO");
        Require(v8a2Manifest.GetProperty("goldReadCount").GetInt32() == 0, "V8A2_GOLD_READS_NONZERO");

        var intake = Load(intakePath);
        var selectedIds = v8a2Manifest.GetProperty("selectedSourceIds").EnumerateArray()
            .Select(x => x.GetString()!).Order(StringComparer.Ordinal).ToArray();
        var sourceRows = intake.GetProperty("candidates").EnumerateArray()
            .Where(x => selectedIds.Contains(x.GetProperty("sourceId").GetString()!, StringComparer.Ordinal))
            .Select(x => new SourceRow(
                x.GetProperty("sourceId").GetString()!,
                x.GetProperty("name").GetString()!,
                x.GetProperty("path").GetString()!,
                x.GetProperty("byteSha256").GetString()!,
                x.GetProperty("family").GetString()!))
            .OrderBy(x => x.SourceId, StringComparer.Ordinal).ToArray();
        Require(sourceRows.Length == 6, "V8H0_SOURCE_COUNT_DRIFT");

        var docs = sourceRows.Select(BuildDocument).ToArray();
        var rebuiltDocs = sourceRows.Select(BuildDocument).ToArray();
        var firstRequestHashes = docs.SelectMany(x => x.RoleRequests.Concat(x.SpanRequests)).Select(x => x.RequestSha256).ToArray();
        var rebuiltRequestHashes = rebuiltDocs.SelectMany(x => x.RoleRequests.Concat(x.SpanRequests)).Select(x => x.RequestSha256).ToArray();
        var requestHashesDeterministic = firstRequestHashes.SequenceEqual(rebuiltRequestHashes, StringComparer.Ordinal);
        Require(requestHashesDeterministic, "V8H0_NONDETERMINISTIC_REQUEST_REBUILD");
        var roleRequests = docs.SelectMany(x => x.RoleRequests).ToArray();
        var spanRequests = docs.SelectMany(x => x.SpanRequests).ToArray();
        var allRequests = roleRequests.Concat(spanRequests)
            .OrderBy(x => x.DocumentId, StringComparer.Ordinal)
            .ThenBy(x => x.Stage, StringComparer.Ordinal)
            .ThenBy(x => x.ShardOrdinal)
            .ToArray();

        var selfTests = BindingContractTests.Run();
        Require(selfTests.All(x => x.Pass), "V8H0_SYNTHETIC_BINDING_TEST_FAILED");

        var sourceUniverse = docs.Select(x => new
        {
            documentId = x.DocumentId,
            byteSha256 = x.ByteSha256,
            rawLineCount = x.SourceLines.Count,
            opaqueOccurrenceCount = x.SourceLines.Count,
            occurrences = x.SourceLines.Select(line => new
            {
                @ref = line.Handle,
                sourceOccurrenceId = line.SourceOccurrenceId,
                lineIndex = line.LineIndex,
                page = line.Page,
                documentOrder = line.DocumentOrder,
                text = line.Text,
                geometry = new { line.Left, line.Right, line.Top, line.Bottom, y = line.Y },
                style = new { line.FontSize, fontName = line.FontName, boldRatio = line.BoldRatio, italicRatio = line.ItalicRatio, fillColor = line.FillColorKey },
            }).ToArray(),
        }).ToArray();

        var candidateManifest = docs.Select(x => new
        {
            documentId = x.DocumentId,
            rawSourceLineCount = x.SourceLines.Count,
            productionCandidateBlockCount = x.CandidateBlocks.Count,
            selectedAnalystBlockCount = x.SelectedBlocks.Count,
            selectedBlockRefs = x.SelectedBlocks.Select(b => b.OpaqueRef).ToArray(),
            selectedBlocks = x.SelectedBlocks.Select(b => new
            {
                @ref = b.OpaqueRef,
                productionBlockId = b.Block.Id,
                page = b.Block.Page,
                text = b.Block.Text,
                lineRefs = b.Block.Lines.Select(line => x.LineBySourceId[PdfCandidateProvenance.LineId(line)].Handle).ToArray(),
                exactSourceBinding = new
                {
                    sourceOccurrenceIds = b.Block.Lines.Select(line => PdfCandidateProvenance.LineId(line)).ToArray(),
                    sourceText = b.Block.Text,
                },
            }).ToArray(),
        }).ToArray();
        var candidateManifestJson = JsonSerializer.Serialize(new { schemaVersion = "a99-v8h0-production-heading-candidate-manifest-v1", detectorVersion = DetectorVersion, documents = candidateManifest }, JsonOptions);
        var candidateManifestSha = Sha256(candidateManifestJson);

        var scale = new
        {
            requestCount = allRequests.Length,
            roleRequestCount = roleRequests.Length,
            pointerSpanRequestCount = spanRequests.Length,
            totalRequestBytes = allRequests.Sum(x => (long)x.RequestBytes),
            maximumRequestBytes = allRequests.Length == 0 ? 0 : allRequests.Max(x => x.RequestBytes),
            totalEstimatedInputTokens = allRequests.Sum(x => (long)x.EstimatedInputTokens),
            maximumEstimatedInputTokens = allRequests.Length == 0 ? 0 : allRequests.Max(x => x.EstimatedInputTokens),
            repeatedContextBytes = allRequests.Sum(x => (long)x.RepeatedContextBytes),
            repeatedContextRatio = allRequests.Sum(x => (long)x.RequestBytes) == 0 ? 0 : allRequests.Sum(x => (double)x.RepeatedContextBytes) / allRequests.Sum(x => (double)x.RequestBytes),
            perDocument = docs.Select(x => new
            {
                documentId = x.DocumentId,
                requestCount = allRequests.Count(r => r.DocumentId == x.DocumentId),
                rawSourceLines = x.SourceLines.Count,
                candidateBlocks = x.CandidateBlocks.Count,
                selectedBlocks = x.SelectedBlocks.Count,
                totalBytes = allRequests.Where(r => r.DocumentId == x.DocumentId).Sum(r => (long)r.RequestBytes),
                totalEstimatedInputTokens = allRequests.Where(r => r.DocumentId == x.DocumentId).Sum(r => (long)r.EstimatedInputTokens),
                maxRequestBytes = allRequests.Where(r => r.DocumentId == x.DocumentId).Select(r => r.RequestBytes).DefaultIfEmpty().Max(),
                maxEstimatedInputTokens = allRequests.Where(r => r.DocumentId == x.DocumentId).Select(r => r.EstimatedInputTokens).DefaultIfEmpty().Max(),
                roleSourceRefs = x.RoleRequests.SelectMany(r => r.SourceRefs).Distinct(StringComparer.Ordinal).Count(),
                spanSourceRefsUpperBound = x.SpanRequests.SelectMany(r => r.SourceRefs).Distinct(StringComparer.Ordinal).Count(),
            }).OrderBy(x => x.documentId, StringComparer.Ordinal).ToArray(),
        };

        var requestIndex = allRequests.Select(x => new
        {
            x.DocumentId,
            x.Stage,
            x.ShardId,
            x.ShardOrdinal,
            x.BlockRefs,
            x.SourceRefs,
            x.RequestBytes,
            x.EstimatedInputTokens,
            x.RepeatedContextBytes,
            x.RequestSha256,
            systemPromptSha256 = Sha256(x.SystemPrompt),
            userPromptSha256 = Sha256(x.UserPrompt),
            conditional = x.Stage == "POINTER_SPAN",
        }).ToArray();

        var manifest = new
        {
            artifactKind = "a99_identity_benchmark_v8h0_heading_extraction_preflight",
            schemaVersion = "a99-v8h0-heading-extraction-preflight-v1",
            status = "READY_FOR_V8H0_HEADING_PROVIDER_AUTHORIZATION",
            authority = new
            {
                v8ProofContract = V8ProofAuthority,
                v8a2Commit = V8A2Authority,
                v8a3Commit = V8A3Authority,
                v8bCommit = V8BAuthority,
                v8b0Commit = V8B0Authority,
                v8a2ManifestSha256 = Sha256File(v8a2ManifestPath),
                sourceIntakeSha256 = Sha256File(intakePath),
                frozenInputArtifactsMutated = false,
            },
            detector = new
            {
                version = DetectorVersion,
                reused = "PdfLineExtraction, PdfLineBlockFilter, PdfSemanticBlockGrouper, PdfStyleClusterProfile, PdfCandidateContextBuilder, PdfCandidateRanker, PdfLayoutEvidenceOutline.BuildBroadCandidates/SelectRankedCandidates, PdfBlockAnalyst role/span prompts",
                maxAnalystBlocks = MaxAnalystBlocks,
                roleBatchSize = RoleBatchSize,
                pointerSpanBatchSize = SpanBatchSize,
                promptProfileSha256 = PdfBlockAnalyst.PromptProfileSha256,
                modelCalls = 0,
                providerCalls = 0,
            },
            documents = new
            {
                count = docs.Length,
                sourceLineCount = docs.Sum(x => x.SourceLines.Count),
                sourceRows = sourceRows.Select(x => new { x.SourceId, x.Name, x.ByteSha256, x.Family }).ToArray(),
                candidateManifest = candidateManifestSha,
            },
            requestScale = scale,
            binding = new
            {
                version = BindingVersion,
                sourceAuthority = "harness-owned immutable source line/block map",
                modelOutputRefs = "production block-local opaque ids only; canonical source IDs are never model output authority",
                exactTextOwnedByHarness = true,
                numericOffsetsOwnedByHarness = true,
                noLevelParentIdentity = true,
                syntheticTests = new { count = selfTests.Count, passed = selfTests.Count(x => x.Pass), results = selfTests },
            },
            firewall = new
            {
                providerCalls = 0,
                modelCalls = 0,
                goldReadCount = 0,
                v8a2Mutated = false,
                v8a3Mutated = false,
                v8bMutated = false,
                v8b0Mutated = false,
                identityCandidateGeneration = false,
                historicalN15Mutated = false,
            },
            gate = new
            {
                sourceUniverseComplete = true,
                productionDetectorReused = true,
                requestHashesDeterministic,
                allRequestsMaterializedBeforeProvider = true,
                noGoldOrPredictionInputs = true,
                status = "READY_FOR_V8H0_HEADING_PROVIDER_AUTHORIZATION",
            },
            next = "Provider authorization may be considered only for frozen role requests; pointer-span requests are conditional upper-bound shards and must be executed only for validated role-selected blocks.",
        };

        Write(Path.Combine(output, "source-universe.json"), new { schemaVersion = "a99-v8h0-source-universe-v1", sourceOnly = true, documents = sourceUniverse });
        File.WriteAllText(Path.Combine(output, "production-candidate-manifest.json"), candidateManifestJson, new UTF8Encoding(false));
        Write(Path.Combine(output, "handle-map.json"), new { schemaVersion = "a99-v8h0-opaque-handle-map-v1", deterministic = true, documents = docs.Select(x => new { documentId = x.DocumentId, occurrences = x.SourceLines.Select(l => new { @ref = l.Handle, sourceOccurrenceId = l.SourceOccurrenceId }).ToArray(), blocks = x.SelectedBlocks.Select(b => new { @ref = b.OpaqueRef, productionBlockId = b.Block.Id, sourceRefs = b.Block.Lines.Select(line => x.LineBySourceId[PdfCandidateProvenance.LineId(line)].Handle).ToArray() }).ToArray() }).ToArray() });
        Write(Path.Combine(output, "heading-role-requests.json"), new { schemaVersion = "a99-v8h0-role-requests-v1", frozenBeforeProvider = true, requests = roleRequests.Select(x => x.ToPublic()).ToArray() });
        Write(Path.Combine(output, "heading-pointer-span-requests.json"), new { schemaVersion = "a99-v8h0-pointer-span-requests-v1", conditionalOnRoleSelection = true, frozenBeforeProvider = true, requests = spanRequests.Select(x => x.ToPublic()).ToArray() });
        Write(Path.Combine(output, "request-index.json"), new { schemaVersion = "a99-v8h0-request-index-v1", requests = requestIndex });
        Write(Path.Combine(output, "binding-contract-tests.json"), new { schemaVersion = "a99-v8h0-binding-contract-tests-v1", results = selfTests });
        Write(Path.Combine(output, "scale-audit.json"), new { schemaVersion = "a99-v8h0-scale-audit-v1", status = manifest.status, scale, perRequest = allRequests.Select(x => x.ToScalePublic()).ToArray() });
        Write(Path.Combine(output, "manifest.json"), manifest);
        File.WriteAllText(Path.Combine(output, "report.md"), BuildReport(manifest, docs, scale, selfTests), new UTF8Encoding(false));

        Console.WriteLine($"V8H0_STATUS={manifest.status} PROVIDER_CALLS=0 GOLD_READ_COUNT=0 SOURCE_LINES={docs.Sum(x => x.SourceLines.Count)} ROLE_REQUESTS={roleRequests.Length} SPAN_REQUESTS={spanRequests.Length}");
    }

    private static DocumentPlan BuildDocument(SourceRow row)
    {
        Require(File.Exists(row.Path), $"V8H0_SOURCE_NOT_FOUND:{row.SourceId}");
        Require(Sha256File(row.Path) == row.ByteSha256, $"V8H0_SOURCE_HASH_MISMATCH:{row.SourceId}");
        using var pdf = PdfDocument.Open(row.Path);
        var lines = PdfLineExtraction.ExtractLines(pdf);
        var annotations = PdfLineBlockFilter.Analyze(lines);
        var semanticLines = annotations.Where(x => !x.ExcludeFromSemanticSamples).Select(x => x.Line).ToArray();
        var profile = PdfStyleClusterProfile.Learn(semanticLines);
        var blocks = PdfSemanticBlockGrouper.Build(annotations);
        var broad = PdfLayoutEvidenceOutline.BuildBroadCandidates(blocks, profile);
        var contexts = PdfCandidateContextBuilder.Build(broad, annotations);
        var ranked = PdfCandidateRanker.Rank(broad, contexts);
        var selected = PdfLayoutEvidenceOutline.SelectRankedCandidates(broad, ranked, MaxAnalystBlocks).Selected;
        var sourceLines = lines.Select((line, index) => new SourceLine(
            Handle: $"U{index + 1:000000}",
            SourceOccurrenceId: PdfCandidateProvenance.LineId(line),
            LineIndex: index,
            Page: line.Page,
            DocumentOrder: index,
            Text: line.Text,
            Y: line.Y,
            Left: line.Left,
            Right: line.Right,
            Top: line.Top ?? line.Y,
            Bottom: line.Bottom ?? line.Y,
            FontSize: line.FontSize,
            BoldRatio: line.BoldRatio,
            ItalicRatio: line.ItalicRatio,
            FontName: line.FontName,
            FillColorKey: line.FillColorKey)).ToArray();
        var bySource = sourceLines.ToDictionary(x => x.SourceOccurrenceId, StringComparer.Ordinal);
        var selectedRefs = selected.Select((block, index) => new SelectedBlock($"B{index + 1:000}", block)).ToArray();
        var roleRequests = BuildRequests(row.SourceId, "ROLE", selectedRefs, contexts, RoleBatchSize, sourceLines, bySource, PdfBlockAnalyst.RoleSystemPromptText,
            (items, context) => PdfBlockAnalyst.BuildUserPrompt(items, context));
        var spanRequests = BuildRequests(row.SourceId, "POINTER_SPAN", selectedRefs, contexts, SpanBatchSize, sourceLines, bySource, PdfBlockAnalyst.PointerSpanSystemPromptText,
            (items, context) => PdfBlockAnalyst.BuildPointerSpanPrompt(items, context));
        return new DocumentPlan(row.SourceId, row.ByteSha256, sourceLines, broad, selectedRefs, bySource, roleRequests, spanRequests);
    }

    private static IReadOnlyList<RequestRow> BuildRequests(
        string documentId,
        string stage,
        IReadOnlyList<SelectedBlock> selected,
        IReadOnlyDictionary<string, PdfCandidateContext> contexts,
        int batchSize,
        IReadOnlyList<SourceLine> sourceLines,
        IReadOnlyDictionary<string, SourceLine> bySource,
        string systemPrompt,
        Func<IReadOnlyList<PdfSemanticBlock>, IReadOnlyDictionary<string, PdfCandidateContext>, string> builder)
    {
        var rows = new List<RequestRow>();
        foreach (var (batch, ordinal) in selected.Chunk(batchSize).Select((x, i) => (x, i + 1)))
        {
            var blocks = batch.Select(x => x.Block).ToArray();
            var blockIds = blocks.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            var context = contexts.Where(x => blockIds.Contains(x.Key)).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
            var userPrompt = builder(blocks, context);
            var sourceRefs = blocks.SelectMany(block => block.Lines.Select(line => bySource[PdfCandidateProvenance.LineId(line)].Handle))
                .Concat(blocks.SelectMany(block => ContextSourceRefs(block, contexts, bySource)))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var opaqueBlockRefs = batch.Select(x => x.OpaqueRef).ToArray();
            object outputContract = stage == "ROLE"
                ? new { response = "blocks", refField = "id", rolesOnly = true, forbidden = new[] { "level", "parent", "semanticNodeId", "identity", "canonicalText" } }
                : new { response = "blocks", refField = "id", pointerOnly = true, forbidden = new[] { "headingText", "level", "parent", "semanticNodeId", "identity" } };
            var requestObject = new
            {
                schemaVersion = "a99-v8h0-heading-extraction-request-v1",
                documentId,
                stage,
                shardId = $"{documentId}:{stage}:{ordinal:000}",
                blocks = opaqueBlockRefs,
                sourceRefs,
                systemPrompt,
                userPrompt,
                outputContract,
            };
            var serialized = JsonSerializer.Serialize(requestObject, JsonOptions);
            var userContextBytes = Encoding.UTF8.GetByteCount(userPrompt);
            var repeatedBytes = RepeatedContextBytes(userPrompt);
            rows.Add(new RequestRow(documentId, stage, $"{documentId}:{stage}:{ordinal:000}", ordinal, opaqueBlockRefs, sourceRefs, serialized, systemPrompt, userPrompt, Encoding.UTF8.GetByteCount(serialized), EstimateTokens(serialized), repeatedBytes, Sha256(serialized)));
        }
        return rows;
    }

    private static IEnumerable<string> ContextSourceRefs(PdfSemanticBlock block, IReadOnlyDictionary<string, PdfCandidateContext> contexts, IReadOnlyDictionary<string, SourceLine> bySource)
    {
        // The production context currently carries text excerpts rather than source IDs. The
        // selected target block lines are exact binding authority; contextual text is recorded as
        // context bytes, not guessed back into canonical occurrences.
        yield break;
    }

    private static int RepeatedContextBytes(string userPrompt)
    {
        try
        {
            using var doc = JsonDocument.Parse(userPrompt);
            var values = new List<string>();
            foreach (var block in doc.RootElement.GetProperty("blocks").EnumerateArray())
            {
                if (!block.TryGetProperty("context", out var context) || context.ValueKind != JsonValueKind.Object) continue;
                foreach (var key in new[] { "previous_blocks", "next_blocks", "active_heading_stack" })
                    if (context.TryGetProperty(key, out var items) && items.ValueKind == JsonValueKind.Array)
                        values.AddRange(items.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
            }
            var total = values.Sum(x => Encoding.UTF8.GetByteCount(x));
            var unique = values.Distinct(StringComparer.Ordinal).Sum(x => Encoding.UTF8.GetByteCount(x));
            return Math.Max(0, total - unique);
        }
        catch (JsonException) { return 0; }
    }

    private static string BuildReport(object manifest, IReadOnlyList<DocumentPlan> docs, object scale, IReadOnlyList<TestResult> tests) => $"# V8H0 — upstream heading extraction + binding preflight\n\nStatus: **READY_FOR_V8H0_HEADING_PROVIDER_AUTHORIZATION**.\n\nThis artifact is source-only and provider-free. It reuses the existing PDF production role/span contract and freezes a complete parser line universe plus deterministic role/pointer request shards. No Gold, identity candidates, historical predictions, or evaluation artifacts were read.\n\n- Documents: **{docs.Count}**\n- Parser source lines preserved: **{docs.Sum(x => x.SourceLines.Count):N0}**\n- Production candidate blocks before budget: **{docs.Sum(x => x.CandidateBlocks.Count):N0}**\n- Selected analyst blocks: **{docs.Sum(x => x.SelectedBlocks.Count):N0}**\n- Role requests: **{docs.Sum(x => x.RoleRequests.Count):N0}**\n- Conditional pointer-span request upper bound: **{docs.Sum(x => x.SpanRequests.Count):N0}**\n- Provider/model calls: **0/0**\n- Gold reads: **0**\n\n## Reused detector\n\n`PdfLineExtraction → PdfLineBlockFilter → PdfSemanticBlockGrouper → PdfStyleClusterProfile → PdfCandidateContextBuilder → PdfCandidateRanker → PdfLayoutEvidenceOutline.BuildBroadCandidates/SelectRankedCandidates → PdfBlockAnalyst role/span contract`. The model sees only local opaque block refs (`b1`, `b2`, ...); canonical source line identities remain harness-owned handle-map data.\n\n## Binding boundary\n\nRole output may select a supplied block ref. Pointer output may select only parser-owned UTF-16 offsets. The harness owns exact source text, source line IDs, and provenance. Unknown/fabricated/missing/duplicate refs are fail-closed. Level, parent, semantic identity, and identity pair labels are outside this lane.\n\n## Offline contract tests\n\n{tests.Count(x => x.Pass)}/{tests.Count} synthetic binding checks passed.\n\n## Next\n\nProvider authorization may be considered for the frozen role request set. Pointer-span requests are conditional upper-bound shards and must only run for role-selected blocks; no V8H0 prediction is present yet.\n";

    private static JsonElement Load(string path) => JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    private static string Full(string root, string relative) => Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
    private static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static int EstimateTokens(string serialized) => Math.Max(1, (int)Math.Ceiling(Encoding.UTF8.GetByteCount(serialized) / BytesPerEstimatedToken));
    private static void Write(string path, object value, bool indented = true) => File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed record SourceRow(string SourceId, string Name, string Path, string ByteSha256, string Family);
    private sealed record SourceLine(string Handle, string SourceOccurrenceId, int LineIndex, int Page, int DocumentOrder, string Text, double Y, double Left, double Right, double Top, double Bottom, double FontSize, double BoldRatio, double ItalicRatio, string FontName, string FillColorKey);
    private sealed record SelectedBlock(string OpaqueRef, PdfSemanticBlock Block);
    private sealed record DocumentPlan(string DocumentId, string ByteSha256, IReadOnlyList<SourceLine> SourceLines, IReadOnlyList<PdfSemanticBlock> CandidateBlocks, IReadOnlyList<SelectedBlock> SelectedBlocks, IReadOnlyDictionary<string, SourceLine> LineBySourceId, IReadOnlyList<RequestRow> RoleRequests, IReadOnlyList<RequestRow> SpanRequests);
    private sealed record RequestRow(string DocumentId, string Stage, string ShardId, int ShardOrdinal, IReadOnlyList<string> BlockRefs, IReadOnlyList<string> SourceRefs, string SerializedRequest, string SystemPrompt, string UserPrompt, int RequestBytes, int EstimatedInputTokens, int RepeatedContextBytes, string RequestSha256)
    {
        public object ToPublic() => new { documentId = DocumentId, stage = Stage, shardId = ShardId, shardOrdinal = ShardOrdinal, blockRefs = BlockRefs, sourceRefs = SourceRefs, request = JsonDocument.Parse(SerializedRequest).RootElement.Clone(), requestBytes = RequestBytes, estimatedInputTokens = EstimatedInputTokens, repeatedContextBytes = RepeatedContextBytes, requestSha256 = RequestSha256 };
        public object ToScalePublic() => new { documentId = DocumentId, stage = Stage, shardId = ShardId, blockCount = BlockRefs.Count, sourceRefCount = SourceRefs.Count, requestBytes = RequestBytes, estimatedInputTokens = EstimatedInputTokens, repeatedContextBytes = RepeatedContextBytes, requestSha256 = RequestSha256 };
    }
}
