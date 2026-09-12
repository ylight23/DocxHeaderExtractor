using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

public sealed record SourceSliceBoundary(int Offset, IReadOnlyList<string> Kinds);

public sealed record SourceSlice(
    string SliceId,
    string SourceId,
    int Ordinal,
    int Start,
    int End,
    string Text,
    IReadOnlyList<string> BoundaryKindsAtStart,
    IReadOnlyList<string> BoundaryKindsAtEnd);

public sealed record SourceSliceabilityResult(
    string SourceAlias,
    string SourceId,
    int SourceLength,
    IReadOnlyList<SourceSliceBoundary> Boundaries,
    IReadOnlyList<SourceSlice> Slices,
    bool ReconstructedExactly,
    string SourceTextSha256,
    string ReconstructedTextSha256);

public sealed record SourceSliceProposalMapping(
    int Index,
    string Source,
    string Text,
    string Role,
    string Classification,
    int Occurrences,
    IReadOnlyList<string> SliceIds);

/// <summary>
/// Offline audit of parser-owned source slicing. It creates no candidates and makes no semantic
/// decision. Existing source boundaries are the only inputs to slice construction.
/// </summary>
public static class A99V6SourceSliceabilityAuditRunner
{
    private const string InventoryPath = "eval/a99-dataset/document-inventory.v1.json";
    private const string FrozenInputRoot = "eval/a99-closed-loop/production-v6-accuracy-full-e2e";
    private const string OutputRoot = "eval/a99-closed-loop/source-sliceability-audit/DOC-0205";
    private const string DocumentId = "DOC-0205";
    private const string TargetSourceId = "S0003";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var source = LoadSource(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(output);

        var audited = source.Paragraphs
            .Where(paragraph => !string.IsNullOrWhiteSpace(paragraph.Text))
            .Select((paragraph, index) => BuildSlices(paragraph, $"S{index + 1:0000}"))
            .ToArray();
        var target = audited.Single(item => item.SourceAlias == TargetSourceId);

        // This is the freeze point. No Gold is opened before this source-only artifact exists.
        var sourceArtifact = new
        {
            schemaVersion = "a99-v6-source-sliceability-audit-source-v1",
            documentId = DocumentId,
            sourceSha256 = Sha256(source.SourcePath),
            sourceAlias = target.SourceAlias,
            sourceId = target.SourceId,
            sourceLength = target.SourceLength,
            parserOwnedBoundaryKinds = new[] { "PARAGRAPH", "HARD_LINE_BREAK", "TEXT_RUN_SPAN", "SOURCE_SEGMENT" },
            goldSpecificBoundaries = 0,
            candidateDerivedBoundaries = 0,
            modelCalls = 0,
            providerCalls = 0,
            aliases = audited,
            reconstruction = audited.Select(item => new
            {
                item.SourceId,
                item.SourceLength,
                item.ReconstructedExactly,
                item.SourceTextSha256,
                item.ReconstructedTextSha256,
            }).ToArray(),
            goldReadBeforeFreeze = false,
        };
        var sourceArtifactPath = Path.Combine(output, "source-slices.freeze.v1.json");
        await WriteJson(sourceArtifactPath, sourceArtifact, ct);
        var sourceArtifactSha = Sha256(sourceArtifactPath);
        await WriteJson(Path.Combine(output, "freeze.v1.json"), new
        {
            schemaVersion = "a99-v6-source-sliceability-audit-freeze-v1",
            documentId = DocumentId,
            sourceSha256 = Sha256(source.SourcePath),
            sourceArtifactSha256 = sourceArtifactSha,
            sourceAlias = target.SourceAlias,
            sourceId = target.SourceId,
            boundaryPolicy = "PARSER_OWNED_ONLY",
            goldSpecificBoundaries = 0,
            modelCalls = 0,
            providerCalls = 0,
            goldReadBeforeFreeze = false,
            frozenUtc = DateTimeOffset.UtcNow,
        }, ct);

        var frozenRaw = LoadFrozenRaw(repoRoot);
        var proposalMap = MapProposals(source, target, frozenRaw);

        // Gold is deliberately opened only after the source slice map and freeze manifest exist.
        var goldPath = Path.Combine(repoRoot, "eval/a99-closed-loop/strict-gold-occurrence-v1", DocumentId + ".occurrence-gold-v1.json");
        var gold = ReasoningGoldArtifactLoader.LoadOccurrence(goldPath)
            .Where(item => item.HeadingSpan is not null && string.Equals(item.SourceId, source.Paragraphs.Single(p => p.SourceOrdinal == 3).SourceId, StringComparison.Ordinal))
            .Select(item => new { item.ExactText, Start = item.HeadingSpan!.Start, End = item.HeadingSpan.End })
            .ToArray();
        var goldMap = gold.Select(item => new
        {
            item.ExactText,
            item.Start,
            item.End,
            Classification = ClassifySpan(target, item.Start, item.End),
        }).ToArray();

        await WriteJson(Path.Combine(output, "proposal-slice-map.v1.json"), new
        {
            schemaVersion = "a99-v6-source-sliceability-proposal-map-v1",
            documentId = DocumentId,
            sourceAlias = target.SourceAlias,
            sourceId = target.SourceId,
            rawProposalCount = frozenRaw.Count,
            proposalMap,
            goldReadBeforeFreeze = false,
        }, ct);
        await WriteJson(Path.Combine(output, "gold-representability.v1.json"), new
        {
            schemaVersion = "a99-v6-source-sliceability-gold-representability-v1",
            documentId = DocumentId,
            sourceAlias = target.SourceAlias,
            sourceId = target.SourceId,
            goldCount = goldMap.Length,
            goldMap,
            counts = new
            {
                exactSingleSlice = goldMap.Count(item => item.Classification == "EXACT_SINGLE_SLICE"),
                exactMultiSlice = goldMap.Count(item => item.Classification == "EXACT_MULTI_SLICE"),
                unrepresentableBoundary = goldMap.Count(item => item.Classification == "UNREPRESENTABLE_BOUNDARY"),
                unresolved = goldMap.Count(item => item.Classification == "UNRESOLVED"),
                ambiguous = goldMap.Count(item => item.Classification == "AMBIGUOUS"),
            },
            goldReadBeforeFreeze = false,
        }, ct);

        var proposalCounts = new
        {
            exactSingleSlice = proposalMap.Count(item => item.Classification == "EXACT_SINGLE_SLICE"),
            exactMultiSlice = proposalMap.Count(item => item.Classification == "EXACT_MULTI_SLICE"),
            unresolved = proposalMap.Count(item => item.Classification == "UNRESOLVED"),
            ambiguous = proposalMap.Count(item => item.Classification == "AMBIGUOUS"),
            unrepresentableBoundary = proposalMap.Count(item => item.Classification == "UNREPRESENTABLE_BOUNDARY"),
        };
        var summary = new
        {
            schemaVersion = "a99-v6-source-sliceability-audit-summary-v1",
            documentId = DocumentId,
            sourceAlias = target.SourceAlias,
            sourceId = target.SourceId,
            sourceSha256 = Sha256(source.SourcePath),
            sourceLength = target.SourceLength,
            sliceCount = target.Slices.Count,
            boundaryCount = target.Boundaries.Count,
            losslessSourceReconstruction = audited.All(item => item.ReconstructedExactly),
            goldSpecificBoundaries = 0,
            modelTextRequiredForRepresentableSliceCases = 0,
            frozenRawProposalCount = frozenRaw.Count,
            proposalCounts,
            goldRepresentability = new
            {
                total = goldMap.Length,
                exactSingleSlice = goldMap.Count(item => item.Classification == "EXACT_SINGLE_SLICE"),
                exactMultiSlice = goldMap.Count(item => item.Classification == "EXACT_MULTI_SLICE"),
                unrepresentableBoundary = goldMap.Count(item => item.Classification == "UNREPRESENTABLE_BOUNDARY"),
                unresolved = goldMap.Count(item => item.Classification == "UNRESOLVED"),
                ambiguous = goldMap.Count(item => item.Classification == "AMBIGUOUS"),
            },
            modelCalls = 0,
            providerCalls = 0,
            goldFirewall = "PASS",
            decision = proposalCounts.exactSingleSlice + proposalCounts.exactMultiSlice > 0
                ? "SOURCE_SLICEABILITY_PARTIAL_REQUIRES_GENERIC_ELIGIBILITY_REPLAY"
                : "NO_USEFUL_PARSER_OWNED_SLICE_BOUNDARIES",
        };
        await WriteJson(Path.Combine(output, "summary.v1.json"), summary, ct);
        Console.WriteLine("MODEL_CALLS=0");
        Console.WriteLine("PROVIDER_CALLS=0");
        Console.WriteLine($"SOURCE_SLICE_COUNT={target.Slices.Count}");
        Console.WriteLine($"FROZEN_RAW_PROPOSALS={frozenRaw.Count}");
        Console.WriteLine($"SOURCE_SLICEABILITY_SUMMARY={Path.Combine(OutputRoot, "summary.v1.json")}");
        return 0;
    }

    public static SourceSliceabilityResult BuildSlices(SourceParagraph paragraph, string? sourceAlias = null)
    {
        ArgumentNullException.ThrowIfNull(paragraph);
        var kinds = new Dictionary<int, HashSet<string>> { [0] = ["PARAGRAPH_START"], [paragraph.Text.Length] = ["PARAGRAPH_END"] };
        AddBoundaries(paragraph.LineBreakOffsets, "HARD_LINE_BREAK");
        AddBoundaries(paragraph.TextSpans.SelectMany(span => new[] { span.Start, span.End }), "TEXT_RUN_SPAN");
        AddBoundaries(paragraph.SourceSegments.SelectMany(segment => new[] { segment.Start, segment.End }), "SOURCE_SEGMENT");
        var offsets = kinds.Keys.Where(offset => offset >= 0 && offset <= paragraph.Text.Length).Order().ToArray();
        var boundaries = offsets.Select(offset => new SourceSliceBoundary(offset, kinds[offset].Order(StringComparer.Ordinal).ToArray())).ToArray();
        var slices = new List<SourceSlice>();
        for (var index = 0; index < boundaries.Length - 1; index++)
        {
            var start = boundaries[index].Offset;
            var end = boundaries[index + 1].Offset;
            if (end <= start) continue;
            var address = sourceAlias ?? paragraph.SourceId.Replace("/", "_");
            slices.Add(new($"{address}.{slices.Count + 1:0000}", paragraph.SourceId,
                slices.Count + 1, start, end, paragraph.Text[start..end], boundaries[index].Kinds, boundaries[index + 1].Kinds));
        }
        var reconstructed = string.Concat(slices.Select(slice => slice.Text));
        return new(sourceAlias ?? paragraph.SourceId.Replace("/", "_"), paragraph.SourceId, paragraph.Text.Length, boundaries, slices,
            string.Equals(reconstructed, paragraph.Text, StringComparison.Ordinal),
            Sha256Text(paragraph.Text), Sha256Text(reconstructed));

        void AddBoundaries(IEnumerable<int> values, string kind)
        {
            foreach (var offset in values)
            {
                if (offset < 0 || offset > paragraph.Text.Length) continue;
                if (!kinds.TryGetValue(offset, out var set)) kinds[offset] = set = [];
                set.Add(kind);
            }
        }
    }

    private static SourceDocument LoadSource(string repoRoot)
    {
        using var inventory = JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot, InventoryPath.Replace('/', Path.DirectorySeparatorChar))));
        var item = inventory.RootElement.GetProperty("documents").EnumerateArray()
            .Single(entry => entry.GetProperty("documentId").GetString() == DocumentId);
        var path = Path.Combine(repoRoot, item.GetProperty("sourcePath").GetString()!.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
        var expected = item.GetProperty("sourceSha256").GetString()!;
        var actual = Sha256(path);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SOURCE_HASH_MISMATCH:" + DocumentId);
        return new OpenXmlDocumentSource().Read(path) with { DocumentId = DocumentId };
    }

    private static IReadOnlyList<SemanticTextHeading> LoadFrozenRaw(string repoRoot)
    {
        var result = new List<SemanticTextHeading>();
        for (var repeat = 1; repeat <= 3; repeat++)
        {
            var path = Path.Combine(repoRoot, FrozenInputRoot.Replace('/', Path.DirectorySeparatorChar), DocumentId, $"r{repeat}", "prediction.v1.json");
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var raw = json.RootElement.GetProperty("rawModelHeadings").EnumerateArray()
                .Select(item => new SemanticTextHeading(item.GetProperty("source").GetString()!, item.GetProperty("text").GetString()!, item.GetProperty("role").GetString()!,
                    item.TryGetProperty("occurrence", out var occurrence) && occurrence.ValueKind != JsonValueKind.Null ? occurrence.GetInt32() : null,
                    item.TryGetProperty("leftExactContext", out var left) && left.ValueKind == JsonValueKind.String ? left.GetString() : null,
                    item.TryGetProperty("rightExactContext", out var right) && right.ValueKind == JsonValueKind.String ? right.GetString() : null))
                .Where(item => item.Source == TargetSourceId).ToArray();
            if (repeat == 1) result.AddRange(raw);
            else if (!result.Select(item => (item.Source, item.Text, item.Role)).SequenceEqual(raw.Select(item => (item.Source, item.Text, item.Role))))
                throw new InvalidDataException("FROZEN_RAW_REPEATS_NOT_SHAPE_EQUIVALENT");
        }
        return result;
    }

    private static IReadOnlyList<SourceSliceProposalMapping> MapProposals(SourceDocument source, SourceSliceabilityResult slices, IReadOnlyList<SemanticTextHeading> raw)
    {
        var paragraph = source.Paragraphs.Single(item => item.SourceId == slices.SourceId);
        return raw.Select((proposal, index) => MapOne(index + 1, proposal, paragraph.Text, slices)).ToArray();
    }

    private static SourceSliceProposalMapping MapOne(int index, SemanticTextHeading proposal, string source, SourceSliceabilityResult slices)
    {
        var positions = FindAll(source, proposal.Text);
        if (positions.Count == 0)
            return new(index, proposal.Source, proposal.Text, proposal.Role, "UNRESOLVED", 0, []);
        if (positions.Count > 1)
            return new(index, proposal.Source, proposal.Text, proposal.Role, "AMBIGUOUS", positions.Count, []);
        var span = new StructuralSpan(positions[0], positions[0] + proposal.Text.Length);
        return new(index, proposal.Source, proposal.Text, proposal.Role, ClassifySpan(slices, span.Start, span.End), 1, SliceIds(slices, span.Start, span.End));
    }

    private static string ClassifySpan(SourceSliceabilityResult slices, int start, int end)
    {
        if (start < 0 || end < start || end > slices.SourceLength) return "UNRESOLVED";
        var selected = slices.Slices.Where(slice => slice.Start >= start && slice.End <= end).ToArray();
        if (selected.Length == 0 || selected[0].Start != start || selected[^1].End != end) return "UNREPRESENTABLE_BOUNDARY";
        for (var index = 1; index < selected.Length; index++)
            if (selected[index].Start != selected[index - 1].End) return "UNREPRESENTABLE_BOUNDARY";
        return selected.Length == 1 ? "EXACT_SINGLE_SLICE" : "EXACT_MULTI_SLICE";
    }

    private static string[] SliceIds(SourceSliceabilityResult slices, int start, int end) =>
        slices.Slices.Where(slice => slice.Start >= start && slice.End <= end).Select(slice => slice.SliceId).ToArray();

    private static List<int> FindAll(string source, string text)
    {
        var positions = new List<int>();
        if (string.IsNullOrEmpty(text)) return positions;
        for (var offset = 0; offset <= source.Length - text.Length;)
        {
            var position = source.IndexOf(text, offset, StringComparison.Ordinal);
            if (position < 0) break;
            positions.Add(position);
            offset = position + Math.Max(1, text.Length);
        }
        return positions;
    }

    private static async Task WriteJson(string path, object value, CancellationToken ct) =>
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, ct);

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
