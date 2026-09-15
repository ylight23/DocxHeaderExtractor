using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using UglyToad.PdfPig;

namespace IdentityBenchmarkV8A2Intake;

internal static class Program
{
    private const string InventoryRelative = "eval/a99-dataset/document-inventory.v1.json";
    private const string OutputRelative = "artifacts/identity-benchmark/v8a2/new-source-intake-v1";
    private const string SelectionSeed = "a99-v8a2-new-source-corpus-v1";
    private const int RequiredDocuments = 6;
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly Regex DocIdRegex = new(@"\bDOC-\d{4}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ShaRegex = new("\\\"(?:sourceSha256|sourceHash|sourceFingerprintSha256|normalizedContentFingerprint|normalizedTextSha256)\\\"\\s*:\\s*\\\"([a-fA-F0-9]{64})\\\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        var root = Path.GetFullPath(args.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal)) ?? Directory.GetCurrentDirectory());
        var sourceDir = GetOption(args, "--source-dir") ?? throw new ArgumentException("V8A2 requires --source-dir");
        sourceDir = Path.GetFullPath(sourceDir);
        var outputRelative = GetOption(args, "--output-relative") ?? OutputRelative;
        var requiredDocuments = ParsePositiveInt(GetOption(args, "--required-documents"), RequiredDocuments);
        var selectionSeed = GetOption(args, "--selection-seed") ?? SelectionSeed;
        try
        {
            await RunAsync(root, sourceDir, outputRelative, requiredDocuments, selectionSeed, args.Contains("--replace", StringComparer.Ordinal));
            var manifestPath = Path.Combine(Full(root, outputRelative), "manifest.json");
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
            Console.WriteLine($"V8A2_STATUS={manifest.RootElement.GetProperty("status").GetString()} PROVIDER_CALLS=0 GOLD_READ_COUNT=0");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"V8A2_ERROR={ex.Message}");
            return 2;
        }
    }

    private static async Task RunAsync(string root, string sourceDir, string outputRelative, int requiredDocuments, string selectionSeed, bool replace)
    {
        Require(Directory.Exists(sourceDir), "V8A2_SOURCE_DIRECTORY_NOT_FOUND");
        var output = Full(root, outputRelative);
        if (replace && Directory.Exists(output)) Directory.Delete(output, true);
        Require(!Directory.Exists(output) || !Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).Any(), "V8A2_OUTPUT_ALREADY_EXISTS_NO_OVERWRITE");
        Directory.CreateDirectory(output);

        var inventoryPath = Full(root, InventoryRelative);
        var historical = ReadHistorical(root, inventoryPath);
        var sourceFiles = Directory.EnumerateFiles(sourceDir, "*", SearchOption.TopDirectoryOnly)
            .Where(IsSupportedSource)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(path => InspectSource(path, historical))
            .ToArray();
        var downloadManifestPath = Path.Combine(sourceDir, "download-manifest.json");
        if (File.Exists(downloadManifestPath)) File.Copy(downloadManifestPath, Path.Combine(output, "download-manifest.json"), true);
        var sourceScanSha = Sha256Text(JsonSerializer.Serialize(sourceFiles.Select(x => new { x.Path, x.ByteSha256, x.NormalizedTextSha256 }), JsonOptions));
        var provenance = ScanProvenance(root);
        var eligible = sourceFiles.Where(x => x.Readable && !x.HardDuplicate && !x.NearDuplicate && x.NormalizedTextAvailable && x.OccurrenceCount >= 2).ToArray();
        var publicEntries = sourceFiles.Select(x => new { x.SourceId, x.Name, x.Path, x.Format, x.ByteSha256, x.NormalizedTextSha256, x.Readable, x.Error, x.ParagraphCount, x.NonEmptyParagraphCount, x.OccurrenceCount, x.SizeBand, x.Family, x.HardDuplicate, x.NearDuplicate, x.NearDuplicateMatch, x.NormalizedTextAvailable }).ToArray();

        await WriteAsync(Path.Combine(output, "intake-manifest.json"), new
        {
            schemaVersion = "a99-v8a2-intake-manifest-v1",
            status = eligible.Length >= requiredDocuments ? "INTAKE_ELIGIBLE_POOL_FOUND" : "BLOCKED_ON_NEW_SOURCE_CORPUS",
            sourceDirectory = sourceDir,
            sourceDirectoryReadOnly = true,
            sourceScanSha256 = sourceScanSha,
            sourceFileCount = sourceFiles.Length,
            supportedSourceCount = sourceFiles.Length,
            eligibleUniqueSourceCount = eligible.Length,
            requiredMinimum = requiredDocuments,
            providerCalls = 0,
            goldReadCount = 0,
            historicalProvenanceRead = true,
            semanticLabelsRead = false,
            predictionContentUsed = false,
            entries = publicEntries,
        });
        await WriteAsync(Path.Combine(output, "new-source-intake.json"), new
        {
            schemaVersion = "a99-v8a2-new-source-intake-v1",
            sourceDirectory = sourceDir,
            sourceScanSha256 = sourceScanSha,
            sourceOnly = true,
            candidates = publicEntries,
            providerCalls = 0,
            goldReadCount = 0,
        });
        await WriteAsync(Path.Combine(output, "source-format-audit.json"), new
        {
            schemaVersion = "a99-v8a2-source-format-audit-v1",
            sourceOnly = true,
            supportedSourceCount = publicEntries.Length,
            readableCount = publicEntries.Count(x => x.Readable),
            invalidCount = publicEntries.Count(x => !x.Readable),
            entries = publicEntries.Select(x => new { x.SourceId, x.Name, x.Format, x.Readable, x.Error, x.ParagraphCount, x.NonEmptyParagraphCount, x.OccurrenceCount }).ToArray(),
        });
        await WriteAsync(Path.Combine(output, "duplicate-analysis.json"), new
        {
            schemaVersion = "a99-v8a2-duplicate-analysis-v1",
            sourceOnly = true,
            hardDuplicateCount = publicEntries.Count(x => x.HardDuplicate),
            nearDuplicateCount = publicEntries.Count(x => x.NearDuplicate),
            uncertainLineageDefault = "EXCLUDE",
            entries = publicEntries.Select(x => new { x.SourceId, x.Name, x.ByteSha256, x.NormalizedTextSha256, x.HardDuplicate, x.NearDuplicate, x.NearDuplicateMatch }).ToArray(),
        });
        await WriteAsync(Path.Combine(output, "provenance-denylist.json"), new
        {
            schemaVersion = "a99-v8a2-provenance-denylist-v1",
            provenanceFileCount = provenance.Files.Count,
            provenanceScanSha256 = provenance.ScanSha256,
            historicalDocumentIds = provenance.DocumentIds.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            historicalSourceHashes = provenance.SourceHashes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
            historicalNormalizedTextHashes = historical.NormalizedTextHashes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
            sourceOnly = true,
        });

        if (eligible.Length < requiredDocuments)
        {
            await WriteAsync(Path.Combine(output, "eligibility-snapshot.json"), new
            {
                schemaVersion = "a99-v8a2-eligibility-snapshot-v1",
                status = "BLOCKED_ON_NEW_SOURCE_CORPUS",
                reason = "Fewer than six readable, unique, non-near-duplicate external sources survived the historical provenance firewall.",
                eligibleUniqueSourceCount = eligible.Length,
                requiredMinimum = requiredDocuments,
                noSubstitution = true,
                noBackupBypass = true,
                noGeneratedDocxBypass = true,
                goldReadCount = 0,
                providerCalls = 0,
            });
            await File.WriteAllTextAsync(Path.Combine(output, "report.md"), BuildBlockedReport(sourceDir, sourceFiles, eligible), new UTF8Encoding(false));
            await WriteAsync(Path.Combine(output, "manifest.json"), new
            {
                schemaVersion = "a99-v8a2-manifest-v1",
                status = "BLOCKED_ON_NEW_SOURCE_CORPUS",
                sourceDirectory = sourceDir,
                sourceScanSha256 = sourceScanSha,
                eligibleUniqueSourceCount = eligible.Length,
                requiredMinimum = requiredDocuments,
                providerCalls = 0,
                modelCalls = 0,
                goldReadCount = 0,
                holdoutSelected = false,
                generalizationClaim = false,
            });
            return;
        }

        var selected = SelectDeterministically(eligible, requiredDocuments, selectionSeed);
        var selectedSourceIds = selected.Select(x => x.SourceId).ToHashSet(StringComparer.Ordinal);
        var rejectedRecords = sourceFiles.Where(x => !selectedSourceIds.Contains(x.SourceId)).Select(x => new
        {
            x.SourceId,
            x.Name,
            x.Format,
            x.Family,
            x.ByteSha256,
            x.NormalizedTextSha256,
            x.Readable,
            x.Error,
            x.OccurrenceCount,
            x.HardDuplicate,
            x.NearDuplicate,
            x.NearDuplicateMatch,
            reason = !x.Readable ? "SOURCE_PARSE_INVALID" : x.HardDuplicate ? "HISTORICAL_BYTE_OR_NORMALIZED_DUPLICATE" : x.NearDuplicate ? "NEAR_DUPLICATE_OR_LINEAGE_REVIEW_REQUIRED" : "ELIGIBLE_POOL_NOT_SELECTED"
        }).ToArray();
        var selectedDir = Path.Combine(output, "source-corpus");
        Directory.CreateDirectory(selectedDir);
        var selectedRecords = new List<object>();
        var selectedDocs = new List<ParsedDocument>();
        foreach (var item in selected)
        {
            var destinationName = $"{item.SourceId}_{SanitizeFileName(Path.GetFileName(item.Path))}";
            var destination = Path.Combine(selectedDir, destinationName);
            File.Copy(item.Path, destination, false);
            Require(Sha256File(destination) == item.ByteSha256, "V8A2_COPY_HASH_MISMATCH");
            var parsed = ParseSource(destination, item);
            Require(parsed.Occurrences.Count >= 2, "V8A2_SELECTED_SOURCE_HAS_NO_OCCURRENCES");
            selectedDocs.Add(parsed);
            selectedRecords.Add(new { item.SourceId, item.Name, item.Path, repositoryPath = Relative(root, destination), item.ByteSha256, item.NormalizedTextSha256, item.Format, item.Family, item.SizeBand, item.OccurrenceCount, item.ParagraphCount, item.NonEmptyParagraphCount });
        }

        var candidateSets = selectedDocs.Select(d => new { d.SourceId, candidates = GenerateCandidates(d.Occurrences) }).ToArray();
        var proposerRequests = selectedDocs.Select(d => BuildRequest(d, candidateSets.Single(x => x.SourceId == d.SourceId).candidates, "V8A2_POSITIVE_PROOF_PROPOSER")).ToArray();
        var falsifierRequests = selectedDocs.Select(d => BuildRequest(d, candidateSets.Single(x => x.SourceId == d.SourceId).candidates, "V8A2_INDEPENDENT_DISTINCTNESS_FALSIFIER")).ToArray();
        var proposerRequestSetSha = Sha256Text(JsonSerializer.Serialize(proposerRequests, JsonOptions));
        var falsifierRequestSetSha = Sha256Text(JsonSerializer.Serialize(falsifierRequests, JsonOptions));
        await WriteAsync(Path.Combine(output, "eligibility-snapshot.json"), new
        {
            schemaVersion = "a99-v8a2-eligibility-snapshot-v1",
            status = "FROZEN_NEW_SOURCE_ELIGIBILITY",
            selectionSeed,
            selectionRule = "source-independent family/format/size-band stratification; no semantic difficulty or known failure-shape ranking",
            eligiblePoolCount = eligible.Length,
            selectedSourceIds = selected.Select(x => x.SourceId).ToArray(),
            selectedDocuments = selectedRecords,
            rejectedDocuments = rejectedRecords,
            familyDistribution = selected.GroupBy(x => x.Family, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            formatDistribution = selected.GroupBy(x => x.Format, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            noGoldOrPredictionInput = true,
        });
        await WriteAsync(Path.Combine(output, "occurrences.json"), new { schemaVersion = "a99-v8a2-occurrences-v1", sourceOnly = true, documents = selectedDocs });
        await WriteAsync(Path.Combine(output, "candidate-pairs.json"), new { schemaVersion = "a99-v8a2-candidates-v1", sourceOnly = true, semanticLabelsUsed = false, candidates = candidateSets });
        await WriteAsync(Path.Combine(output, "proposer-requests.json"), new { schemaVersion = "a99-v8a2-proposer-requests-v1", requestSetFrozenBeforeGold = true, requests = proposerRequests.Select(x => new { request = x, requestHash = Sha256Text(JsonSerializer.Serialize(x, JsonOptions)) }) });
        await WriteAsync(Path.Combine(output, "falsifier-requests.json"), new { schemaVersion = "a99-v8a2-falsifier-requests-v1", independentFromProposer = true, proposerRationaleExcluded = true, requests = falsifierRequests.Select(x => new { request = x, requestHash = Sha256Text(JsonSerializer.Serialize(x, JsonOptions)) }) });
        await WriteAsync(Path.Combine(output, "manifest.json"), new
        {
            schemaVersion = "a99-v8a2-manifest-v1",
            status = "READY_FOR_V8_PROVIDER_EXECUTION",
            sourceDirectory = sourceDir,
            sourceScanSha256 = sourceScanSha,
            selectionSeed,
            selectedSourceIds = selected.Select(x => x.SourceId).ToArray(),
            rejectedSourceIds = rejectedRecords.Select(x => x.SourceId).ToArray(),
            familyDistribution = selected.GroupBy(x => x.Family, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            formatDistribution = selected.GroupBy(x => x.Format, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            selectedDocumentCount = selectedDocs.Count,
            occurrenceCount = selectedDocs.Sum(x => x.Occurrences.Count),
            candidatePairCount = candidateSets.Sum(x => x.candidates.Count),
            proposerRequestCount = proposerRequests.Length,
            falsifierRequestCount = falsifierRequests.Length,
            proposerRequestSetSha256 = proposerRequestSetSha,
            falsifierRequestSetSha256 = falsifierRequestSetSha,
            providerCalls = 0,
            modelCalls = 0,
            goldReadCount = 0,
            v7PredictionReadCount = 0,
            holdoutSelected = true,
            generalizationClaim = false,
            autoCollapse = false,
            acceptedAuthority = "MODEL_PROPOSED_PROOF + CLEAR_FALSIFIER => ACCEPTED_FOR_V8_GRAPH_ONLY; otherwise KEEP_SPLIT",
        });
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), BuildReadyReport(sourceDir, selectedDocs, candidateSets.Sum(x => x.candidates.Count), proposerRequestSetSha, falsifierRequestSetSha, selectionSeed, requiredDocuments), new UTF8Encoding(false));
    }

    private static Historical ReadHistorical(string root, string inventoryPath)
    {
        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedTexts = new List<(string Source, string Text)>();
        var knownNameKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(inventoryPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(inventoryPath));
            foreach (var row in doc.RootElement.GetProperty("documents").EnumerateArray())
            {
                if (row.TryGetProperty("sourceSha256", out var s) && s.ValueKind == JsonValueKind.String) sourceHashes.Add(s.GetString()!);
                if (row.TryGetProperty("normalizedContentFingerprint", out var n) && n.ValueKind == JsonValueKind.String && n.GetString()!.Length == 64 && !n.GetString()!.Equals("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", StringComparison.OrdinalIgnoreCase)) normalized.Add(n.GetString()!);
                if (row.TryGetProperty("sourcePath", out var p) && p.ValueKind == JsonValueKind.String)
                {
                    knownNameKeys.Add(NormalizedName(Path.GetFileNameWithoutExtension(p.GetString()!)));
                }
            }
        }
        var metadataRoots = new[] { "artifacts/identity-benchmark", "eval/a99-closed-loop", "eval/harness-lift", "keys", "docs/accuracy", "tools/identity-benchmark-v4h-b", "tools/identity-benchmark-v4h-c", "tools/identity-benchmark-v5a", "tools/identity-benchmark-v5c", "tools/identity-benchmark-v6a", "tools/identity-benchmark-v6b", "tools/identity-benchmark-v6c", "tools/identity-benchmark-v6d-r", "tools/identity-benchmark-v7a", "tools/identity-benchmark-v7b", "tools/identity-benchmark-v7c", "tools/identity-benchmark-v7d" };
        foreach (var directory in metadataRoots.Select(relative => Full(root, relative)).Where(Directory.Exists))
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Where(IsSmallTextMetadata))
        foreach (Match match in ShaRegex.Matches(SafeRead(file))) normalized.Add(match.Groups[1].Value.ToLowerInvariant());
        return new Historical(normalized, sourceHashes) { NormalizedTexts = normalizedTexts, KnownNameKeys = knownNameKeys };
    }

    private static Provenance ScanProvenance(string root)
    {
        var compactPath = Full(root, "artifacts/identity-benchmark/v8/holdout/source-only-freeze-v1/provenance-denylist.json");
        if (File.Exists(compactPath))
        {
            using var compact = JsonDocument.Parse(File.ReadAllText(compactPath));
            var compactIds = compact.RootElement.GetProperty("historicallyExposedDocumentIds").EnumerateArray().Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal);
            var compactHashes = compact.RootElement.GetProperty("historicallyExposedSourceHashes").EnumerateArray().Select(x => x.GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var compactScanHash = compact.RootElement.TryGetProperty("provenanceScanSha256", out var scan) ? scan.GetString()! : Sha256Text(File.ReadAllText(compactPath));
            return new Provenance(compactIds, compactHashes, new List<string> { Relative(root, compactPath) }, compactScanHash);
        }
        var roots = new[] { "artifacts/identity-benchmark", "eval/a99-closed-loop", "eval/harness-lift", "keys", "docs/accuracy", "tools/identity-benchmark-v4h-b", "tools/identity-benchmark-v4h-c", "tools/identity-benchmark-v5a", "tools/identity-benchmark-v5c", "tools/identity-benchmark-v6a", "tools/identity-benchmark-v6b", "tools/identity-benchmark-v6c", "tools/identity-benchmark-v6d-r", "tools/identity-benchmark-v7a", "tools/identity-benchmark-v7b", "tools/identity-benchmark-v7c", "tools/identity-benchmark-v7d" };
        var ids = new HashSet<string>(StringComparer.Ordinal); var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var files = new List<string>();
        foreach (var relative in roots)
        {
            var dir = Full(root, relative); if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Where(IsSmallTextMetadata))
            {
                var text = SafeRead(file); var foundIds = DocIdRegex.Matches(text).Select(x => x.Value).Distinct(StringComparer.Ordinal).ToArray(); var foundHashes = ShaRegex.Matches(text).Select(x => x.Groups[1].Value.ToLowerInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (foundIds.Length == 0 && foundHashes.Length == 0) continue;
                foreach (var id in foundIds) ids.Add(id); foreach (var hash in foundHashes) hashes.Add(hash); files.Add(Relative(root, file));
            }
        }
        var scanHash = Sha256Text(JsonSerializer.Serialize(files.OrderBy(x => x, StringComparer.Ordinal).ToArray(), JsonOptions));
        return new Provenance(ids, hashes, files, scanHash);
    }

    private static SourceInspection InspectSource(string path, Historical historical)
    {
        var bytes = new FileInfo(path).Length; var byteSha = Sha256File(path); var format = Path.GetExtension(path).TrimStart('.').ToLowerInvariant(); ParsedDocument? parsed = null; string? error = null;
        try { parsed = ParseFile(path, byteSha); } catch (Exception ex) { error = ex.GetType().Name; }
        var normalized = parsed?.NormalizedText ?? ""; var normalizedSha = normalized.Length == 0 ? null : Sha256Text(normalized); var hard = historical.SourceHashes.Contains(byteSha) || (normalizedSha is not null && historical.NormalizedTextHashes.Contains(normalizedSha));
        var near = historical.KnownNameKeys.Contains(NormalizedName(Path.GetFileNameWithoutExtension(path))); string? nearMatch = near ? "historical-source-name" : null;
        if (normalized.Length > 80) { foreach (var candidate in historical.NormalizedTexts) { var similarity = Similarity(normalized, candidate.Text); if (similarity >= 0.985) { near = true; nearMatch = candidate.Source; break; } } }
        var name = Path.GetFileName(path); return new SourceInspection(SourceId(byteSha), name, path, format, byteSha, normalizedSha, parsed?.Success == true, error ?? parsed?.Error, parsed?.ParagraphCount ?? 0, parsed?.NonEmptyParagraphCount ?? 0, parsed?.Occurrences.Count ?? 0, SizeBand(bytes), Family(path), hard, near, nearMatch, normalized.Length > 0, parsed?.NormalizedText ?? "");
    }

    private static ParsedDocument ParseFile(string path, string byteSha)
    {
        return Path.GetExtension(path).Equals(".docx", StringComparison.OrdinalIgnoreCase) ? ParseDocx(path, SourceId(byteSha)) : ParsePdf(path, SourceId(byteSha));
    }

    private static ParsedDocument ParseSource(string copiedPath, SourceInspection inspection) => ParseFile(copiedPath, inspection.ByteSha256);

    private static ParsedDocument ParseDocx(string path, string sourceId)
    {
        using var archive = ZipFile.OpenRead(path); var entry = archive.GetEntry("word/document.xml") ?? throw new InvalidDataException("MISSING_WORD_DOCUMENT_XML"); using var stream = entry.Open(); var xml = XDocument.Load(stream); var all = xml.Descendants(W + "p").Select(p => new { p, text = string.Concat(p.Descendants(W + "t").Select(t => t.Value)).Trim() }).Where(x => x.text.Length > 0).ToArray(); var occurrences = new List<Occurrence>(); var order = 0;
        foreach (var item in all)
        {
            var p = item.p; var pPr = p.Element(W + "pPr"); var style = pPr?.Element(W + "pStyle")?.Attribute(W + "val")?.Value ?? ""; var outline = pPr?.Element(W + "outlineLvl")?.Attribute(W + "val")?.Value ?? ""; var numbered = pPr?.Element(W + "numPr") is not null; var bold = p.Descendants(W + "b").Any(); var shortText = item.text.Length <= 240; var structural = style.Contains("heading", StringComparison.OrdinalIgnoreCase) || style.Contains("title", StringComparison.OrdinalIgnoreCase) || outline.Length > 0 || numbered || (shortText && bold);
            if (!structural) continue; order++; occurrences.Add(new Occurrence($"{sourceId}:P{order:D5}", item.text, order, style, outline, numbered, p.Ancestors(W + "tbl").Count(), 0, "DOCX_PARAGRAPH_STYLE_OUTLINE_NUMBERING_OR_FORMAT"));
        }
        var normalized = Normalize(all.Select(x => x.text)); return new ParsedDocument(sourceId, true, "docx", all.Length, all.Length, occurrences, normalized, null, null);
    }

    private static ParsedDocument ParsePdf(string path, string sourceId)
    {
        using var pdf = PdfDocument.Open(path); var allLines = new List<(string Text, int Page)>();
        foreach (var page in pdf.GetPages())
        {
            var grouped = page.GetWords()
                .GroupBy(word => Math.Round(word.BoundingBox.Bottom, 1))
                .OrderByDescending(group => group.Key)
                .Select(group => string.Join(' ', group.OrderBy(word => word.BoundingBox.Left).Select(word => word.Text).Where(text => text.Length > 0).ToArray()))
                .Where(line => line.Trim().Length > 0);
            allLines.AddRange(grouped.Select(line => (line, page.Number)));
        }
        var nonEmpty = allLines.Where(x => x.Text.Length > 0).ToArray(); var occurrences = nonEmpty.Where(x => LooksStructuralPdfLine(x.Text)).Select((item, i) => new Occurrence($"{sourceId}:L{i + 1:D5}", item.Text, i + 1, "PDF_TEXT_LINE", "", false, 0, item.Page, "PDF_WORD_COORDINATE_LINE_STRUCTURAL_SIGNAL")).ToArray(); return new ParsedDocument(sourceId, true, "pdf", allLines.Count, nonEmpty.Length, occurrences, Normalize(nonEmpty.Select(x => x.Text)), null, null);
    }

    private static IReadOnlyList<Candidate> GenerateCandidates(IReadOnlyList<Occurrence> occurrences)
    {
        var result = new List<Candidate>(); var seen = new HashSet<string>(StringComparer.Ordinal); var normalizedSeen = new Dictionary<string, List<int>>(StringComparer.Ordinal); var normalizedTexts = occurrences.Select(x => Normalize(new[] { x.Text })).ToArray();
        void Add(int leftIndex, int rightIndex, IEnumerable<string> candidateReasons)
        {
            var left = occurrences[leftIndex]; var right = occurrences[rightIndex]; var key = left.OccurrenceId + "|" + right.OccurrenceId; var reasons = candidateReasons.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray(); if (reasons.Length == 0 || !seen.Add(key)) return; result.Add(new Candidate($"C{result.Count + 1:D5}", left.OccurrenceId, right.OccurrenceId, right.DocumentOrder - left.DocumentOrder, reasons));
        }
        for (var i = 0; i < occurrences.Count; i++)
        {
            if (i + 1 < occurrences.Count) Add(i, i + 1, new[] { "ADJACENT_ORDER" });
            if (i + 2 < occurrences.Count && (!EndsSentence(occurrences[i].Text) || occurrences[i + 1].Text.StartsWith("(", StringComparison.Ordinal))) Add(i, i + 2, new[] { "TEXT_CONTINUITY_SIGNAL" });
            if (occurrences[i].Style.Length > 0) for (var j = i + 1; j < occurrences.Count && j <= i + 8; j++) if (occurrences[i].Style == occurrences[j].Style) Add(i, j, new[] { "STYLE_COMPATIBLE" });
            var normalized = normalizedTexts[i]; if (normalized.Length >= 3 && normalizedSeen.TryGetValue(normalized, out var prior)) foreach (var priorIndex in prior.Where(index => i - index <= 128)) Add(priorIndex, i, new[] { "NORMALIZED_TEXT_AFFINITY" });
            if (normalized.Length >= 3) { if (!normalizedSeen.TryGetValue(normalized, out var indexes)) normalizedSeen[normalized] = indexes = new List<int>(); indexes.Add(i); }
        }
        return result;
    }

    private static object BuildRequest(ParsedDocument document, IReadOnlyList<Candidate> candidates, string task) => new
    {
        schemaVersion = "a99-v8-proof-carrying-identity-contract-v1",
        task,
        sourceId = document.SourceId,
        occurrences = document.Occurrences,
        candidatePairs = candidates,
        evidencePolicy = "parser/source-owned evidence is attention evidence, not merge authority",
        forbidden = new[] { "Gold", "historical predictions", "pair labels", "parent", "ROOT", "level", "automatic collapse", "connected components" },
        proposerOutput = task.Contains("PROPOSER", StringComparison.Ordinal) ? new { positiveProofs = new[] { "SUFFICIENT", "INSUFFICIENT", "UNRESOLVED" }, allowedRelations = new[] { "SAME_SEMANTIC_REPEAT", "CONTINUATION_OF" }, omitted = "NO_CLAIM_KEEP_SPLIT" } : null,
        falsifierOutput = task.Contains("FALSIFIER", StringComparison.Ordinal) ? new { decisions = new[] { "NO_CREDIBLE_DISTINCTNESS", "DISTINCTNESS_FOUND", "UNRESOLVED" }, omitted = "UNRESOLVED_KEEP_SPLIT", proposerRationaleVisible = false } : null,
        autoCollapse = false,
    };

    private static IReadOnlyList<SourceInspection> SelectDeterministically(IReadOnlyList<SourceInspection> eligible, int count, string selectionSeed)
    {
        var ranked = eligible.OrderBy(x => x.Family, StringComparer.Ordinal).ThenBy(x => x.Format, StringComparer.Ordinal).ThenBy(x => x.SizeBand, StringComparer.Ordinal).ThenBy(x => Sha256Text(selectionSeed + "|" + x.SourceId), StringComparer.Ordinal).ToArray(); var selected = new List<SourceInspection>(); foreach (var family in ranked.Select(x => x.Family).Distinct(StringComparer.Ordinal)) { var row = ranked.First(x => x.Family == family); if (!selected.Any(x => x.SourceId == row.SourceId)) selected.Add(row); if (selected.Count == count) break; } foreach (var row in ranked) { if (selected.Count == count) break; if (selected.All(x => x.SourceId != row.SourceId)) selected.Add(row); } return selected.OrderBy(x => x.SourceId, StringComparer.Ordinal).ToArray();
    }

    private static double Similarity(string left, string right)
    {
        var a = Shingles(left); var b = Shingles(right); if (a.Count == 0 || b.Count == 0) return 0; return (double)a.Intersect(b, StringComparer.Ordinal).Count() / a.Union(b, StringComparer.Ordinal).Count();
    }
    private static HashSet<string> Shingles(string value) { var t = value.Split(' ', StringSplitOptions.RemoveEmptyEntries); var result = new HashSet<string>(StringComparer.Ordinal); for (var i = 0; i + 4 < t.Length; i++) result.Add(string.Join(' ', t.Skip(i).Take(5))); return result; }
    private static string Normalize(IEnumerable<string> values) => string.Join(' ', values.SelectMany(x => x.Normalize(NormalizationForm.FormKC).ToUpperInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
    private static bool IsSupportedSource(string path) => Path.GetExtension(path).Equals(".docx", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
    private static bool LooksStructuralPdfLine(string text)
    {
        var value = text.Trim(); if (value.Length == 0 || value.Length > 220) return false; var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries); var letters = value.Count(char.IsLetter); if (letters == 0) return false; var upper = value.Count(char.IsUpper); var upperRatio = (double)upper / letters; var numbered = Regex.IsMatch(value, @"^(?:\d+(?:\.\d+)*|[IVXLC]+)(?:[\.)\-:]|\s)", RegexOptions.CultureInvariant); var shortLabel = words.Length <= 8 && value.Length <= 80 && !EndsSentence(value) && !value.Any(c => c is ',' or ';'); return numbered || upperRatio >= 0.82 || shortLabel;
    }
    private static bool IsSmallTextMetadata(string path) => new[] { ".json", ".md", ".cs", ".ps1", ".csv" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) && new FileInfo(path).Length <= 20_000_000 && !path.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase) && !path.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase);
    private static string SafeRead(string path) { try { return File.ReadAllText(path); } catch { return ""; } }
    private static string Family(string path)
    {
        var name = Path.GetFileName(path); var separator = name.IndexOf("__", StringComparison.Ordinal);
        return separator > 0 ? name[..separator] : Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
    }
    private static string SizeBand(long bytes) => bytes < 250_000 ? "SMALL" : bytes < 2_000_000 ? "MEDIUM" : "LARGE";
    private static string SourceId(string sha) => "NEW-" + sha[..12].ToUpperInvariant();
    private static string NormalizedName(string value) => string.Concat(value.Normalize(NormalizationForm.FormKC).ToUpperInvariant().Where(char.IsLetterOrDigit));
    private static string SanitizeFileName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    private static bool EndsSentence(string text) => text.EndsWith(".", StringComparison.Ordinal) || text.EndsWith("!", StringComparison.Ordinal) || text.EndsWith("?", StringComparison.Ordinal);
    private static string GetOption(string[] args, string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
    private static int ParsePositiveInt(string? value, int fallback) => value is null ? fallback : int.TryParse(value, out var parsed) && parsed > 0 ? parsed : throw new ArgumentException("Expected a positive integer option.");
    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256Text(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static async Task WriteAsync(string path, object value) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
    private static void Require(bool ok, string error) { if (!ok) throw new InvalidDataException(error); }
    private static string BuildBlockedReport(string sourceDir, IReadOnlyList<SourceInspection> all, IReadOnlyList<SourceInspection> eligible) => $"# V8A2 — new source corpus intake\n\nStatus: **BLOCKED_ON_NEW_SOURCE_CORPUS**.\n\nSource directory: `{sourceDir}`\n\nThe read-only source scan found **{all.Count}** supported DOCX/PDF files; only **{eligible.Count}** survived readability, byte/source provenance, normalized-text, and near-duplicate checks. No substitution with generated-docx, backup, converted, or historical files was performed.\n\n- Provider/model calls: **0/0**\n- Gold/prediction reads: **0**\n- Holdout selection: **not performed**\n- Candidate/request materialization: **not performed**\n";
    private static string BuildReadyReport(string sourceDir, IReadOnlyList<ParsedDocument> docs, int candidateCount, string proposerRequestSetSha, string falsifierRequestSetSha, string selectionSeed, int requiredDocuments) => $"# V8A2 — new source corpus intake\n\nStatus: **READY_FOR_V8_PROVIDER_EXECUTION**.\n\nSource-only intake from `{sourceDir}` selected **{docs.Count}** documents using `{selectionSeed}`; configured minimum was **{requiredDocuments}**. No Gold, historical predictions, semantic labels, or provider calls were used. Proposer and independent falsifier request sets are frozen; acceptance is graph-only and is not automatic collapse.\n\n- Occurrences: **{docs.Sum(x => x.Occurrences.Count)}**\n- Candidate pairs: **{candidateCount}**\n- Proposer request-set SHA-256: `{proposerRequestSetSha}`\n- Falsifier request-set SHA-256: `{falsifierRequestSetSha}`\n- Provider/model calls: **0/0**\n- Gold/prediction reads: **0**\n";

    private sealed record Historical(HashSet<string> NormalizedTextHashes, HashSet<string> SourceHashes)
    {
        public IReadOnlyList<(string Source, string Text)> NormalizedTexts { get; init; } = Array.Empty<(string, string)>();
        public HashSet<string> KnownNameKeys { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }
    private sealed record Provenance(HashSet<string> DocumentIds, HashSet<string> SourceHashes, List<string> Files, string ScanSha256);
    private sealed record SourceInspection(string SourceId, string Name, string Path, string Format, string ByteSha256, string? NormalizedTextSha256, bool Readable, string? Error, int ParagraphCount, int NonEmptyParagraphCount, int OccurrenceCount, string SizeBand, string Family, bool HardDuplicate, bool NearDuplicate, string? NearDuplicateMatch, bool NormalizedTextAvailable, string NormalizedText);
    private sealed record ParsedDocument(string SourceId, bool Success, string Format, int ParagraphCount, int NonEmptyParagraphCount, IReadOnlyList<Occurrence> Occurrences, string NormalizedText, string? Error, string? SourcePath);
    private sealed record Occurrence(string OccurrenceId, string Text, int DocumentOrder, string Style, string OutlineLevel, bool HasNumbering, int ContainerDepth, int PageNumber, string EvidenceReason);
    private sealed record Candidate(string CandidateId, string Left, string Right, int DocumentOrderDistance, string[] Reasons);
}
