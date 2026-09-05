using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Eval.HarnessLift;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Cli;

/// <summary>
/// HL2 evaluation-only runner. It joins retained references to parser occurrences before doing any
/// scoring, freezes the measurement manifest before a provider call, and never changes production
/// extraction behavior or sends reference/gold data to the model.
/// </summary>
internal static class HarnessLiftOccurrenceJoinRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly Regex NumericPrefix = new("^(?<id>\\d{3})[_-]", RegexOptions.Compiled);
    private static readonly Regex KeyLine = new(
        "^\\s*(?:@(?<source>\\S+)|(?<ordinal>\\d+))\\s+(?<level>\\d+)(?:\\s+#\\s*(?<text>.*))?\\s*$",
        RegexOptions.Compiled);

    public static async Task<int> RunAsync(CommandLineOptions options, CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.HarnessLiftRoot ?? Directory.GetCurrentDirectory());
        var outputRoot = Path.Combine(repoRoot, "eval", "harness-lift");
        var packetRoot = Path.Combine(outputRoot, "review-packets-v2");
        Directory.CreateDirectory(outputRoot);
        Directory.CreateDirectory(packetRoot);

        var baseSha = Git(repoRoot, "rev-parse", "HEAD");
        var corpus = LoadCorpus(repoRoot);
        var references = LoadReferences(repoRoot);
        var sourceDocuments = LoadSources(repoRoot, corpus, cancellationToken);
        var byDocument = corpus.ToDictionary(item => item.DocumentId, StringComparer.Ordinal);
        var joins = BuildReferenceBridge(repoRoot, corpus, references, sourceDocuments);
        var expansion = BuildSourceStructuralExpansion(corpus, sourceDocuments, joins);
        joins.AddRange(expansion.Joins);

        await WriteJsonAsync(Path.Combine(outputRoot, "occurrence-identity-contract.v1.json"), BuildIdentityContract(baseSha), cancellationToken);
        await WriteJsonAsync(Path.Combine(outputRoot, "reference-occurrence-bridge.v1.json"), new
        {
            artifactKind = "harness_lift_reference_occurrence_bridge",
            schemaVersion = "2.0",
            baseRevision = baseSha,
            strategy = new[] { "EXACT_SOURCE_ID", "EXACT_SPAN", "EXACT_ORDINAL_TEXT", "UNIQUE_EXACT_TEXT" },
            forbidden = new[] { "FUZZY_TEXT", "FILENAME_SIMILARITY", "PDF_LINE_INDEX_AS_DOCX_ORDINAL", "SOURCE_TEXT_RECONSTRUCTION" },
            totals = JoinTotals(joins),
            occurrences = joins,
        }, cancellationToken);

        var coverage = BuildCoverage(references, expansion, joins);
        await WriteJsonAsync(Path.Combine(outputRoot, "reference-coverage.v2.json"), coverage, cancellationToken);
        await WriteJsonAsync(Path.Combine(outputRoot, "source-structural-reference-expansion.v1.json"), expansion.Artifact, cancellationToken);

        var previousGaps = ReadJsonArray(repoRoot, "eval/harness-lift/unknown-gap-manifest.v1.json", "gaps");
        var unknownGaps = BuildUnknownGaps(previousGaps, expansion, joins);
        await WriteJsonAsync(Path.Combine(outputRoot, "unknown-gap-manifest.v2.json"), unknownGaps, cancellationToken);

        var review = await BuildReviewQueueAsync(repoRoot, outputRoot, packetRoot, corpus, sourceDocuments, joins, cancellationToken);
        await WriteJsonAsync(Path.Combine(outputRoot, "review-priority.v1.json"), review.Priority, cancellationToken);
        await WriteJsonAsync(Path.Combine(outputRoot, "review-manifest.v1.json"), review.Manifest, cancellationToken);

        var trusted = SelectTrusted(corpus, repoRoot);
        var frozenManifest = BuildFrozenManifest(repoRoot, baseSha, corpus, references, joins, expansion, trusted, options);
        await WriteJsonAsync(Path.Combine(outputRoot, "current-measurement-manifest.v2.json"), frozenManifest, cancellationToken);

        var model = options.HarnessLiftRunModel
            ? await RunModelAsync(repoRoot, options, trusted, sourceDocuments, cancellationToken)
            : ModelRunResult.NotRun("--harness-run-model was not supplied");
        await WriteJsonAsync(Path.Combine(outputRoot, "model-occurrence-bridge.v1.json"), new
        {
            artifactKind = "harness_lift_model_occurrence_bridge",
            schemaVersion = "2.0",
            codeRevision = baseSha,
            reusePreviousRuns = "NO",
            modelCalls = model.ProviderCalls,
            traces = model.Traces,
            note = "sanitized occurrence traces only; prompts, completions, references, and gold are excluded",
        }, cancellationToken);
        await WriteJsonAsync(Path.Combine(outputRoot, "harness-lift-runs.v2.json"), new
        {
            artifactKind = "harness_lift_runs",
            schemaVersion = "2.0",
            codeRevision = baseSha,
            reusePreviousRuns = "NO",
            status = model.Status,
            provider = model.Provider,
            model = model.Model,
            repeats = model.Repeats,
            providerCalls = model.ProviderCalls,
            runs = model.Runs,
            errors = model.Errors,
        }, cancellationToken);

        var fields = BuildFieldMetrics(joins, model.Traces);
        var firstLoss = BuildFirstLoss(joins, model.Traces);
        var preModel = BuildPreModelCoverage(corpus, joins, model);
        await WriteJsonAsync(Path.Combine(outputRoot, "harness-lift-by-field.v1.json"), fields, cancellationToken);
        await WriteJsonAsync(Path.Combine(outputRoot, "first-loss-summary.v2.json"), firstLoss, cancellationToken);
        await WriteJsonAsync(Path.Combine(outputRoot, "pre-model-coverage.v2.json"), preModel, cancellationToken);
        await WriteJsonAsync(Path.Combine(outputRoot, "harness-lift-by-mode.v2.json"), BuildByDimension("mode", corpus, joins, model.Traces), cancellationToken);
        await WriteJsonAsync(Path.Combine(outputRoot, "harness-lift-by-family.v2.json"), BuildByDimension("family", corpus, joins, model.Traces), cancellationToken);
        await WriteJsonAsync(Path.Combine(outputRoot, "harness-lift-summary.v2.json"), BuildSummary(baseSha, corpus, references, joins, expansion, review, model, fields, firstLoss), cancellationToken);

        var finalDecision = BuildFinalDecision(baseSha, corpus, references, joins, expansion, review, model, fields, firstLoss);
        await WriteJsonAsync(Path.Combine(outputRoot, "final-decision.v2.json"), finalDecision, cancellationToken);
        await WriteReadmeAsync(repoRoot, finalDecision, cancellationToken);
        await WriteClosureDocAsync(repoRoot, finalDecision, cancellationToken);

        Console.WriteLine($"HL2 occurrence-join corpus={corpus.Count} references={references.Count} joins={joins.Count} trusted={trusted.Count} providerCalls={model.ProviderCalls} reviewPending=YES");
        return 0;
    }

    private static object BuildIdentityContract(string revision) => new
    {
        artifactKind = "harness_lift_occurrence_identity_contract",
        schemaVersion = "1.0",
        codeRevision = revision,
        allowedStrategies = new[]
        {
            new { name = "EXACT_SOURCE_ID", strength = 4, requirement = "current parser SourceId or StableId matches" },
            new { name = "EXACT_SPAN", strength = 3, requirement = "one current source occurrence owns the exact span" },
            new { name = "EXACT_ORDINAL_TEXT", strength = 2, requirement = "source ordinal and normalized full text both match" },
            new { name = "UNIQUE_EXACT_TEXT", strength = 1, requirement = "normalized full text is unique in the document" },
        },
        ambiguousIsUnknown = true,
        sourceShaMustMatch = true,
        fuzzyJoin = false,
        filenameJoin = false,
        pdfLineIndexAsDocxOrdinal = false,
        sourceTextIsNeverReconstructedFromStructure = true,
    };

    private static List<HarnessCorpusDocument> LoadCorpus(string repoRoot)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot, "eval/harness-lift/corpus-map.v1.json")));
        var documents = new List<HarnessCorpusDocument>();
        foreach (var node in json.RootElement.GetProperty("documents").EnumerateArray())
        {
            documents.Add(new HarnessCorpusDocument
            {
                DocumentId = String(node, "documentId") ?? throw new InvalidDataException("corpus documentId missing"),
                Path = String(node, "path") ?? throw new InvalidDataException("corpus path missing"),
                SourceSha256 = String(node, "sourceSha256") ?? throw new InvalidDataException("corpus SHA missing"),
                DocumentGroupId = String(node, "documentGroupId"),
                Split = String(node, "split"),
                SourceKind = String(node, "sourceKind") ?? "DOCX",
                FamilyId = String(node, "familyId"),
                FamilyAssignmentAuthority = String(node, "familyAssignmentAuthority"),
                DocumentMode = String(node, "documentMode"),
                DocumentModeStatus = String(node, "documentModeStatus") ?? "NOT_OBSERVABLE",
                JoinMethod = String(node, "joinMethod") ?? "UNKNOWN",
            });
        }
        return documents.OrderBy(item => item.DocumentId, StringComparer.Ordinal).ToList();
    }

    private static List<HarnessReferenceRecord> LoadReferences(string repoRoot)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot, "eval/harness-lift/reference-coverage.v1.json")));
        var references = new List<HarnessReferenceRecord>();
        foreach (var node in json.RootElement.GetProperty("references").EnumerateArray())
        {
            var authority = ParseAuthority(String(node, "authority"));
            var coverage = ParseCoverage(String(node, "coverage"));
            var metrics = node.TryGetProperty("supportedMetrics", out var metricNode)
                ? metricNode.EnumerateArray().Select(item => item.GetString() ?? "").Where(item => item.Length > 0).ToArray()
                : [];
            references.Add(new HarnessReferenceRecord
            {
                ReferenceId = String(node, "referenceId") ?? "reference",
                DocumentId = String(node, "documentId"),
                DocumentGroupId = String(node, "documentGroupId"),
                Authority = authority,
                SourcePath = String(node, "sourcePath") ?? "",
                SourceSha256 = String(node, "sourceSha256"),
                Coverage = coverage,
                SupportedMetrics = metrics,
                Provenance = String(node, "provenance") ?? "unknown",
                Notes = String(node, "notes") ?? "",
            });
        }
        return references.OrderBy(item => item.ReferenceId, StringComparer.Ordinal).ToList();
    }

    private static Dictionary<string, SourceDocument> LoadSources(
        string repoRoot,
        IReadOnlyList<HarnessCorpusDocument> corpus,
        CancellationToken cancellationToken)
    {
        var reader = new OpenXmlDocumentSource();
        var result = new Dictionary<string, SourceDocument>(StringComparer.Ordinal);
        foreach (var item in corpus)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(repoRoot, item.Path);
            if (!File.Exists(path)) continue;
            result[item.DocumentId] = reader.Read(path);
        }
        return result;
    }

    private static List<HarnessOccurrenceJoinResult> BuildReferenceBridge(
        string repoRoot,
        IReadOnlyList<HarnessCorpusDocument> corpus,
        IReadOnlyList<HarnessReferenceRecord> references,
        IReadOnlyDictionary<string, SourceDocument> sourceDocuments)
    {
        var byId = corpus.ToDictionary(item => item.DocumentId, StringComparer.Ordinal);
        var bySha = corpus.ToDictionary(item => item.SourceSha256, StringComparer.OrdinalIgnoreCase);
        var result = new List<HarnessOccurrenceJoinResult>();
        foreach (var reference in references)
        {
            var referencePath = Path.Combine(repoRoot, reference.SourcePath);
            if (!File.Exists(referencePath)) continue;
            HarnessCorpusDocument? document = reference.DocumentId is not null && byId.TryGetValue(reference.DocumentId, out var byDocument)
                ? byDocument
                : null;
            var relative = reference.SourcePath.Replace('\\', '/');
            if (relative.Contains("occurrence-bridge/", StringComparison.OrdinalIgnoreCase) ||
                relative.Contains("hierarchy/", StringComparison.OrdinalIgnoreCase))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(referencePath));
                var sourceSha = String(json.RootElement, "docxSha256") ?? String(json.RootElement, "sourceDocumentSha256");
                if (sourceSha is not null && bySha.TryGetValue(sourceSha, out var sourceDocument)) document = sourceDocument;
                if (document is null || !sourceDocuments.TryGetValue(document.DocumentId, out var source)) continue;
                if (relative.Contains("occurrence-bridge/", StringComparison.OrdinalIgnoreCase))
                    ParseOccurrenceBridge(reference, document, source, json.RootElement, result);
                else
                    ParseHierarchyReference(reference, document, source, json.RootElement, result);
                continue;
            }
            if (document is null || !sourceDocuments.TryGetValue(document.DocumentId, out var keySource)) continue;
            if (!reference.SourcePath.EndsWith(".key", StringComparison.OrdinalIgnoreCase)) continue;
            ParseKeyReference(reference, document, keySource, referencePath, result);
        }
        return result;
    }

    private static void ParseKeyReference(
        HarnessReferenceRecord reference,
        HarnessCorpusDocument document,
        SourceDocument source,
        string path,
        ICollection<HarnessOccurrenceJoinResult> destination)
    {
        var lineNumber = 0;
        foreach (var line in File.ReadLines(path))
        {
            lineNumber++;
            var match = KeyLine.Match(line);
            if (!match.Success) continue;
            var sourceId = match.Groups["source"].Success ? match.Groups["source"].Value : null;
            var ordinal = match.Groups["ordinal"].Success ? int.Parse(match.Groups["ordinal"].Value, CultureInfo.InvariantCulture) : (int?)null;
            var level = int.Parse(match.Groups["level"].Value, CultureInfo.InvariantCulture);
            var text = match.Groups["text"].Success ? match.Groups["text"].Value.Trim() : null;
            destination.Add(Join(document, reference, source, $"{reference.ReferenceId}#L{lineNumber}", new HarnessReferenceOccurrenceInput
            {
                ReferenceSourceId = sourceId,
                ReferenceOrdinal = ordinal,
                ReferenceText = text,
                ExpectedIsHeading = true,
                ExpectedRole = "heading",
                ExpectedLevel = level,
                SupportedFields = ["headingExistence", "role", "level"],
                SourceContract = "human-key-positive-occurrence",
            }));
        }
    }

    private static void ParseOccurrenceBridge(
        HarnessReferenceRecord reference,
        HarnessCorpusDocument document,
        SourceDocument source,
        JsonElement root,
        ICollection<HarnessOccurrenceJoinResult> destination)
    {
        if (!root.TryGetProperty("occurrences", out var occurrences)) return;
        var sourceIds = source.Paragraphs.Select(item => item.SourceId).ToHashSet(StringComparer.Ordinal);
        var index = 0;
        foreach (var node in occurrences.EnumerateArray())
        {
            index++;
            var sourceFactId = String(node, "sourceFactId");
            var sourceId = sourceFactId is not null && sourceIds.Contains(sourceFactId) ? sourceFactId : null;
            var expectedSpan = TrySpan(node, "span");
            var text = String(node, "goldText");
            destination.Add(Join(document, reference, source, $"{reference.ReferenceId}#O{index}", new HarnessReferenceOccurrenceInput
            {
                ReferenceSourceId = sourceId,
                ReferenceText = text,
                ReferenceSpan = expectedSpan,
                ExpectedSpan = expectedSpan,
                ExpectedIsHeading = true,
                ExpectedRole = "heading",
                SupportedFields = expectedSpan is null ? ["headingExistence", "role"] : ["headingExistence", "role", "span"],
                SourceContract = "source-occurrence-bridge;pdf-line-index-not-docx-ordinal",
            }));
        }
    }

    private static void ParseHierarchyReference(
        HarnessReferenceRecord reference,
        HarnessCorpusDocument document,
        SourceDocument source,
        JsonElement root,
        ICollection<HarnessOccurrenceJoinResult> destination)
    {
        if (!root.TryGetProperty("headings", out var headings)) return;
        var index = 0;
        foreach (var node in headings.EnumerateArray())
        {
            index++;
            var anchor = String(node, "sourceAnchor") ?? String(node, "headingId");
            var parent = String(node, "goldParentId");
            var level = Int(node, "goldLevel");
            destination.Add(Join(document, reference, source, $"{reference.ReferenceId}#H{index}", new HarnessReferenceOccurrenceInput
            {
                ReferenceSourceId = anchor,
                ExpectedIsHeading = true,
                ExpectedRole = "heading",
                ExpectedLevel = level,
                ExpectedParentOccurrenceId = parent?.Trim().TrimStart('@'),
                SupportedFields = ["headingExistence", "role", "level", "parent", "hierarchy"],
                SourceContract = "hierarchy-source-anchor",
            }));
        }
    }

    private static HarnessOccurrenceJoinResult Join(
        HarnessCorpusDocument document,
        HarnessReferenceRecord reference,
        SourceDocument source,
        string occurrenceReferenceId,
        HarnessReferenceOccurrenceInput input)
    {
        var sourceOccurrences = source.Paragraphs.Select(paragraph => new HarnessSourceOccurrence(
            paragraph.SourceId,
            paragraph.StableId,
            paragraph.SourceOrdinal,
            new HarnessSpan(0, paragraph.Text.Length),
            paragraph.Text)).ToArray();
        return HarnessOccurrenceIdentityJoiner.Join(input with
        {
            ReferenceId = occurrenceReferenceId,
            DocumentId = document.DocumentId,
            DocumentGroupId = document.DocumentGroupId,
            SourceSha256 = document.SourceSha256,
            ReferenceAuthority = reference.Authority,
        }, sourceOccurrences, document.SourceSha256);
    }

    private static SourceExpansionResult BuildSourceStructuralExpansion(
        IReadOnlyList<HarnessCorpusDocument> corpus,
        IReadOnlyDictionary<string, SourceDocument> sourceDocuments,
        IReadOnlyCollection<HarnessOccurrenceJoinResult> existingJoins)
    {
        var joins = new List<HarnessOccurrenceJoinResult>();
        var promoted = new List<object>();
        var promotedDocumentIds = new HashSet<string>(StringComparer.Ordinal);
        var rejected = new List<object>();
        var documentsAudited = 0;
        foreach (var document in corpus.Where(item => string.Equals(item.Split, "DEV", StringComparison.OrdinalIgnoreCase)))
        {
            if (!sourceDocuments.TryGetValue(document.DocumentId, out var source)) continue;
            documentsAudited++;
            var stack = new List<(int Level, string SourceId)>();
            foreach (var paragraph in source.Paragraphs.OrderBy(item => item.SourceOrdinal))
            {
                if (string.IsNullOrWhiteSpace(paragraph.Text))
                    continue;
                var outline = paragraph.Style.OutlineLevel;
                if (outline is null or < 0 or > 8)
                {
                    if (outline is not null) rejected.Add(new { documentId = document.DocumentId, sourceId = paragraph.SourceId, reason = "outline-level-out-of-range" });
                    continue;
                }
                while (stack.Count > 0 && stack[^1].Level >= outline.Value) stack.RemoveAt(stack.Count - 1);
                var parent = stack.Count == 0 ? null : stack[^1].SourceId;
                var input = new HarnessReferenceOccurrenceInput
                {
                    ReferenceId = $"source-structural:{document.DocumentId}:{paragraph.SourceId}",
                    DocumentId = document.DocumentId,
                    DocumentGroupId = document.DocumentGroupId,
                    SourceSha256 = document.SourceSha256,
                    ReferenceAuthority = HarnessReferenceAuthority.SourceStructuralReference,
                    ReferenceSourceId = paragraph.SourceId,
                    ExpectedLevel = outline.Value + 1,
                    ExpectedParentOccurrenceId = parent,
                    SupportedFields = parent is null ? ["level", "hierarchy"] : ["level", "parent", "hierarchy"],
                    SourceContract = "parser-owned-style-outline-level;positive-structure-only",
                };
                var joined = HarnessOccurrenceIdentityJoiner.Join(input,
                    source.Paragraphs.Select(item => new HarnessSourceOccurrence(item.SourceId, item.StableId, item.SourceOrdinal,
                        new HarnessSpan(0, item.Text.Length), item.Text)).ToArray(), document.SourceSha256);
                joins.Add(joined);
                promotedDocumentIds.Add(document.DocumentId);
                promoted.Add(new
                {
                    documentId = document.DocumentId,
                    documentGroupId = document.DocumentGroupId,
                    sourceId = paragraph.SourceId,
                    sourceOrdinal = paragraph.SourceOrdinal,
                    rawText = paragraph.Text,
                    span = new { start = 0, end = paragraph.Text.Length },
                    outlineLevel = outline.Value,
                    structuralLevel = outline.Value + 1,
                    parentSourceId = parent,
                    supportedFields = input.SupportedFields,
                });
                stack.Add((outline.Value, paragraph.SourceId));
            }
        }
        var artifact = new
        {
            artifactKind = "harness_lift_source_structural_reference_expansion",
            schemaVersion = "1.0",
            authority = "SOURCE_STRUCTURAL_REFERENCE",
            documentsAudited,
            documentsPromoted = promotedDocumentIds.Count,
            occurrencesPromoted = promoted.Count,
            fields = new[] { "level", "parent", "hierarchy" },
            occurrences = promoted,
            rejectedCandidates = rejected,
            excludedSplits = new[] { "GENERALIZATION_HOLDOUT", "RESERVED_UNLABELED" },
            noNegativeLabelsCreated = true,
        };
        return new SourceExpansionResult(joins, artifact, promoted.Count, documentsAudited);
    }

    private static object BuildCoverage(
        IReadOnlyList<HarnessReferenceRecord> references,
        SourceExpansionResult expansion,
        IReadOnlyCollection<HarnessOccurrenceJoinResult> joins) => new
    {
        artifactKind = "harness_lift_reference_coverage",
        schemaVersion = "2.0",
        priorReferenceCounts = references.GroupBy(item => item.Authority.ToString()).ToDictionary(group => group.Key, group => group.Count()),
        occurrenceJoinStatus = JoinTotals(joins),
        sourceStructuralExpansion = new { referencesAdded = expansion.Joins.Count, occurrences = expansion.PromotedOccurrences },
        references = references.Select(item => (object)new
        {
            referenceId = item.ReferenceId,
            documentId = item.DocumentId,
            documentGroupId = item.DocumentGroupId,
            authority = item.Authority,
            sourcePath = item.SourcePath,
            sourceSha256 = item.SourceSha256,
            coverage = item.Coverage,
            supportedMetrics = item.SupportedMetrics,
            provenance = item.Provenance,
            notes = item.Notes,
        }).Concat(expansion.Joins.GroupBy(item => item.DocumentId, StringComparer.Ordinal).Select(group => (object)new
        {
            referenceId = $"source-structural-expansion:{group.Key}",
            documentId = group.Key,
            documentGroupId = (string?)null,
            authority = HarnessReferenceAuthority.SourceStructuralReference,
            sourcePath = "eval/harness-lift/source-structural-reference-expansion.v1.json",
            sourceSha256 = (string?)null,
            coverage = HarnessCoverage.Partial,
            supportedMetrics = new[] { "level", "parent", "hierarchy" },
            provenance = "parser-owned style outline level",
            notes = "positive structural evidence only; not exhaustive existence/role gold",
        })).ToArray(),
    };

    private static object BuildUnknownGaps(
        IReadOnlyList<JsonElement> previousGaps,
        SourceExpansionResult expansion,
        IReadOnlyCollection<HarnessOccurrenceJoinResult> joins)
    {
        var remaining = new List<object>();
        var resolved = new List<object>();
        foreach (var gap in previousGaps)
        {
            var documentId = String(gap, "documentId") ?? "UNKNOWN";
            var missing = String(gap, "missing") ?? "UNKNOWN";
            var fixedByExpansion = expansion.Joins.Any(item => item.DocumentId == documentId &&
                (missing.Equals("Level", StringComparison.OrdinalIgnoreCase) ||
                 missing.Equals("Parent", StringComparison.OrdinalIgnoreCase) ||
                 missing.Equals("Hierarchy", StringComparison.OrdinalIgnoreCase)) &&
                item.SupportedFields.Any(field => field.Equals(missing, StringComparison.OrdinalIgnoreCase)));
            var fixedByJoin = joins.Any(item => item.DocumentId == documentId && item.JoinStatus is not (HarnessOccurrenceJoinStatus.NotFound or HarnessOccurrenceJoinStatus.Ambiguous or HarnessOccurrenceJoinStatus.NotSupported) &&
                item.SupportedFields.Any(field => field.Equals(missing, StringComparison.OrdinalIgnoreCase)));
            var row = new
            {
                documentId,
                documentGroupId = String(gap, "documentGroupId"),
                missing,
                reason = String(gap, "reason") ?? "prior gap",
                resolvedByReferenceJoin = fixedByJoin,
                resolvedBySourceStructural = fixedByExpansion,
            };
            if (fixedByJoin || fixedByExpansion) resolved.Add(row); else remaining.Add(row);
        }
        return new
        {
            artifactKind = "harness_lift_unknown_gap_manifest",
            schemaVersion = "2.0",
            gapsBefore = previousGaps.Count,
            resolved = resolved.Count,
            remaining = remaining.Count,
            resolvedRows = resolved,
            gaps = remaining,
            noHoldoutLabelsAdded = true,
        };
    }

    private static async Task<ReviewQueueResult> BuildReviewQueueAsync(
        string repoRoot,
        string outputRoot,
        string packetRoot,
        IReadOnlyList<HarnessCorpusDocument> corpus,
        IReadOnlyDictionary<string, SourceDocument> sourceDocuments,
        IReadOnlyCollection<HarnessOccurrenceJoinResult> joins,
        CancellationToken cancellationToken)
    {
        var packetRows = new List<object>();
        var packetPaths = new List<object>();
        foreach (var document in corpus)
        {
            if (!sourceDocuments.TryGetValue(document.DocumentId, out var source)) continue;
            var official = joins.Where(item => item.DocumentId == document.DocumentId && item.OfficialMetricEligible && item.JoinStatus is not (HarnessOccurrenceJoinStatus.NotFound or HarnessOccurrenceJoinStatus.Ambiguous or HarnessOccurrenceJoinStatus.NotSupported)).ToArray();
            var missingOfficial = official.Length == 0;
            var unresolved = joins.Count(item => item.DocumentId == document.DocumentId &&
                (item.JoinStatus is HarnessOccurrenceJoinStatus.NotFound or HarnessOccurrenceJoinStatus.Ambiguous));
            var tier = missingOfficial ? "P0" : unresolved > 0 ? "P1" : "P3";
            var packetPath = Path.Combine(packetRoot, $"{document.DocumentId}.v2.json");
            var packet = new
            {
                artifactKind = "harness_lift_source_first_blind_review_packet",
                schemaVersion = "2.0",
                reviewOnly = true,
                documentId = document.DocumentId,
                documentGroupId = document.DocumentGroupId,
                split = document.Split,
                familyId = document.FamilyId,
                sourceSha256 = document.SourceSha256,
                priorityTier = tier,
                authorityClass = "PARSER_SOURCE_FACTS_ONLY",
                prohibitedEvidence = new[] { "model prediction", "candidate ranking", "silver label", "existing expected answer", "final output" },
                allowedLabels = new[] { "REVIEWED_HEADING", "REVIEWED_NON_HEADING", "UNCERTAIN" },
                occurrences = source.Paragraphs.Select((paragraph, index) => new
                {
                    sourceId = paragraph.SourceId,
                    sourceOrdinal = paragraph.SourceOrdinal,
                    rawText = paragraph.Text,
                    fullSpan = new { start = 0, end = paragraph.Text.Length },
                    previousSourceId = index == 0 ? null : source.Paragraphs[index - 1].SourceId,
                    nextSourceId = index + 1 == source.Paragraphs.Count ? null : source.Paragraphs[index + 1].SourceId,
                    style = paragraph.Style,
                    numbering = paragraph.Numbering,
                    layout = paragraph.Layout,
                    inTableOfContents = paragraph.InTableOfContents,
                }).ToArray(),
            };
            await WriteJsonAsync(packetPath, packet, cancellationToken);
            var packetSha = Sha256(packetPath);
            packetRows.Add(new { documentId = document.DocumentId, documentGroupId = document.DocumentGroupId, split = document.Split, tier, missingOfficial, unresolvedOccurrences = unresolved, packet = Relative(repoRoot, packetPath), packetSha256 = packetSha, sourceSha256 = document.SourceSha256 });
            packetPaths.Add(new { documentId = document.DocumentId, packet = Relative(repoRoot, packetPath), packetSha256 = packetSha });
        }
        var imported = LoadReviewResults(repoRoot, sourceDocuments, packetRows);
        return new ReviewQueueResult(
            new { artifactKind = "harness_lift_review_priority", schemaVersion = "1.0", packets = packetRows, humanReviewPending = true, modelFieldsExcluded = true },
            new { artifactKind = "harness_lift_review_manifest", schemaVersion = "2.0", packetCount = packetRows.Count, packets = packetPaths, importedResults = imported, newHumanKeysImported = 0, humanReviewPending = true });
    }

    private static object LoadReviewResults(string repoRoot, IReadOnlyDictionary<string, SourceDocument> sourceDocuments, IReadOnlyList<object> packetRows)
    {
        var candidates = new[] { Path.Combine(repoRoot, "eval/harness-lift/review-results-v2"), Path.Combine(repoRoot, "eval/harness-lift/review-results") };
        var files = candidates.Where(Directory.Exists).SelectMany(path => Directory.EnumerateFiles(path, "*.json", SearchOption.AllDirectories)).ToArray();
        var valid = 0;
        var rejected = 0;
        foreach (var file in files)
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(file));
                if (!json.RootElement.TryGetProperty("decisions", out var decisions)) continue;
                foreach (var node in decisions.EnumerateArray())
                {
                    var documentId = String(node, "documentId");
                    var sourceId = String(node, "sourceId");
                    if (documentId is null || sourceId is null || !sourceDocuments.ContainsKey(documentId)) { rejected++; continue; }
                    valid++;
                }
            }
            catch (JsonException) { rejected++; }
        }
        return new { valid, rejected, sourceOnlyValidation = true, noGoldPromotion = true };
    }

    private static object BuildFrozenManifest(
        string repoRoot,
        string codeSha,
        IReadOnlyList<HarnessCorpusDocument> corpus,
        IReadOnlyList<HarnessReferenceRecord> references,
        IReadOnlyCollection<HarnessOccurrenceJoinResult> joins,
        SourceExpansionResult expansion,
        IReadOnlyList<HarnessCorpusDocument> trusted,
        CommandLineOptions options) => new
    {
        artifactKind = "harness_lift_current_measurement_manifest",
        schemaVersion = "2.0",
        codeSha,
        frozenBeforeCurrentOutput = true,
        corpusFiles = corpus.Count,
        documentIds = trusted.Select(item => item.DocumentId).ToArray(),
        documentGroupIds = trusted.Select(item => item.DocumentGroupId).Where(item => item is not null).ToArray(),
        sourceHashes = corpus.ToDictionary(item => item.DocumentId, item => item.SourceSha256),
        referenceHashes = references.ToDictionary(item => item.ReferenceId, item => item.SourceSha256),
        referenceAuthorities = references.GroupBy(item => item.Authority.ToString()).ToDictionary(group => group.Key, group => group.Count()),
        occurrenceJoinTotals = JoinTotals(joins),
        sourceStructuralExpansion = new { documents = expansion.DocumentsAudited, occurrences = expansion.PromotedOccurrences },
        providerProfile = options.HarnessLiftRunModel ? "CURRENT_PROFILE" : "NOT_RUN",
        provider = options.HarnessLiftRunModel ? "OpenRouter" : "NONE",
        model = options.HarnessLiftRunModel ? options.Provider.Remote.Model : null,
        temperature = 0,
        concurrency = 1,
        maxTokens = options.Provider.Remote.MaxOutputTokens,
        repeats = options.HarnessLiftRunModel ? 3 : 0,
        selectionRule = "exact trusted documentIds from current-measurement-manifest.v1; no reuse of prior model outputs",
        trueBlindAvailable = false,
    };

    private static IReadOnlyList<HarnessCorpusDocument> SelectTrusted(IReadOnlyList<HarnessCorpusDocument> corpus, string repoRoot)
    {
        var prior = Path.Combine(repoRoot, "eval/harness-lift/current-measurement-manifest.v1.json");
        if (File.Exists(prior))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(prior));
            if (json.RootElement.TryGetProperty("documentIds", out var ids))
            {
                var selected = ids.EnumerateArray().Select(item => item.GetString()).Where(item => item is not null)
                    .Select(id => corpus.FirstOrDefault(item => item.DocumentId == id)).Where(item => item is not null).Cast<HarnessCorpusDocument>().ToArray();
                if (selected.Length > 0) return selected;
            }
        }
        return corpus.Where(item => item.Split == "DEV").Take(3).ToArray();
    }

    private static async Task<ModelRunResult> RunModelAsync(
        string repoRoot,
        CommandLineOptions options,
        IReadOnlyList<HarnessCorpusDocument> trusted,
        IReadOnlyDictionary<string, SourceDocument> sourceDocuments,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")))
            return ModelRunResult.NotRun("OPENROUTER_API_KEY unavailable; provider was not called");

        options.Provider.Backend = InferenceBackend.OpenRouter;
        options.Provider.Remote = RemoteInferenceOptions.FromEnvironment("openrouter");
        var allTraces = new List<HarnessModelOccurrenceTrace>();
        var runs = new List<object>();
        var errors = new List<object>();
        var providerCalls = 0;
        for (var repeat = 1; repeat <= 3; repeat++)
        {
            foreach (var document in trusted.OrderBy(item => item.DocumentId, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(repoRoot, document.Path);
                Console.Error.WriteLine($"  HL2 model repeat={repeat} {document.DocumentId}");
                try
                {
                    var pipelineOptions = new PipelineOptions();
                    using var pipeline = new AuthorityExtractionPipeline(pipelineOptions, new HeaderClassifierFactory(options.Provider));
                    var execution = await pipeline.RunDocumentExecutionAsync(path, null, cancellationToken);
                    var traces = BuildModelTraces(document, sourceDocuments[document.DocumentId], execution, repeat);
                    allTraces.AddRange(traces);
                    var calls = execution.Result.Provenance.ProviderCalls;
                    providerCalls += calls;
                    runs.Add(new { runId = $"repeat-{repeat}-{document.DocumentId}", repeat, documentId = document.DocumentId, providerCalls = calls, traceCount = traces.Count, status = "COMPLETED" });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add(new { repeat, documentId = document.DocumentId, type = ex.GetType().Name, message = ex.Message });
                    runs.Add(new { runId = $"repeat-{repeat}-{document.DocumentId}", repeat, documentId = document.DocumentId, providerCalls = 0, traceCount = 0, status = "FAILED" });
                }
            }
        }
        return new ModelRunResult("MEASURED_ON_TRUSTED_SUBSET", "OpenRouter", options.Provider.Remote.Model, 3, providerCalls, runs, errors, allTraces);
    }

    private static IReadOnlyList<HarnessModelOccurrenceTrace> BuildModelTraces(
        HarnessCorpusDocument document,
        SourceDocument source,
        AuthorityPipelineExecutionResult execution,
        int repeat)
    {
        var audit = execution.CompatibilityOutline.RouteAudit;
        var constructed = (audit?.CandidateBlocks ?? []).Select(item => item.Id)
            .Concat(audit?.SelectedCandidateBlocks.Select(item => item.Id) ?? [])
            .Concat(audit?.BlockDecisions.Select(item => item.Id) ?? [])
            .ToHashSet(StringComparer.Ordinal);
        var selected = (audit?.SelectedCandidateBlocks ?? []).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var decisionById = (audit?.BlockDecisions ?? []).GroupBy(item => item.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var validationById = (audit?.CandidateStageTraces ?? []).GroupBy(item => item.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var hierarchyById = (audit?.HierarchyProposals ?? []).GroupBy(item => item.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var factsById = (audit?.HierarchyFacts ?? []).GroupBy(item => item.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var elements = execution.Result.Structure.Elements;
        var finalBySource = elements.SelectMany(element => element.Sources.Select(sourceRef => (sourceRef.SourceId, Element: element)))
            .GroupBy(item => item.SourceId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().Element, StringComparer.Ordinal);
        var compatibilityHeadings = execution.CompatibilityOutline.Headings;
        var modelCalled = audit?.RawAnalystResponses.Count > 0;
        return source.Paragraphs.Select(paragraph =>
        {
            decisionById.TryGetValue(paragraph.SourceId, out var decision);
            validationById.TryGetValue(paragraph.SourceId, out var validation);
            hierarchyById.TryGetValue(paragraph.SourceId, out var hierarchy);
            factsById.TryGetValue(paragraph.SourceId, out var facts);
            finalBySource.TryGetValue(paragraph.SourceId, out var element);
            var finalParent = element?.ParentId is null ? null : elements.FirstOrDefault(item => item.Id == element.ParentId)?.Sources.FirstOrDefault()?.SourceId ?? element.ParentId;
            var compatibilityHeading = compatibilityHeadings.FirstOrDefault(heading =>
                string.Equals(heading.SourceId, paragraph.SourceId, StringComparison.Ordinal) ||
                string.Equals(heading.StableId, paragraph.StableId, StringComparison.Ordinal));
            if (compatibilityHeading is null)
                compatibilityHeading = compatibilityHeadings.FirstOrDefault(heading => heading.Index == paragraph.SourceOrdinal);
            var finalIncluded = element is not null || compatibilityHeading is not null;
            return new HarnessModelOccurrenceTrace
            {
                RunId = $"repeat-{repeat}-{document.DocumentId}",
                Repeat = repeat,
                DocumentId = document.DocumentId,
                DocumentGroupId = document.DocumentGroupId,
                SourceId = paragraph.SourceId,
                SourceOrdinal = paragraph.SourceOrdinal,
                SourceSpan = new HarnessSpan(0, paragraph.Text.Length),
                CandidateConstructed = constructed.Contains(paragraph.SourceId),
                CandidateSelected = selected.Contains(paragraph.SourceId),
                ModelCalled = modelCalled,
                ModelExposed = modelCalled == true && selected.Contains(paragraph.SourceId),
                ModelRole = decision?.Role,
                ModelLevel = null,
                ModelParent = hierarchy?.ProposedParentId,
                ModelSpan = null,
                ValidationStatus = validation?.ValidationStatus,
                ValidationReason = validation?.Reason,
                AfterMarkerRole = null,
                AfterMarkerLevel = facts?.ResolvedLevel,
                AfterMarkerParent = facts?.MarkerPrefixParentCandidate,
                AfterStructuralRole = null,
                AfterStructuralLevel = element?.Level ?? compatibilityHeading?.Level,
                AfterStructuralParent = finalParent,
                FinalIncluded = finalIncluded,
                FinalRole = element?.Role.ToString() ?? element?.Type.ToString() ?? (compatibilityHeading is null ? null : "heading"),
                FinalLevel = element?.Level ?? compatibilityHeading?.Level,
                FinalParent = finalParent,
                FinalSpan = element?.Sources.FirstOrDefault() is { } sourceRef
                    ? new HarnessSpan(sourceRef.Span.Start, sourceRef.Span.End)
                    : compatibilityHeading?.HeadingSpan is { } headingSpan
                        ? new HarnessSpan(headingSpan.Start, headingSpan.End)
                        : null,
            };
        }).ToArray();
    }

    private static object BuildFieldMetrics(
        IReadOnlyCollection<HarnessOccurrenceJoinResult> joins,
        IReadOnlyCollection<HarnessModelOccurrenceTrace> traces)
    {
        var fields = new[] { "role", "level", "parent", "span" };
        var metrics = fields.ToDictionary(field => field, field => BuildFieldMetric(field, joins, traces), StringComparer.Ordinal);
        return new { artifactKind = "harness_lift_by_field", schemaVersion = "1.0", fields = metrics };
    }

    private static object BuildFieldMetric(string field, IReadOnlyCollection<HarnessOccurrenceJoinResult> joins, IReadOnlyCollection<HarnessModelOccurrenceTrace> traces)
    {
        var expected = joins.Where(item => item.OfficialMetricEligible && item.ResolvedSourceId is not null && item.SupportedFields.Any(value => value.Equals(field == "role" ? "role" : field, StringComparison.OrdinalIgnoreCase)) && HasExpected(field, item))
            .GroupBy(item => $"{item.DocumentId}|{item.ResolvedSourceId}|{field}", StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.ReferenceAuthority switch
            {
                HarnessReferenceAuthority.HumanGold => 3,
                HarnessReferenceAuthority.HumanKey => 2,
                _ => 1,
            }).First()).ToArray();
        var observations = expected.SelectMany(item => traces.Where(trace => trace.DocumentId == item.DocumentId && trace.SourceId == item.ResolvedSourceId)
            .Select(trace => new
            {
                trace,
                expected = ExpectedValue(field, item),
                model = ModelValue(field, trace),
                final = FinalValue(field, trace),
            })).ToArray();
        var modelExposed = observations.Where(item => item.trace.ModelExposed && item.model is not null).ToArray();
        var finalObserved = observations.Where(item => item.trace.FinalIncluded && item.final is not null).ToArray();
        var modelCorrect = modelExposed.Count(item => EqualValue(field, item.model, item.expected));
        var finalCorrect = finalObserved.Count(item => EqualValue(field, item.final, item.expected));
        var modelErrors = modelExposed.Length - modelCorrect;
        var finalErrors = finalObserved.Length - finalCorrect;
        return new
        {
            field,
            referenceOccurrencePopulation = expected.Length,
            runObservationCount = observations.Length,
            modelExposed = modelExposed.Length,
            modelCorrect,
            modelErrors,
            modelAccuracy = modelExposed.Length == 0 ? (double?)null : (double)modelCorrect / modelExposed.Length,
            finalObserved = finalObserved.Length,
            finalCorrect,
            finalErrors,
            finalAccuracy = finalObserved.Length == 0 ? (double?)null : (double)finalCorrect / finalObserved.Length,
            harnessLift = modelExposed.Length == 0 || finalObserved.Length == 0 ? null : (double?)(finalCorrect - modelCorrect) / expected.Length,
            status = expected.Length == 0 ? "NOT_MEASURED" : traces.Count == 0 ? "NOT_MEASURED_NO_MODEL_RUN" : "MEASURED_POSITIVE_OCCURRENCES_ONLY",
            repeated = RepeatStats(field, expected, traces),
            recovery = new
            {
                modelErrorsTotal = modelErrors,
                correctedByMarker = CountCorrected(field, observations, stage: "marker"),
                correctedByStructural = CountCorrected(field, observations, stage: "structural"),
                rejectedByValidator = observations.Count(item => item.trace.ValidationStatus is "rejected" or "invalid"),
                introducedByDeterministicStage = observations.Count(item => EqualValue(field, item.model, item.expected) && !EqualValue(field, item.final, item.expected)),
                modelErrorsSurvivedFinal = observations.Count(item => item.trace.ModelExposed && !EqualValue(field, item.model, item.expected) && !EqualValue(field, item.final, item.expected)),
            },
        };
    }

    private static object RepeatStats(string field, IReadOnlyList<HarnessOccurrenceJoinResult> expected, IReadOnlyCollection<HarnessModelOccurrenceTrace> traces)
    {
        var values = new List<double>();
        foreach (var repeat in traces.Select(item => item.Repeat).Distinct().OrderBy(item => item))
        {
            var rows = expected.SelectMany(item => traces.Where(trace => trace.Repeat == repeat && trace.DocumentId == item.DocumentId && trace.SourceId == item.ResolvedSourceId && trace.ModelExposed)
                .Select(trace => EqualValue(field, ModelValue(field, trace), ExpectedValue(field, item)) ? 1.0 : 0.0)).ToArray();
            if (rows.Length > 0) values.Add(rows.Average());
        }
        return HarnessLiftAccounting.Summarize(values);
    }

    private static int CountCorrected(string field, IEnumerable<dynamic> observations, string stage)
    {
        var count = 0;
        foreach (var item in observations)
        {
            var before = stage == "marker" ? item.trace.AfterMarkerLevel : item.trace.AfterStructuralLevel;
            var after = stage == "marker" ? item.trace.AfterStructuralLevel : item.trace.FinalLevel;
            if (field == "level" && before is not null && after is not null && !Equals(before, after) && EqualValue(field, after, item.expected)) count++;
        }
        return count;
    }

    private static object BuildFirstLoss(IReadOnlyCollection<HarnessOccurrenceJoinResult> joins, IReadOnlyCollection<HarnessModelOccurrenceTrace> traces)
    {
        var expected = joins.Where(item => item.OfficialMetricEligible && item.ResolvedSourceId is not null && item.ExpectedRole is not null)
            .GroupBy(item => $"{item.DocumentId}|{item.ResolvedSourceId}", StringComparer.Ordinal).Select(group => group.First()).ToArray();
        var rows = expected.SelectMany(item => traces.Where(trace => trace.DocumentId == item.DocumentId && trace.SourceId == item.ResolvedSourceId)
            .Select(trace => HarnessLiftAccounting.ChooseFirstLoss(
                sourceSeen: trace.SourceId is not null,
                candidateSelected: trace.CandidateSelected == true,
                modelCalled: trace.ModelCalled,
                modelCorrect: EqualValue("role", trace.ModelRole, item.ExpectedRole),
                finalCorrect: EqualValue("role", trace.FinalRole, item.ExpectedRole),
                validatorRejected: trace.ValidationStatus is "rejected" or "invalid",
                markerChanged: trace.AfterMarkerRole is not null,
                structuralChanged: trace.AfterStructuralRole is not null))).ToArray();
        var byStage = rows.GroupBy(item => item.ToString()).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        return new { artifactKind = "harness_lift_first_loss_summary", schemaVersion = "2.0", referencePositiveRows = expected.Length, observedRows = rows.Length, byFirstLoss = byStage, unknown = rows.Length == 0 ? "NO_OCCURRENCE_LEVEL_MODEL_TRACE" : null };
    }

    private static object BuildPreModelCoverage(IReadOnlyList<HarnessCorpusDocument> corpus, IReadOnlyCollection<HarnessOccurrenceJoinResult> joins, ModelRunResult model) => new
    {
        artifactKind = "harness_lift_pre_model_coverage",
        schemaVersion = "2.0",
        sourceRecall = new { status = "NOT_MEASURED", reason = "retained references are positive-only; exhaustive negatives are absent", positiveJoinedOccurrences = joins.Count(item => item.ResolvedSourceId is not null && item.OfficialMetricEligible) },
        candidateConstruction = new { status = model.Traces.Count == 0 ? "NOT_MEASURED" : "POSITIVE_OCCURRENCE_TRACE_ONLY", exhaustiveRecall = false },
        candidateSelection = new { status = model.Traces.Count == 0 ? "NOT_MEASURED" : "POSITIVE_OCCURRENCE_TRACE_ONLY", exhaustiveRecall = false },
        modelExposure = new { status = model.Traces.Count == 0 ? "NOT_MEASURED" : "POSITIVE_OCCURRENCE_TRACE_ONLY", exhaustiveRecall = false },
        corpusDocuments = corpus.Count,
        noNewHoldoutLabels = true,
    };

    private static object BuildByDimension(string dimension, IReadOnlyList<HarnessCorpusDocument> corpus, IReadOnlyCollection<HarnessOccurrenceJoinResult> joins, IReadOnlyCollection<HarnessModelOccurrenceTrace> traces) => new
    {
        artifactKind = $"harness_lift_by_{dimension}",
        schemaVersion = "2.0",
        groups = corpus.GroupBy(item => dimension == "mode" ? item.DocumentMode ?? "NOT_OBSERVABLE" : item.FamilyId ?? "UNKNOWN", StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new
            {
                key = group.Key,
                documents = group.Count(),
                joinedOfficialOccurrences = joins.Count(item => group.Any(doc => doc.DocumentId == item.DocumentId) && item.OfficialMetricEligible && item.ResolvedSourceId is not null),
                modelTraceRows = traces.Count(item => group.Any(doc => doc.DocumentId == item.DocumentId)),
                status = traces.Count == 0 ? "NOT_MEASURED" : "MEASURED_WITH_FIELD_SEPARATION",
            }).ToArray(),
    };

    private static object BuildSummary(string codeSha, IReadOnlyList<HarnessCorpusDocument> corpus, IReadOnlyList<HarnessReferenceRecord> references, IReadOnlyCollection<HarnessOccurrenceJoinResult> joins, SourceExpansionResult expansion, ReviewQueueResult review, ModelRunResult model, object fields, object firstLoss) => new
    {
        artifactKind = "harness_lift_summary",
        schemaVersion = "2.0",
        codeSha,
        corpus = new { files = corpus.Count, joinedA99Files = corpus.Count(item => item.JoinMethod == "EXACT_SOURCE_SHA256") },
        references = new { records = references.Count, occurrenceInputs = joins.Count, joins = JoinTotals(joins) },
        trustedMeasurement = new { documentIds = new[] { "DOC-0066", "DOC-0082", "DOC-0205" }, modelTraceReuse = "NO", providerCalls = model.ProviderCalls, repeats = model.Repeats },
        fields,
        firstLoss,
        sourceStructuralExpansion = new { occurrences = expansion.PromotedOccurrences, noNewNegativeLabels = true },
        review = review.Manifest,
        observedPostModelHarnessLift = model.Traces.Count == 0 ? "NOT_MEASURED" : "MEASURED_POSITIVE_OCCURRENCES_ONLY",
        accuracyClaim = "NOT_MEASURED_WITHOUT_EXHAUSTIVE_REVIEWED_NEGATIVES_AND_BLIND_HOLDOUT",
        providerCalls = model.ProviderCalls,
    };

    private static object BuildFinalDecision(string codeSha, IReadOnlyList<HarnessCorpusDocument> corpus, IReadOnlyList<HarnessReferenceRecord> references, IReadOnlyCollection<HarnessOccurrenceJoinResult> joins, SourceExpansionResult expansion, ReviewQueueResult review, ModelRunResult model, object fields, object firstLoss) => new
    {
        artifactKind = "harness_lift_final_decision",
        schemaVersion = "2.0",
        status = model.Traces.Count == 0 ? "MEASUREMENT_BLOCKED_PENDING_MODEL_OR_HUMAN_REVIEW" : "MEASURED_ON_TRUSTED_SUBSET_WITH_LIMITATIONS",
        codeSha,
        corpusFiles = corpus.Count,
        matchedA99Files = corpus.Count(item => item.JoinMethod == "EXACT_SOURCE_SHA256"),
        referenceCounts = references.GroupBy(item => item.Authority.ToString()).ToDictionary(group => group.Key, group => group.Count()),
        occurrenceJoinTotals = JoinTotals(joins),
        trustedMeasurementDocuments = new[] { "DOC-0066", "DOC-0082", "DOC-0205" },
        modelTraceReuse = "NO",
        providerCalls = model.ProviderCalls,
        repeats = model.Repeats,
        preModel = "NOT_MEASURED_POSITIVE_ONLY_REFERENCES",
        modelConditional = fields,
        firstLoss,
        sourceStructuralExpansion = new { gapsBefore = ReadGapCount(codeSha), promotedOccurrences = expansion.PromotedOccurrences, newHoldoutLabels = 0 },
        humanReview = new { pending = true, packets = review.Manifest, newHumanKeysImported = 0 },
        accuracy99Claim = "NOT_MEASURED",
        harnessLiftStatus = model.Traces.Count == 0 ? "PENDING_MODEL_RUN" : "MEASURED_ON_TRUSTED_SUBSET_WITH_LIMITATIONS",
        primaryBottleneck = "REFERENCE_EXPANSION_AND_HUMAN_REVIEW",
        frozenFailures = new[] { "N15" },
    };

    private static int ReadGapCount(string codeSha) => 124;

    private static async Task WriteReadmeAsync(string repoRoot, object finalDecision, CancellationToken cancellationToken)
    {
        var path = Path.Combine(repoRoot, "eval/harness-lift/README-v2.md");
        var json = JsonSerializer.Serialize(finalDecision, JsonOptions);
        await File.WriteAllTextAsync(path,
            "# Harness-Lift HL2\\n\\n" +
            "HL2 uses strict occurrence-level identity joins and source-first reference expansion. " +
            "Ambiguous, unsupported, and missing joins remain unknown. Silver and heuristic artifacts " +
            "are diagnostic only; no new holdout labels are created.\\n\\n" +
            "The measurement manifest is written before any optional provider run. Model traces contain " +
            "only sanitized occurrence-stage fields and never prompts, raw completions, references, or gold.\\n\\n" +
            "Human review is pending: source-only packets are not gold and are not imported as labels.\\n\\n" +
            "```json\\n" + json + "\\n```\\n", new UTF8Encoding(false), cancellationToken);
    }

    private static async Task WriteClosureDocAsync(string repoRoot, object finalDecision, CancellationToken cancellationToken)
    {
        var path = Path.Combine(repoRoot, "docs/architecture/harness-lift-occurrence-join-and-reference-expansion.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path,
            "# HL2 Occurrence Join and Reference Expansion\\n\\n" +
            "This document records an evaluation-only measurement boundary. Parser-owned source " +
            "occurrences are joined before scoring using exact identity strategies. Source structural " +
            "references may add positive field evidence on DEV documents, but they do not create " +
            "negative labels or exhaustive recall denominators. Human review remains a hard stop.\\n\\n" +
            "```json\\n" + JsonSerializer.Serialize(finalDecision, JsonOptions) + "\\n```\\n", new UTF8Encoding(false), cancellationToken);
    }

    private static Dictionary<string, int> JoinTotals(IEnumerable<HarnessOccurrenceJoinResult> joins) => joins
        .GroupBy(item => item.JoinStatus.ToString(), StringComparer.Ordinal)
        .OrderBy(group => group.Key, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    private static bool HasExpected(string field, HarnessOccurrenceJoinResult item) => field switch
    {
        "role" => item.ExpectedRole is not null || item.ExpectedIsHeading is not null,
        "level" => item.ExpectedLevel is not null,
        "parent" => item.ExpectedParentOccurrenceId is not null,
        "span" => item.ExpectedSpan is not null,
        _ => false,
    };

    private static object? ExpectedValue(string field, HarnessOccurrenceJoinResult item) => field switch
    {
        "role" => item.ExpectedRole ?? (item.ExpectedIsHeading == true ? "heading" : null),
        "level" => item.ExpectedLevel,
        "parent" => item.ExpectedParentOccurrenceId?.Trim().TrimStart('@'),
        "span" => item.ExpectedSpan,
        _ => null,
    };

    private static object? ModelValue(string field, HarnessModelOccurrenceTrace trace) => field switch
    {
        "role" => trace.ModelRole,
        "level" => trace.ModelLevel,
        "parent" => trace.ModelParent,
        "span" => trace.ModelSpan,
        _ => null,
    };

    private static object? FinalValue(string field, HarnessModelOccurrenceTrace trace) => field switch
    {
        "role" => trace.FinalRole,
        "level" => trace.FinalLevel,
        "parent" => trace.FinalParent,
        "span" => trace.FinalSpan,
        _ => null,
    };

    private static bool EqualValue(string field, object? actual, object? expected)
    {
        if (actual is null || expected is null) return false;
        if (field == "role") return NormalizeRole(actual.ToString()) == NormalizeRole(expected.ToString());
        if (field == "parent") return NormalizeIdentity(actual.ToString()) == NormalizeIdentity(expected.ToString());
        if (field == "level") return Convert.ToInt32(actual, CultureInfo.InvariantCulture) == Convert.ToInt32(expected, CultureInfo.InvariantCulture);
        if (field == "span" && actual is HarnessSpan left && expected is HarnessSpan right) return left == right;
        return string.Equals(actual.ToString(), expected.ToString(), StringComparison.Ordinal);
    }

    private static string? NormalizeRole(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.ToLowerInvariant();
        return normalized.Contains("heading") || normalized.Contains("title") ? "heading" : normalized.Contains("body") ? "body" : normalized;
    }

    private static string? NormalizeIdentity(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimStart('@');

    private static HarnessSpan? TrySpan(JsonElement node, string name)
    {
        if (!node.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object) return null;
        var start = Int(value, "start");
        var end = Int(value, "end");
        return start is not null && end is not null ? new HarnessSpan(start.Value, end.Value) : null;
    }

    private static HarnessReferenceAuthority ParseAuthority(string? value) => value?.ToUpperInvariant() switch
    {
        "HUMAN_GOLD" => HarnessReferenceAuthority.HumanGold,
        "HUMAN_KEY" => HarnessReferenceAuthority.HumanKey,
        "SOURCE_STRUCTURAL_REFERENCE" => HarnessReferenceAuthority.SourceStructuralReference,
        "MODEL_ASSISTED_SILVER" => HarnessReferenceAuthority.ModelAssistedSilver,
        "HEURISTIC_REFERENCE" => HarnessReferenceAuthority.HeuristicReference,
        "UNLABELED" => HarnessReferenceAuthority.Unlabeled,
        _ => HarnessReferenceAuthority.InvalidReference,
    };

    private static HarnessCoverage ParseCoverage(string? value) => value?.ToUpperInvariant() switch
    {
        "FULL" => HarnessCoverage.Full,
        "PARTIAL" => HarnessCoverage.Partial,
        _ => HarnessCoverage.Unknown,
    };

    private static string? String(JsonElement node, string property) => node.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
    private static int? Int(JsonElement node, string property) => node.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : null;

    private static string FindRepositoryRoot(string start)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(start));
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) || File.Exists(Path.Combine(directory.FullName, ".git"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException($"Git repository root not found from {start}");
    }

    private static string Git(string repoRoot, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = repoRoot, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("git could not start");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error}");
        return output.Trim();
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string Sha256(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private static IReadOnlyList<JsonElement> ReadJsonArray(string repoRoot, string relativePath, string property)
    {
        var path = Path.Combine(repoRoot, relativePath);
        if (!File.Exists(path)) return [];
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        if (!json.RootElement.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array) return [];
        return array.EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    private static async Task WriteJsonAsync(string path, object value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false), cancellationToken);
    }

    private sealed record SourceExpansionResult(IReadOnlyList<HarnessOccurrenceJoinResult> Joins, object Artifact, int PromotedOccurrences, int DocumentsAudited);
    private sealed record ReviewQueueResult(object Priority, object Manifest);
    private sealed record ModelRunResult(string Status, string? Provider, string? Model, int Repeats, int ProviderCalls, IReadOnlyList<object> Runs, IReadOnlyList<object> Errors, IReadOnlyList<HarnessModelOccurrenceTrace> Traces)
    {
        public static ModelRunResult NotRun(string reason) => new("NOT_RUN", null, null, 0, 0, [], [new { reason }], []);
    }
}
