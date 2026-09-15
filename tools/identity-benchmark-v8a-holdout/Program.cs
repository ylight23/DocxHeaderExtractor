using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace IdentityBenchmarkV8AHoldout;

internal static class Program
{
    private const string InventoryRelative = "eval/a99-dataset/document-inventory.v1.json";
    private static readonly string[] SourcePoolPrefixes = { "todo10_8\\heading_corpus_95_word\\", "todo10_8\\generated-docx\\" };
    private const string OutputRelative = "artifacts/identity-benchmark/v8/holdout/source-only-freeze-v1";
    private const string SelectionSeed = "a99-v8a-new-document-holdout-v1";
    private const int TargetDocuments = 6;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly Regex DocIdRegex = new(@"\bDOC-\d{4}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SourceHashRegex = new(@"""(?:sourceSha256|sourceHash|sourceFingerprintSha256)""\s*:\s*""([a-fA-F0-9]{64})""", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public static async Task<int> Main(string[] args)
    {
        var root = Path.GetFullPath(args.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal)) ?? Directory.GetCurrentDirectory());
        try
        {
            await RunAsync(root, args.Contains("--replace-preflight", StringComparer.Ordinal));
            var manifestPath = Full(root, OutputRelative) + Path.DirectorySeparatorChar + "manifest.json";
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
            Console.WriteLine($"V8A_STATUS={manifest.RootElement.GetProperty("status").GetString()} PROVIDER_CALLS=0 GOLD_READ_COUNT=0");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine($"V8A_ERROR={ex.Message}"); return 2; }
    }

    private static async Task RunAsync(string root, bool replace)
    {
        var output = Full(root, OutputRelative);
        if (replace && Directory.Exists(output)) Directory.Delete(output, recursive: true);
        Require(!Directory.Exists(output) || !Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).Any(), "V8A_OUTPUT_ALREADY_EXISTS_NO_OVERWRITE");

        var inventoryPath = Full(root, InventoryRelative);
        using var inventoryDoc = JsonDocument.Parse(await File.ReadAllTextAsync(inventoryPath));
        var inventory = ReadInventory(inventoryDoc.RootElement);
        var provenance = ScanProvenance(root);
        var lineageDenied = inventory.Where(x => provenance.DocumentIds.Contains(x.DocumentId) || provenance.SourceHashes.Contains(x.SourceSha256)).Select(x => x.SourceSha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sourcePool = inventory.Where(IsSourcePoolRow).ToArray();
        var formatAudit = AuditFormats(root, sourcePool);
        var eligible = sourcePool.Where(x => x.SourcePath.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) && File.Exists(Full(root, x.SourcePath)) && !lineageDenied.Contains(x.SourceSha256)).GroupBy(x => x.SourceSha256, StringComparer.OrdinalIgnoreCase).Select(g => g.OrderBy(x => x.DocumentId, StringComparer.Ordinal).First()).ToArray();
        Directory.CreateDirectory(output);
        await WriteAsync(Path.Combine(output, "provenance-denylist.json"), new { schemaVersion = "a99-v8a-provenance-denylist-v1", generatedFrom = provenance.Files, historicallyExposedDocumentIds = provenance.DocumentIds.OrderBy(x => x, StringComparer.Ordinal).ToArray(), historicallyExposedSourceHashes = provenance.SourceHashes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(), sourceHashLineageDeniedCount = lineageDenied.Count, sourceOnlyScan = true, semanticLabelsRead = false, predictionContentUsed = false });
        if (eligible.Length < TargetDocuments)
        {
            await WriteAsync(Path.Combine(output, "source-format-audit.json"), formatAudit);
            await WriteAsync(Path.Combine(output, "eligibility-snapshot.json"), new { schemaVersion = "a99-v8a-holdout-eligibility-v1", status = "BLOCKED_ON_NEW_SOURCE_CORPUS", sourcePoolPrefixes = SourcePoolPrefixes.Select(x => x.Replace('\\', '/')).ToArray(), inventoryDocumentCount = inventory.Count, sourcePoolDocumentCount = sourcePool.Length, historicallyExposedDocumentIds = provenance.DocumentIds.Count, historicallyExposedSourceHashes = provenance.SourceHashes.Count, lineageDeniedCount = lineageDenied.Count, eligibleUniqueSourceCount = eligible.Length, requiredMinimum = TargetDocuments, sourceOnly = true, goldReadCount = 0, providerCalls = 0, reason = "All currently inventoried source-pool documents, including generated-docx, are present in historical provenance or share a historically exposed source hash. No new source corpus was selected or substituted." });
            await WriteAsync(Path.Combine(output, "manifest.json"), new { schemaVersion = "a99-v8a-holdout-manifest-v1", status = "BLOCKED_ON_NEW_SOURCE_CORPUS", holdoutSelected = false, generalizationClaim = false, providerCalls = 0, modelCalls = 0, goldReadCount = 0, predictionReadCount = 0, eligibleUniqueSourceCount = eligible.Length, requiredMinimum = TargetDocuments, sourcePoolDocumentCount = sourcePool.Length, sourcePoolPrefixes = SourcePoolPrefixes.Select(x => x.Replace('\\', '/')).ToArray(), historicallyExposedIntersection = 0 });
            await File.WriteAllTextAsync(Path.Combine(output, "report.md"), $"# A99 V8A — holdout eligibility blocked\n\nStatus: **BLOCKED_ON_NEW_SOURCE_CORPUS**.\n\nThe provenance-derived denylist covers the current 95-document source corpus (including source-hash lineage), so no document was selected by substitution. This is fail-closed: no Gold, model output, candidate labels, or provider call was used. V8A requires a genuinely new source corpus before request materialization.\n\n- Provider/model calls: **0/0**\n- Gold/prediction reads: **0**\n- Eligible unique sources: **{eligible.Length}**\n- Required minimum: **{TargetDocuments}**\n");
            return;
        }

        var parsed = eligible.Select(x => new ParsedCandidate(x, TryExtract(root, x), new FileInfo(Full(root, x.SourcePath)).Length)).Where(x => x.Parsed.Success && x.Parsed.Occurrences.Count >= 20).ToArray();
        Require(parsed.Length >= TargetDocuments, "V8A_INSUFFICIENT_READABLE_STRUCTURAL_SOURCES");
        var selected = SelectHoldout(parsed, TargetDocuments);
        var sourceFiles = selected.Select(x => x.Metadata).ToArray();
        var allOccurrences = selected.SelectMany(x => x.Parsed.Occurrences).ToArray();
        var candidateSets = selected.Select(x => new { x.Metadata.DocumentId, Candidates = GenerateCandidates(x.Parsed.Occurrences) }).ToArray();
        var requests = selected.Select((x, i) => BuildRequest(x.Metadata, x.Parsed.Occurrences, candidateSets[i].Candidates)).ToArray();
        var requestHashes = requests.Select(x => Sha256Text(JsonSerializer.Serialize(x, JsonOptions))).ToArray();
        var sourceFingerprint = new { inventorySha256 = Sha256File(inventoryPath), selectedSourceHashes = sourceFiles.Select(x => new { x.DocumentId, x.SourceSha256 }).ToArray(), occurrenceSha256 = Sha256Text(JsonSerializer.Serialize(allOccurrences, JsonOptions)), candidateSha256 = Sha256Text(JsonSerializer.Serialize(candidateSets, JsonOptions)) };

        await WriteAsync(Path.Combine(output, "source-format-audit.json"), formatAudit);
        await WriteAsync(Path.Combine(output, "holdout-selection.json"), new { schemaVersion = "a99-v8a-holdout-selection-v1", status = "FROZEN_SOURCE_ONLY_NEW_DOCUMENT_HOLDOUT", selectionSeed = SelectionSeed, sourcePoolPrefixes = SourcePoolPrefixes.Select(x => x.Replace('\\', '/')).ToArray(), selectionRule = "deterministic family/size-band stratification over readable unique source hashes; no semantic text/failure-shape ranking", requestedDocuments = TargetDocuments, selectedDocuments = sourceFiles.Select(x => new { x.DocumentId, x.DocumentGroupId, x.FamilyId, x.SourcePath, x.SourceSha256, byteLength = new FileInfo(Full(root, x.SourcePath)).Length, sizeBand = SizeBand(new FileInfo(Full(root, x.SourcePath)).Length) }).ToArray(), eligibleBeforeSelection = eligible.Length, readableStructuralSources = parsed.Length, historicallyExposedIntersection = sourceFiles.Count(x => provenance.DocumentIds.Contains(x.DocumentId) || provenance.SourceHashes.Contains(x.SourceSha256)) });
        await WriteAsync(Path.Combine(output, "source-fingerprint.json"), sourceFingerprint);
        await WriteAsync(Path.Combine(output, "occurrences.json"), new { schemaVersion = "a99-v8a-source-only-occurrences-v1", sourceOnly = true, occurrenceCount = allOccurrences.Length, documents = selected.Select(x => new { documentId = x.Metadata.DocumentId, occurrences = x.Parsed.Occurrences }).ToArray() });
        await WriteAsync(Path.Combine(output, "candidate-pairs.json"), new { schemaVersion = "a99-v8a-source-only-candidate-pairs-v1", sourceOnly = true, semanticLabelsUsed = false, knownPairsUsed = false, candidates = candidateSets });
        await WriteAsync(Path.Combine(output, "proposer-requests.json"), new { schemaVersion = "a99-v8a-proposer-requests-v1", status = "FROZEN_SOURCE_ONLY_REQUESTS_BEFORE_GOLD", requests = requests.Select((x, i) => new { request = x, requestHash = requestHashes[i] }).ToArray(), falsifierPolicy = "materialize only from frozen proposer positives; falsifier input excludes proposer output/rationale" });
        await WriteAsync(Path.Combine(output, "manifest.json"), new { schemaVersion = "a99-v8a-holdout-manifest-v1", status = "READY_FOR_V8_PROVIDER_EXECUTION", selectionSeed = SelectionSeed, documentCount = sourceFiles.Length, occurrenceCount = allOccurrences.Length, candidatePairCount = candidateSets.Sum(x => x.Candidates.Count), requestCount = requests.Length, sourceOnlyCandidateGeneration = true, providerCalls = 0, modelCalls = 0, goldReadCount = 0, v4hReadCount = 0, v5ReadCount = 0, v6ReadCount = 0, v7ReadCount = 0, predictionReadCount = 0, sourcePoolDocumentCount = sourcePool.Length, sourcePoolPrefixes = SourcePoolPrefixes.Select(x => x.Replace('\\', '/')).ToArray(), historicalExposedIntersection = 0, requestHashes, sourceFingerprint, primaryMetricsPreregistered = new[] { "acceptedEdgePrecision", "falseMerge" }, secondaryMetricsPreregistered = new[] { "falseSplit", "nodeConstraintAccuracy", "coverage", "acceptedEdgeRecall" }, asymmetricRisk = "falseMerge_cost_much_greater_than_falseSplit", generalizationClaim = false, holdoutSelected = true });
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), BuildReport(sourceFiles, allOccurrences.Length, candidateSets.Sum(x => x.Candidates.Count), provenance, lineageDenied.Count), new UTF8Encoding(false));
    }

    private static IReadOnlyList<DocRow> ReadInventory(JsonElement root) => root.GetProperty("documents").EnumerateArray().Select(x => new DocRow(x.GetProperty("documentId").GetString()!, x.GetProperty("documentGroupId").GetString()!, x.GetProperty("sourcePath").GetString()!, x.GetProperty("sourceSha256").GetString()!, x.GetProperty("familyId").GetString()!)).ToArray();
    private static bool IsSourcePoolRow(DocRow row) => SourcePoolPrefixes.Any(prefix => row.SourcePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static object AuditFormats(string root, IReadOnlyList<DocRow> rows)
    {
        var entries = rows.Select(row =>
        {
            var path = Full(root, row.SourcePath); var valid = false; var paragraphs = 0; var nonEmpty = 0; string? error = null;
            try
            {
                using var archive = ZipFile.OpenRead(path); var entry = archive.GetEntry("word/document.xml"); if (entry is null) throw new InvalidDataException("MISSING_WORD_DOCUMENT_XML");
                using var stream = entry.Open(); var xml = XDocument.Load(stream); paragraphs = xml.Descendants(W + "p").Count(); nonEmpty = xml.Descendants(W + "p").Count(p => string.Concat(p.Descendants(W + "t").Select(t => t.Value)).Trim().Length > 0); valid = true;
            }
            catch (Exception ex) { error = ex.GetType().Name; }
            return new { row.DocumentId, row.SourcePath, row.FamilyId, validDocx = valid, paragraphCount = paragraphs, nonEmptyParagraphCount = nonEmpty, error };
        }).ToArray();
        return new { schemaVersion = "a99-v8a-source-format-audit-v1", sourceOnly = true, format = "OOXML_DOCX_WORD_DOCUMENT_XML", total = entries.Length, validDocx = entries.Count(x => x.validDocx), invalidDocx = entries.Count(x => !x.validDocx), totalParagraphs = entries.Sum(x => x.paragraphCount), totalNonEmptyParagraphs = entries.Sum(x => x.nonEmptyParagraphCount), entries };
    }

    private static ProvenanceScan ScanProvenance(string root)
    {
        var roots = new[] { "artifacts/identity-benchmark", "eval/a99-closed-loop", "eval/harness-lift", "keys", "docs/accuracy", "tools/identity-benchmark-v4h-b", "tools/identity-benchmark-v4h-c", "tools/identity-benchmark-v5a", "tools/identity-benchmark-v5c", "tools/identity-benchmark-v6a", "tools/identity-benchmark-v6b", "tools/identity-benchmark-v6c", "tools/identity-benchmark-v6d-r", "tools/identity-benchmark-v7a", "tools/identity-benchmark-v7b", "tools/identity-benchmark-v7c", "tools/identity-benchmark-v7d" };
        var ids = new HashSet<string>(StringComparer.Ordinal); var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var files = new List<object>();
        foreach (var relativeRoot in roots)
        {
            var path = Full(root, relativeRoot); if (!Directory.Exists(path)) continue;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Where(IsProvenanceText))
            {
                var text = File.ReadAllText(file); var found = DocIdRegex.Matches(text).Select(x => x.Value).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                var foundHashes = SourceHashRegex.Matches(text).Select(x => x.Groups[1].Value.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray();
                if (found.Length == 0 && foundHashes.Length == 0) continue;
                foreach (var id in found) ids.Add(id); foreach (var hash in foundHashes) hashes.Add(hash);
                files.Add(new { path = Relative(root, file), documentIds = found, sha256Tokens = foundHashes.Length });
            }
        }
        return new ProvenanceScan(ids, hashes, files);
    }

    private static IReadOnlyList<SelectedDoc> SelectHoldout(IEnumerable<ParsedCandidate> source, int count)
    {
        var rows = source.OrderBy(x => x.Metadata.FamilyId, StringComparer.Ordinal).ThenBy(x => SizeBand(x.ByteLength), StringComparer.Ordinal).ThenBy(x => HashRank(x.Metadata.DocumentId), StringComparer.Ordinal).ToArray();
        var result = new List<ParsedCandidate>();
        foreach (var family in rows.Select(x => x.Metadata.FamilyId).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal)) result.Add(rows.First(x => x.Metadata.FamilyId == family));
        foreach (var row in rows) if (result.Count >= count) break; else if (!result.Any(x => x.Metadata.DocumentId == row.Metadata.DocumentId)) result.Add(row);
        return result.OrderBy(x => x.Metadata.DocumentId, StringComparer.Ordinal).Select(x => new SelectedDoc(x.Metadata, x.Parsed)).ToArray();
    }

    private static string HashRank(string id) => Sha256Text(SelectionSeed + "|" + id);
    private static ParsedSource TryExtract(string root, DocRow row)
    {
        try
        {
            using var archive = ZipFile.OpenRead(Full(root, row.SourcePath)); var entry = archive.GetEntry("word/document.xml"); if (entry is null) return ParsedSource.Failed("MISSING_WORD_DOCUMENT_XML");
            using var stream = entry.Open(); var xml = XDocument.Load(stream); var occurrences = new List<Occurrence>(); var order = 0;
            foreach (var paragraph in xml.Descendants(W + "p"))
            {
                var text = string.Concat(paragraph.Descendants(W + "t").Select(x => x.Value)).Trim(); if (text.Length == 0) continue;
                var pPr = paragraph.Element(W + "pPr"); var style = pPr?.Element(W + "pStyle")?.Attribute(W + "val")?.Value; var outline = pPr?.Element(W + "outlineLvl")?.Attribute(W + "val")?.Value; var numbered = pPr?.Element(W + "numPr") is not null; var headingStyle = style?.Contains("heading", StringComparison.OrdinalIgnoreCase) == true; if (!headingStyle && outline is null && !numbered && text.Length > 180) continue;
                order++; occurrences.Add(new Occurrence(row.DocumentId + ":B" + order.ToString("D6"), text, order, style ?? "", outline ?? "", numbered, "body", "PARAGRAPH_STYLE_OUTLINE_OR_NUMBERING"));
            }
            return new ParsedSource(true, occurrences, null);
        }
        catch (Exception ex) { return ParsedSource.Failed(ex.GetType().Name); }
    }

    private static IReadOnlyList<Candidate> GenerateCandidates(IReadOnlyList<Occurrence> occurrences)
    {
        var result = new List<Candidate>();
        for (var i = 0; i < occurrences.Count; i++) for (var j = i + 1; j < occurrences.Count; j++)
        {
            var left = occurrences[i]; var right = occurrences[j]; var distance = right.DocumentOrder - left.DocumentOrder; var reasons = new List<string>();
            if (distance == 1) reasons.Add("ADJACENT_ORDER");
            if (distance <= 8 && left.Style == right.Style && left.Style.Length > 0) reasons.Add("STYLE_COMPATIBLE");
            if (distance <= 128 && Normalize(left.Text) == Normalize(right.Text) && Normalize(left.Text).Length >= 3) reasons.Add("NORMALIZED_TEXT_AFFINITY");
            if (distance <= 2 && (!EndsSentence(left.Text) || right.Text.StartsWith("(", StringComparison.Ordinal))) reasons.Add("TEXT_CONTINUITY_SIGNAL");
            if (reasons.Count == 0) continue;
            var pairId = "P" + (result.Count + 1).ToString("D5"); result.Add(new Candidate(pairId, left.OccurrenceId, right.OccurrenceId, distance, reasons.OrderBy(x => x, StringComparer.Ordinal).ToArray()));
        }
        return result;
    }

    private static object BuildRequest(DocRow row, IReadOnlyList<Occurrence> occurrences, IReadOnlyList<Candidate> candidates) => new
    {
        documentId = row.DocumentId,
        task = "V8A_IDENTITY_PROPOSER_SOURCE_ONLY",
        occurrences,
        candidatePairs = candidates,
        forbidden = new[] { "Gold", "historical predictions", "pair labels", "parent", "ROOT", "level", "automatic collapse", "connected components" },
        defaultPolicy = "NO_CLAIM_KEEP_SPLIT",
        proofStatus = new[] { "SUFFICIENT", "INSUFFICIENT", "UNRESOLVED" },
        allowedRelations = new[] { "SAME_SEMANTIC_REPEAT", "CONTINUATION_OF" },
        autoCollapse = false,
    };

    private static string BuildReport(IReadOnlyList<DocRow> docs, int occurrences, int candidates, ProvenanceScan provenance, int lineageDenied) => $"# A99 V8A — new-document source-only holdout freeze\n\nStatus: **READY_FOR_V8_PROVIDER_EXECUTION**.\n\nSelected **{docs.Count}** documents from the source pool using deterministic seed `{SelectionSeed}`. The selection excludes every document ID/source hash found in the provenance scan and does not inspect semantic labels, known failure cases, pair outcomes, or model predictions.\n\n- Occurrences: **{occurrences}**\n- Source-only candidate pairs: **{candidates}**\n- Provenance files scanned: **{provenance.Files.Count}**\n- Source-hash lineage excluded: **{lineageDenied}**\n- Provider/model calls: **0/0**\n- Gold/prediction reads: **0**\n\nThe frozen requests contain only parser-owned source evidence and deterministic candidate reasons. V8 proposer and falsifier execution remains blocked until a separate authorization. Gold must be created only after predictions are frozen.\n";

    private static bool IsProvenanceText(string path) => new[] { ".json", ".md", ".cs", ".ps1", ".csv" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) && !path.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase) && !path.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase);
    private static bool EndsSentence(string text) => text.EndsWith(".", StringComparison.Ordinal) || text.EndsWith("!", StringComparison.Ordinal) || text.EndsWith("?", StringComparison.Ordinal);
    private static string Normalize(string text) => string.Join(' ', text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    private static string SizeBand(long bytes) => bytes < 250_000 ? "SMALL" : bytes < 1_000_000 ? "MEDIUM" : "LARGE";
    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('\\', Path.DirectorySeparatorChar));
    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256Text(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static async Task WriteAsync(string path, object value) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }

    private sealed record DocRow(string DocumentId, string DocumentGroupId, string SourcePath, string SourceSha256, string FamilyId);
    private sealed record Occurrence(string OccurrenceId, string Text, int DocumentOrder, string Style, string OutlineLevel, bool HasNumbering, string SourceContainerIdentity, string EvidenceReason);
    private sealed record Candidate(string CandidateId, string Left, string Right, int DocumentOrderDistance, string[] Reasons);
    private sealed record ParsedSource(bool Success, IReadOnlyList<Occurrence> Occurrences, string? Error) { public static ParsedSource Failed(string error) => new(false, Array.Empty<Occurrence>(), error); }
    private sealed record ParsedCandidate(DocRow Metadata, ParsedSource Parsed, long ByteLength);
    private sealed record SelectedDoc(DocRow Metadata, ParsedSource Parsed);
    private sealed record ProvenanceScan(IReadOnlySet<string> DocumentIds, IReadOnlySet<string> SourceHashes, IReadOnlyList<object> Files);
}
