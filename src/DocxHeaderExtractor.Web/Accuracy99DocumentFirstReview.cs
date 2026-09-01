using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.AgentHarness;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Web;

/// <summary>
/// Evaluation-only document-first review storage. It transports the existing Accuracy-99 JSONL
/// contract; it does not create a second source schema and never participates in production
/// extraction.
/// </summary>
public sealed class Accuracy99DocumentFirstReview
{
    public const string SourceCatalogVersion = "accuracy99-parser-source-catalog-v1";
    private const string ProductionBaselineRevision = "732c3505afc5dd312423ed0fa58056192fb39608";
    private static readonly string[] Labels = ["HEADING", "NON_HEADING", "UNCERTAIN", "EXCLUDED"];
    private readonly string _root;

    public Accuracy99DocumentFirstReview()
    {
        _root = Environment.GetEnvironmentVariable("DHX_ACCURACY99_WORK_DIR") is { Length: > 0 } configured
            ? Path.GetFullPath(configured)
            : Path.Combine(Directory.GetCurrentDirectory(), "eval", "accuracy99", "adjudication", "work");
        Directory.CreateDirectory(_root);
    }

    public sealed record Session(string Id, string DocumentPath, ReviewPacket Packet, bool Frozen);

    public async Task<Session> CreateOrResumeAsync(IFormFile upload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(upload);
        if (upload.Length == 0) throw new InvalidDataException("Chưa chọn DOCX.");
        var extension = Path.GetExtension(upload.FileName).ToLowerInvariant();
        if (extension is not ".docx" and not ".docm")
            throw new InvalidDataException("Human Review document-first chỉ nhận .docx hoặc .docm.");

        await using var input = upload.OpenReadStream();
        var bytes = await ReadAllAsync(input, ct);
        var id = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var documentPath = Path.Combine(_root, id + extension);
        if (!File.Exists(documentPath)) await File.WriteAllBytesAsync(documentPath, bytes, ct);

        var source = new OpenXmlDocumentSource().Read(documentPath);
        var canonical = FindCanonicalPacket(source);
        var packet = BuildPacket(source, canonical, id);
        var frozenPath = FrozenPath(id);
        var draftPath = DraftPath(id);
        var frozen = File.Exists(frozenPath);
        if (frozen)
            packet = ReadPacket(frozenPath);
        else if (File.Exists(draftPath))
            packet = ReadPacket(draftPath);
        else
            await WritePacketAsync(draftPath, packet, ct);

        return new Session(id, documentPath, packet, frozen);
    }

    public Session Load(string id)
    {
        ValidateId(id);
        var documentPath = Directory.EnumerateFiles(_root, id + ".*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => Path.GetExtension(path) is ".docx" or ".docm")
            ?? throw new FileNotFoundException("Review session document not found.");
        var frozenPath = FrozenPath(id);
        var draftPath = DraftPath(id);
        var path = File.Exists(frozenPath) ? frozenPath : draftPath;
        if (!File.Exists(path)) throw new FileNotFoundException("Review session packet not found.");
        return new Session(id, documentPath, ReadPacket(path), File.Exists(frozenPath));
    }

    public async Task SaveDraftAsync(string id, string jsonl, CancellationToken ct)
    {
        var session = Load(id);
        if (session.Frozen) throw new InvalidOperationException("GOLD_FROZEN session is immutable.");
        var packet = ParsePacket(jsonl);
        EnsureSameSourceContract(session.Packet, packet);
        var status = String(packet.Manifest, "reviewStatus");
        if (status is not "REVIEW_DRAFT" and not "REVIEW_COMPLETE")
            throw new InvalidDataException("Draft reviewStatus must be REVIEW_DRAFT or REVIEW_COMPLETE.");
        await WritePacketAsync(DraftPath(id), packet, ct);
    }

    public IReadOnlyList<string> Validate(string id)
    {
        var session = Load(id);
        return ValidatePacket(session.Packet, requireComplete: false);
    }

    public async Task<Session> FreezeAsync(string id, CancellationToken ct)
    {
        var session = Load(id);
        if (session.Frozen) throw new InvalidOperationException("GOLD_FROZEN session already exists; it was not overwritten.");
        var errors = ValidatePacket(session.Packet, requireComplete: true);
        if (errors.Count > 0) throw new InvalidDataException(string.Join("; ", errors));
        session.Packet.Manifest["reviewStatus"] = "GOLD_FROZEN";
        session.Packet.Manifest["frozenAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        await WritePacketAsync(FrozenPath(id), session.Packet, ct);
        TryDelete(DraftPath(id));
        return new Session(id, session.DocumentPath, session.Packet, true);
    }

    public async Task<object> CompareAsync(string id, CancellationToken ct)
    {
        var session = Load(id);
        if (!session.Frozen || ManifestStatus(session.Packet.Manifest) != "GOLD_FROZEN")
            throw new InvalidOperationException("Run & Compare requires explicit GOLD_FROZEN review.");

        var options = new PipelineOptions
        {
            DisableLlm = true,
            Extraction = new ExtractionOptions(),
        };
        using var tool = new PipelineDocumentExtractionTool(options);
        var outline = await tool.ExecuteAsync(new AgentToolInvocation(new DocumentAgentRequest(session.DocumentPath), 0), ct);

        var gold = session.Packet.Occurrences
            .Where(row => EffectiveLabel(row) == "HEADING")
            .ToDictionary(row => String(row, "sourceId"), StringComparer.Ordinal);
        var actual = outline.Headings
            .Where(row => !string.IsNullOrWhiteSpace(row.SourceId ?? row.StableId))
            .ToDictionary(row => row.SourceId ?? row.StableId!, StringComparer.Ordinal);
        var truePositiveIds = gold.Keys.Intersect(actual.Keys, StringComparer.Ordinal).OrderBy(idValue => idValue, StringComparer.Ordinal).ToArray();
        var falsePositiveIds = actual.Keys.Except(gold.Keys, StringComparer.Ordinal).OrderBy(idValue => idValue, StringComparer.Ordinal).ToArray();
        var falseNegativeIds = gold.Keys.Except(actual.Keys, StringComparer.Ordinal).OrderBy(idValue => idValue, StringComparer.Ordinal).ToArray();

        var exactSpanPairs = truePositiveIds.Where(sourceId =>
        {
            var row = gold[sourceId]; var heading = actual[sourceId];
            var start = Int(row, "headingStart"); var end = Int(row, "headingEnd");
            return start is not null && end is not null && heading.HeadingSpan is not null &&
                   heading.HeadingSpan.Start == start && heading.HeadingSpan.End == end;
        }).ToArray();
        var levelPairs = truePositiveIds.Where(sourceId => String(gold[sourceId], "levelReviewStatus") == "REVIEWED").ToArray();
        var levelCorrect = levelPairs.Where(sourceId => Int(gold[sourceId], "level") == actual[sourceId].Level).ToArray();
        var parentBySourceId = InferParents(outline.Headings);
        var parentPairs = truePositiveIds.Where(sourceId => String(gold[sourceId], "parentReviewStatus") is "ROOT" or "PARENT_REVIEWED").ToArray();
        var parentCorrect = parentPairs.Where(sourceId =>
        {
            var row = gold[sourceId];
            var expected = String(row, "parentReviewStatus") == "ROOT" ? null :
                gold.Values.FirstOrDefault(candidate => String(candidate, "goldHeadingId") == String(row, "parentGoldId")) is { } parent
                    ? String(parent, "sourceId") : null;
            return parentBySourceId.GetValueOrDefault(sourceId) == expected;
        }).ToArray();

        return new
        {
            sessionId = session.Id,
            datasetId = String(session.Packet.Manifest, "datasetId"),
            pipelineRoute = outline.DeterministicRoute ?? "current-authority-pipeline",
            providerCalls = 0,
            metrics = new
            {
                headingPrecision = Ratio(truePositiveIds.Length, actual.Count),
                headingRecall = Ratio(truePositiveIds.Length, gold.Count),
                f1 = F1(truePositiveIds.Length, actual.Count, gold.Count),
                exactSpanAccuracy = Ratio(exactSpanPairs.Length, truePositiveIds.Length),
                levelAccuracy = Ratio(levelCorrect.Length, levelPairs.Length),
                parentAccuracy = Ratio(parentCorrect.Length, parentPairs.Length),
            },
            counts = new { tp = truePositiveIds.Length, fp = falsePositiveIds.Length, fn = falseNegativeIds.Length },
            tp = truePositiveIds.Select(sourceId => Coordinate(gold[sourceId])).ToArray(),
            fp = falsePositiveIds.Select(sourceId => Coordinate(actual[sourceId], session.Packet.Occurrences.FirstOrDefault(row => String(row, "sourceId") == sourceId))).ToArray(),
            fn = falseNegativeIds.Select(sourceId => Coordinate(gold[sourceId])).ToArray(),
            exactSpan = new { correct = exactSpanPairs.Length, total = truePositiveIds.Length },
            level = new { correct = levelCorrect.Length, total = levelPairs.Length },
            parent = new { correct = parentCorrect.Length, total = parentPairs.Length },
        };
    }

    private ReviewPacket BuildPacket(SourceDocument source, ReviewPacket? canonical, string documentHash)
    {
        var canonicalRows = canonical?.Occurrences.ToDictionary(
            row => String(row, "sourceId"), StringComparer.Ordinal) ?? new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var canonicalManifest = canonical?.Manifest;
        var datasetId = canonicalManifest is not null ? String(canonicalManifest, "datasetId") :
            Path.GetFileNameWithoutExtension(source.FileName) + "-" + ShortHash(source.DocumentId);
        var documentId = canonicalManifest is not null ? String(canonicalManifest, "documentId") : "sha256:" + documentHash;
        var occurrences = new List<JsonObject>();
        for (var index = 0; index < source.Paragraphs.Count; index++)
        {
            var paragraph = source.Paragraphs[index];
            canonicalRows.TryGetValue(paragraph.SourceId, out var historicalRow);
            var row = new JsonObject
            {
                ["recordType"] = "occurrence",
                ["datasetId"] = datasetId,
                ["documentId"] = documentId,
                ["sourceId"] = paragraph.SourceId,
                ["sourceOrdinal"] = paragraph.SourceOrdinal,
                ["rawSourceText"] = paragraph.Text,
                ["rawSourceSpan"] = new JsonObject { ["start"] = 0, ["end"] = paragraph.Text.Length },
                ["rawTextLength"] = paragraph.Text.Length,
                ["sourceType"] = source.SourceKind,
                ["previousSourceText"] = index == 0 ? null : source.Paragraphs[index - 1].Text,
                ["nextSourceText"] = index + 1 >= source.Paragraphs.Count ? null : source.Paragraphs[index + 1].Text,
                ["page"] = null,
                ["historicalProvenanceStatus"] = historicalRow is null ? "NONE" : String(historicalRow, "historicalProvenanceStatus", "NONE"),
                ["historicalPositiveReferences"] = historicalRow?["historicalPositiveReferences"]?.DeepClone() ?? new JsonArray(),
                ["adjudicatedLabel"] = null,
                ["initialAdjudicatedLabel"] = null,
                ["finalAdjudicatedLabel"] = null,
                ["resolutionReason"] = null,
                ["headingStart"] = null,
                ["headingEnd"] = null,
                ["headingText"] = null,
                ["structuralType"] = null,
                ["level"] = null,
                ["levelReviewStatus"] = null,
                ["parentGoldId"] = null,
                ["parentReviewStatus"] = null,
                ["goldHeadingId"] = null,
                ["reviewer"] = null,
                ["reviewNotes"] = null,
                ["parserMetadata"] = new JsonObject
                {
                    ["styleId"] = paragraph.Style.StyleId,
                    ["styleName"] = paragraph.Style.StyleName,
                    ["builtInHeadingStyleLevel"] = paragraph.Style.BuiltInHeadingStyleLevel,
                    ["outlineLevel"] = paragraph.Style.OutlineLevel,
                    ["bold"] = paragraph.Style.Bold,
                    ["italic"] = paragraph.Style.Italic,
                    ["underline"] = paragraph.Style.Underline,
                    ["allCaps"] = paragraph.Style.AllCaps,
                    ["fontSizePt"] = paragraph.Style.FontSizePt,
                    ["alignment"] = paragraph.Style.Alignment,
                    ["numberingId"] = paragraph.Numbering.NumberingId,
                    ["numberingLevel"] = paragraph.Numbering.NumberingLevel,
                    ["numberLabel"] = paragraph.Numbering.NumberLabel,
                    ["numberingFormat"] = paragraph.Numbering.NumberingFormat,
                    ["tableDepth"] = paragraph.Layout.TableDepth,
                    ["sectionIndex"] = paragraph.Layout.SectionIndex,
                    ["inTableOfContents"] = paragraph.InTableOfContents,
                },
            };
            if (historicalRow is not null)
                foreach (var field in new[] { "historicalProvenanceStatus", "historicalPositiveReferences" })
                    row[field] = historicalRow[field]?.DeepClone();
            occurrences.Add(row);
        }

        var hash = SourceCatalogHash(datasetId, documentId, occurrences);
        var manifest = new JsonObject
        {
            ["recordType"] = "manifest",
            ["schemaVersion"] = 1,
            ["artifactKind"] = "accuracy99_development_source_first_review",
            ["datasetId"] = datasetId,
            ["documentId"] = documentId,
            ["productionBaselineRevision"] = canonicalManifest?["productionBaselineRevision"]?.DeepClone() ?? ProductionBaselineRevision,
            ["sourceCatalogVersion"] = SourceCatalogVersion,
            ["sourceCatalogHash"] = canonicalManifest?["sourceCatalogHash"]?.DeepClone() ?? hash,
            ["catalogOccurrenceCount"] = occurrences.Count,
            ["blind"] = false,
            ["predictionsIncluded"] = false,
            ["reviewStatus"] = "REVIEW_DRAFT",
            ["requiredLabels"] = new JsonArray("HEADING", "NON_HEADING", "UNCERTAIN", "EXCLUDED"),
                    ["documentSourceHash"] = documentHash,
        };
        return new ReviewPacket(manifest, occurrences);
    }

    private ReviewPacket? FindCanonicalPacket(SourceDocument source)
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "eval", "accuracy99", "adjudication", "development");
        if (!Directory.Exists(directory)) return null;
        foreach (var path in Directory.EnumerateFiles(directory, "*.review.jsonl"))
        {
            try
            {
                var candidate = ReadPacket(path);
                if (candidate.Occurrences.Count != source.Paragraphs.Count) continue;
                if (candidate.Occurrences.Select(row => String(row, "sourceId")).SequenceEqual(source.Paragraphs.Select(p => p.SourceId)) &&
                    candidate.Occurrences.Select(row => String(row, "rawSourceText")).SequenceEqual(source.Paragraphs.Select(p => p.Text)))
                    return candidate;
            }
            catch (Exception) { /* unrelated or incomplete packet; continue scanning */ }
        }
        return null;
    }

    private static IReadOnlyList<string> ValidatePacket(ReviewPacket packet, bool requireComplete)
    {
        var errors = new List<string>();
        if (String(packet.Manifest, "recordType") != "manifest") errors.Add("MANIFEST_RECORD_TYPE_INVALID");
        if (String(packet.Manifest, "sourceCatalogVersion") != SourceCatalogVersion) errors.Add("SOURCE_CATALOG_VERSION_MISMATCH");
        if (packet.Manifest["predictionsIncluded"]?.GetValue<bool>() == true) errors.Add("PRODUCTION_PREDICTION_EXPOSED");
        if (packet.Occurrences.Count != packet.Manifest["catalogOccurrenceCount"]?.GetValue<int>()) errors.Add("OCCURRENCE_COUNT_MISMATCH");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in packet.Occurrences)
        {
            var sourceId = String(row, "sourceId");
            if (string.IsNullOrWhiteSpace(sourceId) || !ids.Add(sourceId)) errors.Add($"DUPLICATE_OR_EMPTY_SOURCE_ID:{sourceId}");
            var label = EffectiveLabel(row);
            if (label is null) { if (requireComplete) errors.Add($"UNREVIEWED_OCCURRENCE:{sourceId}"); continue; }
            if (!Labels.Contains(label, StringComparer.Ordinal)) { errors.Add($"INVALID_LABEL:{sourceId}"); continue; }
            if (string.IsNullOrWhiteSpace(String(row, "reviewer"))) errors.Add($"REVIEWER_REQUIRED:{sourceId}");
            if (label == "HEADING") ValidateHeading(row, errors);
            else ValidateNonHeading(row, errors);
        }
        var headings = packet.Occurrences.Where(row => EffectiveLabel(row) == "HEADING")
            .ToDictionary(row => String(row, "goldHeadingId"), StringComparer.Ordinal);
        foreach (var row in packet.Occurrences.Where(row => String(row, "parentReviewStatus") == "PARENT_REVIEWED"))
            if (!headings.ContainsKey(String(row, "parentGoldId"))) errors.Add($"PARENT_GOLD_ID_NOT_FOUND:{String(row, "sourceId")}");
        return errors.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    private static void ValidateHeading(JsonObject row, ICollection<string> errors)
    {
        var text = String(row, "rawSourceText");
        var start = Int(row, "headingStart"); var end = Int(row, "headingEnd");
        if (start is null || end is null || string.IsNullOrEmpty(row["headingText"]?.GetValue<string>())) errors.Add($"HEADING_SPAN_REQUIRED:{String(row, "sourceId")}");
        else if (start < 0 || end <= start || end > text.Length || text[start.Value..end.Value] != String(row, "headingText")) errors.Add($"HEADING_SPAN_TEXT_MISMATCH:{String(row, "sourceId")}");
        var sourceId = String(row, "sourceId");
        var goldHeadingId = String(row, "goldHeadingId");
        if (string.IsNullOrWhiteSpace(goldHeadingId)) errors.Add($"GOLD_HEADING_ID_REQUIRED:{sourceId}");
        else if (start is not null && end is not null && goldHeadingId != ComputeGoldHeadingId(String(row, "documentId"), sourceId, start.Value, end.Value)) errors.Add($"GOLD_HEADING_ID_MISMATCH:{sourceId}");
        if (string.IsNullOrWhiteSpace(String(row, "structuralType"))) errors.Add($"STRUCTURAL_TYPE_REQUIRED:{String(row, "sourceId")}");
        var levelStatus = String(row, "levelReviewStatus"); var level = Int(row, "level");
        if (levelStatus == "REVIEWED" && (level is null || level < 1)) errors.Add($"REVIEWED_LEVEL_INVALID:{String(row, "sourceId")}");
        else if (levelStatus == "LEVEL_NOT_REVIEWED" && level is not null) errors.Add($"UNREVIEWED_LEVEL_MUST_BE_NULL:{String(row, "sourceId")}");
        else if (levelStatus is not ("REVIEWED" or "LEVEL_NOT_REVIEWED")) errors.Add($"LEVEL_REVIEW_STATUS_REQUIRED:{String(row, "sourceId")}");
        var parent = String(row, "parentReviewStatus");
        if (parent is not ("ROOT" or "PARENT_REVIEWED" or "PARENT_UNKNOWN")) errors.Add($"PARENT_REVIEW_STATUS_REQUIRED:{String(row, "sourceId")}");
        if (parent == "PARENT_REVIEWED" && string.IsNullOrWhiteSpace(String(row, "parentGoldId"))) errors.Add($"PARENT_GOLD_ID_REQUIRED:{String(row, "sourceId")}");
        if (parent is "ROOT" or "PARENT_UNKNOWN" && row["parentGoldId"] is not null) errors.Add($"PARENT_GOLD_ID_CONTRADICTS_STATUS:{String(row, "sourceId")}");
    }

    private static void ValidateNonHeading(JsonObject row, ICollection<string> errors)
    {
        foreach (var field in new[] { "headingStart", "headingEnd", "headingText", "structuralType", "level", "levelReviewStatus", "parentGoldId", "parentReviewStatus", "goldHeadingId" })
            if (row[field] is not null) { errors.Add($"NON_HEADING_HAS_HEADING_FIELDS:{String(row, "sourceId")}"); break; }
    }

    private static string? EffectiveLabel(JsonObject row) =>
        row["finalAdjudicatedLabel"]?.GetValue<string>() ?? row["initialAdjudicatedLabel"]?.GetValue<string>() ?? row["adjudicatedLabel"]?.GetValue<string>();

    private static string String(JsonObject node, string property, string fallback = "") => node[property]?.GetValue<string>() ?? fallback;
    private static int? Int(JsonObject node, string property) => node[property] is null ? null : node[property]!.GetValue<int>();

    private static string ManifestStatus(JsonObject manifest) => String(manifest, "reviewStatus");

    private static double Ratio(int numerator, int denominator) => denominator == 0 ? 0 : (double)numerator / denominator;
    private static double F1(int tp, int actual, int gold)
    {
        var precision = Ratio(tp, actual); var recall = Ratio(tp, gold);
        return precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
    }

    private static Dictionary<string, string?> InferParents(IReadOnlyList<HeadingRecord> headings)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        var stack = new List<(int Level, string SourceId)>();
        foreach (var heading in headings.OrderBy(item => item.Index))
        {
            var sourceId = heading.SourceId ?? heading.StableId;
            if (string.IsNullOrWhiteSpace(sourceId)) continue;
            var level = heading.Level;
            if (level is null) { result[sourceId] = null; continue; }
            while (stack.Count > 0 && stack[^1].Level >= level.Value) stack.RemoveAt(stack.Count - 1);
            result[sourceId] = stack.Count == 0 ? null : stack[^1].SourceId;
            stack.Add((level.Value, sourceId));
        }
        return result;
    }

    private static object Coordinate(JsonObject goldRow) => new
    {
        documentId = String(goldRow, "documentId"),
        sourceId = String(goldRow, "sourceId"),
        sourceOrdinal = Int(goldRow, "sourceOrdinal"),
        rawSourceText = String(goldRow, "rawSourceText"),
        rawSourceSpan = goldRow["rawSourceSpan"],
    };

    private static object Coordinate(HeadingRecord heading, JsonObject? sourceRow) => new
    {
        documentId = sourceRow is null ? null : String(sourceRow, "documentId"),
        sourceId = heading.SourceId ?? heading.StableId,
        sourceOrdinal = heading.Index,
        rawSourceText = sourceRow is null ? heading.Text : String(sourceRow, "rawSourceText"),
        rawSourceSpan = sourceRow?["rawSourceSpan"],
    };

    private void EnsureSameSourceContract(ReviewPacket expected, ReviewPacket actual)
    {
        if (String(expected.Manifest, "datasetId") != String(actual.Manifest, "datasetId") ||
            String(expected.Manifest, "documentId") != String(actual.Manifest, "documentId") ||
            String(expected.Manifest, "sourceCatalogHash") != String(actual.Manifest, "sourceCatalogHash") ||
            expected.Occurrences.Count != actual.Occurrences.Count)
            throw new InvalidDataException("Review source identity changed.");
        for (var i = 0; i < expected.Occurrences.Count; i++)
        {
            var left = expected.Occurrences[i]; var right = actual.Occurrences[i];
            foreach (var field in new[] { "sourceId", "sourceOrdinal", "rawSourceText", "rawSourceSpan", "rawTextLength", "sourceType" })
                if (left[field]?.ToJsonString() != right[field]?.ToJsonString()) throw new InvalidDataException($"Parser source field changed: {String(right, "sourceId")}");
        }
        if (actual.Manifest["predictionsIncluded"]?.GetValue<bool>() == true) throw new InvalidDataException("Prediction data is forbidden in source-first review.");
    }

    private static ReviewPacket ParsePacket(string text)
    {
        var nodes = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonNode.Parse(line)?.AsObject() ?? throw new InvalidDataException("Invalid review JSONL line."))
            .ToArray();
        if (nodes.Length == 0 || String(nodes[0], "recordType") != "manifest") throw new InvalidDataException("Review JSONL manifest is missing.");
        return new ReviewPacket(nodes[0], nodes.Skip(1).ToList());
    }

    private static ReviewPacket ReadPacket(string path) => ParsePacket(File.ReadAllText(path, Encoding.UTF8));

    private static async Task WritePacketAsync(string path, ReviewPacket packet, CancellationToken ct)
    {
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, ToJsonLines(packet), new UTF8Encoding(false), ct);
        File.Move(temp, path, true);
    }

    public static string ToJsonLines(ReviewPacket packet) =>
        string.Join('\n', new[] { packet.Manifest.ToJsonString() }.Concat(packet.Occurrences.Select(row => row.ToJsonString()))) + "\n";

    private static string SourceCatalogHash(string datasetId, string documentId, IReadOnlyList<JsonObject> rows)
    {
        var builder = new StringBuilder(); Append(builder, datasetId); Append(builder, documentId); Append(builder, SourceCatalogVersion);
        foreach (var row in rows.OrderBy(row => Int(row, "sourceOrdinal")).ThenBy(row => String(row, "sourceId"), StringComparer.Ordinal))
        {
            Append(builder, String(row, "sourceId")); Append(builder, Int(row, "sourceOrdinal")?.ToString(CultureInfo.InvariantCulture));
            Append(builder, String(row, "rawSourceText")); var span = row["rawSourceSpan"]!.AsObject();
            Append(builder, Int(span, "start")?.ToString(CultureInfo.InvariantCulture)); Append(builder, Int(span, "end")?.ToString(CultureInfo.InvariantCulture));
            Append(builder, String(row, "sourceType")); Append(builder, String(row, "page"));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static void Append(StringBuilder builder, string? value) { value ??= string.Empty; builder.Append(value.Length).Append(':').Append(value).Append('|'); }
    private static string ShortHash(string value, int length = 12) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..length];
    private static string ComputeGoldHeadingId(string documentId, string sourceId, int start, int end)
    {
        var builder = new StringBuilder(); Append(builder, documentId); Append(builder, sourceId); Append(builder, start.ToString(CultureInfo.InvariantCulture)); Append(builder, end.ToString(CultureInfo.InvariantCulture));
        return "gold-heading:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }
    private static string DraftPath(string id) => Path.Combine(WorkingRoot(), id + ".review.draft.jsonl");
    private static string FrozenPath(string id) => Path.Combine(WorkingRoot(), id + ".review.frozen.jsonl");
    private static string WorkingRoot() => Environment.GetEnvironmentVariable("DHX_ACCURACY99_WORK_DIR") is { Length: > 0 } configured ? Path.GetFullPath(configured) : Path.Combine(Directory.GetCurrentDirectory(), "eval", "accuracy99", "adjudication", "work");
    private static void ValidateId(string id) { if (id.Length != 64 || id.Any(ch => !Uri.IsHexDigit(ch))) throw new InvalidDataException("Invalid review session id."); }
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } }
    private static async Task<byte[]> ReadAllAsync(Stream stream, CancellationToken ct) { using var memory = new MemoryStream(); await stream.CopyToAsync(memory, ct); return memory.ToArray(); }

    public sealed record ReviewPacket(JsonObject Manifest, List<JsonObject> Occurrences);
}
