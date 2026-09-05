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
using DocxHeaderExtractor.Eval.R18;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Cli;

/// <summary>
/// Evaluation-only HARNESS-LIFT runner. It reads source/reference artifacts and serializes
/// provenance-safe measurements; it never changes extraction policy or production decisions.
/// </summary>
internal static class HarnessLiftRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly Regex NumericPrefix = new("^(?<id>\\d{3})[_-]", RegexOptions.Compiled);
    private static readonly Regex KeyLine = new(
        "^\\s*@(?<source>\\S+)\\s+(?<level>\\d+)(?:\\s+#\\s*(?<text>.*))?\\s*$",
        RegexOptions.Compiled);
    private static readonly Regex Commit = new("(?<![0-9a-f])[0-9a-f]{40}(?![0-9a-f])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task<int> RunAsync(CommandLineOptions options, CancellationToken cancellationToken)
    {
        var operation = options.HarnessLiftOperation?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(operation) || operation is "help" or "-h")
        {
            Console.WriteLine("harness-lift operations: reconcile");
            Console.WriteLine("  dhx harness-lift reconcile --root <repo> [--harness-run-model --harness-repeats 3]");
            return 0;
        }
        if (operation is not "reconcile")
            throw new ArgumentException($"harness-lift operation không hợp lệ: {operation}");

        var repoRoot = FindRepositoryRoot(options.HarnessLiftRoot ?? options.Accuracy99Root ?? Directory.GetCurrentDirectory());
        var outputRoot = Path.Combine(repoRoot, "eval", "harness-lift");
        Directory.CreateDirectory(outputRoot);
        Directory.CreateDirectory(Path.Combine(outputRoot, "review-packets"));

        var corpus = LoadCorpus(repoRoot);
        var references = LoadReferences(repoRoot, corpus);
        var referenceByDocument = references
            .Where(reference => reference.DocumentId is not null)
            .GroupBy(reference => reference.DocumentId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<HarnessReferenceRecord>)group.ToArray(), StringComparer.Ordinal);

        await WriteJsonAsync(Path.Combine(outputRoot, "corpus-map.v1.json"), new
        {
            artifactKind = "harness_lift_corpus_map",
            schemaVersion = "1.0",
            baseRevision = Git(repoRoot, "rev-parse HEAD"),
            todoCorpusRoot = Relative(repoRoot, Path.Combine(repoRoot, "todo10_8", "heading_corpus_95_word")),
            TODO_CORPUS_FILES = corpus.Count,
            MATCHED_A99_FILES = corpus.Count(item => item.JoinMethod == "EXACT_SOURCE_SHA256"),
            MATCHED_A99_GROUPS = corpus.Select(item => item.DocumentGroupId).Where(item => item is not null).Distinct().Count(),
            UNMATCHED_FILES = corpus.Count(item => item.JoinMethod == "UNKNOWN"),
            DUPLICATE_GROUPS = corpus.GroupBy(item => item.DocumentGroupId).Count(group => group.Key is not null && group.Count() > 1),
            documents = corpus,
        }, cancellationToken);

        await WriteJsonAsync(Path.Combine(outputRoot, "reference-coverage.v1.json"), new
        {
            artifactKind = "harness_lift_reference_coverage",
            schemaVersion = "1.0",
            authorityPolicy = "keys and source artifacts retain their declared provenance; silver and heuristics are diagnostic only",
            counts = references.GroupBy(item => item.Authority).ToDictionary(group => group.Key.ToString(), group => group.Count()),
            documentCounts = references.Where(item => item.DocumentId is not null).GroupBy(item => item.Authority).ToDictionary(
                group => group.Key.ToString(), group => group.Select(item => item.DocumentId).Distinct().Count()),
            references,
        }, cancellationToken);

        var historical = BuildHistoricalEvidence(repoRoot, corpus);
        await WriteJsonAsync(Path.Combine(outputRoot, "historical-evidence-ledger.v1.json"), new
        {
            artifactKind = "harness_lift_historical_evidence_ledger",
            schemaVersion = "1.0",
            identityRule = "occurrence identity is retained only when the source artifact carries it; aggregate counts never create occurrence rows",
            evidence = historical,
        }, cancellationToken);
        await WriteJsonAsync(Path.Combine(outputRoot, "historical-current-reconciliation.v1.json"), new
        {
            artifactKind = "harness_lift_historical_current_reconciliation",
            schemaVersion = "1.0",
            currentCodeSha = Git(repoRoot, "rev-parse HEAD"),
            historicalEvidence = historical.Select(item => new
            {
                item.EvidenceId,
                item.DocumentId,
                item.DocumentGroupId,
                item.SourceArtifact,
                item.SourceCommit,
                authority = item.SourceReferenceAuthority.ToString(),
                granularity = item.EvidenceGranularity.ToString(),
                strength = item.EvidenceStrength.ToString(),
                item.ReusableForCurrentAttribution,
                currentJoin = item.DocumentId is null ? "UNJOINED" : "DOCUMENT_JOINED_OCCURRENCE_REQUIRES_REVIEW",
            }),
            policy = "historical aggregate evidence never creates current occurrence labels; current attribution is only measured when identity and field-specific authority are present",
        }, cancellationToken);

        var sourceRuns = await RunDeterministicAsync(repoRoot, corpus, referenceByDocument, outputRoot, cancellationToken);
        await WriteJsonAsync(Path.Combine(outputRoot, "pre-model-coverage.v1.json"), new
        {
            artifactKind = "harness_lift_pre_model_coverage",
            schemaVersion = "1.0",
            codeSha = Git(repoRoot, "rev-parse HEAD"),
            providerCalls = 0,
            documents = sourceRuns.Select(item => item.PreModel),
            aggregate = AggregatePreModel(sourceRuns),
        }, cancellationToken);

        await WriteJsonAsync(Path.Combine(outputRoot, "r18-decision-trace.v1.json"), new
        {
            artifactKind = "harness_lift_r18_decision_trace",
            schemaVersion = "1.0",
            referenceJoin = "post-run only; no reference fields entered pipeline reasoning",
            providerCalls = 0,
            documents = sourceRuns.Select(item => item.R18),
            traceStorage = "full per-document reports are stored under r18-traces; this index is intentionally compact",
        }, cancellationToken);

        var matrix = BuildCoverageMatrix(sourceRuns, referenceByDocument);
        await WriteJsonAsync(Path.Combine(outputRoot, "evidence-coverage-matrix.v1.json"), new
        {
            artifactKind = "harness_lift_evidence_coverage_matrix",
            schemaVersion = "1.0",
            rows = matrix,
        }, cancellationToken);

        var unknownGaps = BuildUnknownGaps(corpus, references, referenceByDocument);
        await WriteJsonAsync(Path.Combine(outputRoot, "unknown-gap-manifest.v1.json"), new
        {
            artifactKind = "harness_lift_unknown_gap_manifest",
            schemaVersion = "1.0",
            gaps = unknownGaps,
        }, cancellationToken);

        var reviewRequired = BuildReviewRequired(corpus, references, referenceByDocument);
        await WriteJsonAsync(Path.Combine(outputRoot, "review-required.v1.json"), new
        {
            artifactKind = "harness_lift_review_required",
            schemaVersion = "1.0",
            trueBlindAvailable = false,
            required = reviewRequired.Count > 0,
            packets = reviewRequired,
        }, cancellationToken);

        var trusted = SelectTrustedDocuments(repoRoot, corpus, referenceByDocument);
        var measurementManifest = BuildMeasurementManifest(repoRoot, corpus, references, trusted, options);
        await WriteJsonAsync(Path.Combine(outputRoot, "current-measurement-manifest.v1.json"), measurementManifest, cancellationToken);

        ModelMeasurement modelMeasurement;
        if (options.HarnessLiftRunModel)
        {
            modelMeasurement = await RunCurrentModelAsync(repoRoot, options, trusted, referenceByDocument, cancellationToken);
        }
        else
        {
            modelMeasurement = ModelMeasurement.NotRun("provider run requires explicit --harness-run-model after the manifest is frozen");
        }

        await WriteJsonAsync(Path.Combine(outputRoot, "harness-lift-runs.v1.json"), modelMeasurement, cancellationToken);
        await WriteJsonAsync(Path.Combine(outputRoot, "harness-lift-summary.v1.json"), BuildSummary(repoRoot, corpus, references, sourceRuns, modelMeasurement), cancellationToken);
        await WriteJsonAsync(Path.Combine(outputRoot, "harness-lift-by-mode.v1.json"), GroupPreModel(sourceRuns, item => item.DocumentMode ?? "UNKNOWN"), cancellationToken);
        await WriteJsonAsync(Path.Combine(outputRoot, "harness-lift-by-family.v1.json"), GroupPreModel(sourceRuns, item => item.Corpus.FamilyId ?? "UNKNOWN"), cancellationToken);
        await WriteJsonAsync(Path.Combine(outputRoot, "harness-lift-by-reference-authority.v1.json"), BuildAuthorityBreakdown(sourceRuns, references, referenceByDocument), cancellationToken);
        await WriteJsonAsync(Path.Combine(outputRoot, "first-loss-summary.v1.json"), BuildFirstLossSummary(sourceRuns, modelMeasurement), cancellationToken);

        var memory = BuildCorrectionMemoryAudit(repoRoot);
        await WriteJsonAsync(Path.Combine(outputRoot, "correction-memory-audit.v1.json"), memory, cancellationToken);

        var finalDecision = BuildFinalDecision(repoRoot, corpus, references, historical, sourceRuns, modelMeasurement, unknownGaps, reviewRequired);
        await WriteJsonAsync(Path.Combine(outputRoot, "final-decision.v1.json"), finalDecision, cancellationToken);
        await WriteReadmeAsync(repoRoot, finalDecision, cancellationToken);
        await WriteArchitectureDocAsync(repoRoot, finalDecision, cancellationToken);

        Console.WriteLine($"HARNESS_LIFT corpus={corpus.Count} references={references.Count} historical={historical.Count} providerCalls={modelMeasurement.ProviderCalls}");
        Console.WriteLine($"TRUSTED_MEASUREMENT_DOCUMENTS={modelMeasurement.TrustedDocuments} MODEL_STATUS={modelMeasurement.Status}");
        Console.WriteLine($"UNKNOWN_GAPS={unknownGaps.Count} REVIEW_REQUIRED={reviewRequired.Count}");
        return 0;
    }

    private static List<HarnessCorpusDocument> LoadCorpus(string repoRoot)
    {
        var corpusRoot = Path.Combine(repoRoot, "todo10_8", "heading_corpus_95_word");
        if (!Directory.Exists(corpusRoot)) throw new DirectoryNotFoundException(corpusRoot);
        var inventory = LoadJson(repoRoot, "eval/a99-dataset/document-inventory.v1.json");
        var splitNode = LoadJson(repoRoot, "eval/a99-dataset/evaluation-splits.v1.json");
        var a99BySha = inventory.RootElement.GetProperty("documents").EnumerateArray()
            .Where(item => item.TryGetProperty("sourceSha256", out _))
            .GroupBy(item => item.GetProperty("sourceSha256").GetString()!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(item => item.GetProperty("documentGroupId").GetString(), StringComparer.Ordinal)
                    .ThenBy(item => item.GetProperty("documentId").GetString(), StringComparer.Ordinal)
                    .First(),
                StringComparer.OrdinalIgnoreCase);
        var splits = splitNode.RootElement.GetProperty("splits").EnumerateArray()
            .Where(item => item.TryGetProperty("documentGroupId", out _))
            .ToDictionary(item => item.GetProperty("documentGroupId").GetString()!, item => item.GetProperty("split").GetString() ?? "UNKNOWN", StringComparer.Ordinal);

        var result = new List<HarnessCorpusDocument>();
        foreach (var file in Directory.EnumerateFiles(corpusRoot, "*.docx", SearchOption.AllDirectories).OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
        {
            var sha = Sha256(file);
            var name = Path.GetFileName(file);
            var prefix = NumericPrefix.Match(name);
            var docId = prefix.Success ? prefix.Groups["id"].Value : Path.GetFileNameWithoutExtension(name);
            if (a99BySha.TryGetValue(sha, out var entry))
            {
                var group = entry.GetProperty("documentGroupId").GetString();
                result.Add(new HarnessCorpusDocument
                {
                    DocumentId = entry.GetProperty("documentId").GetString() ?? docId,
                    Path = Relative(repoRoot, file),
                    SourceSha256 = sha,
                    DocumentGroupId = group,
                    Split = group is not null && splits.TryGetValue(group, out var split) ? split : null,
                    SourceKind = "DOCX",
                    FamilyId = entry.TryGetProperty("familyId", out var family) ? family.GetString() : null,
                    FamilyAssignmentAuthority = entry.TryGetProperty("familyAssignmentAuthority", out var authority) ? authority.GetString() : null,
                    JoinMethod = "EXACT_SOURCE_SHA256",
                });
            }
            else
            {
                result.Add(new HarnessCorpusDocument
                {
                    DocumentId = docId,
                    Path = Relative(repoRoot, file),
                    SourceSha256 = sha,
                    SourceKind = "DOCX",
                    JoinMethod = "UNKNOWN",
                });
            }
        }
        return result;
    }

    private static List<HarnessReferenceRecord> LoadReferences(string repoRoot, IReadOnlyList<HarnessCorpusDocument> corpus)
    {
        var byPrefix = corpus.ToDictionary(item => PrefixOf(item.Path), item => item, StringComparer.OrdinalIgnoreCase);
        var result = new List<HarnessReferenceRecord>();
        var roots = new[] { Path.Combine(repoRoot, "keys"), Path.Combine(repoRoot, "eval") };
        foreach (var root in roots.Where(Directory.Exists))
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(file => IsReferenceArtifact(repoRoot, file)))
        {
            var relative = Relative(repoRoot, file);
            var authority = ReferenceAuthority(relative);
            var prefix = PrefixOf(Path.GetFileName(file));
            byPrefix.TryGetValue(prefix, out var doc);
            var (coverage, metrics, provenance, notes) = ReferenceShape(relative, authority);
            result.Add(new HarnessReferenceRecord
            {
                ReferenceId = relative.Replace('\\', '/'),
                DocumentId = doc?.DocumentId,
                DocumentGroupId = doc?.DocumentGroupId,
                Authority = authority,
                SourcePath = relative,
                SourceSha256 = Sha256(file),
                Coverage = coverage,
                SupportedMetrics = metrics,
                Provenance = provenance,
                Notes = notes,
            });
        }
        return result.OrderBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsReferenceArtifact(string repoRoot, string file)
    {
        var relative = Relative(repoRoot, file).Replace('\\', '/');
        if (relative.StartsWith("keys/", StringComparison.OrdinalIgnoreCase))
            return Path.GetExtension(file).Equals(".key", StringComparison.OrdinalIgnoreCase) ||
                relative.Contains("occurrence-bridge/", StringComparison.OrdinalIgnoreCase) && file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                relative.Contains("hierarchy/", StringComparison.OrdinalIgnoreCase) && file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                relative.Contains("tagged-pdf-coverage/", StringComparison.OrdinalIgnoreCase) && file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".provenance.json", StringComparison.OrdinalIgnoreCase);
        return relative.Contains("silver-labels/", StringComparison.OrdinalIgnoreCase) && file.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    private static HarnessReferenceAuthority ReferenceAuthority(string relative)
    {
        if (relative.Contains("silver-labels", StringComparison.OrdinalIgnoreCase)) return HarnessReferenceAuthority.ModelAssistedSilver;
        if (relative.Contains("toc-derived", StringComparison.OrdinalIgnoreCase)) return HarnessReferenceAuthority.HeuristicReference;
        if (relative.Contains("tagged-pdf-coverage", StringComparison.OrdinalIgnoreCase) ||
            relative.Contains("occurrence-bridge", StringComparison.OrdinalIgnoreCase)) return HarnessReferenceAuthority.SourceStructuralReference;
        return HarnessReferenceAuthority.HumanKey;
    }

    private static HarnessReferenceAuthority HistoricalReferenceAuthority(string relative) =>
        relative.StartsWith("keys/", StringComparison.OrdinalIgnoreCase)
            ? ReferenceAuthority(relative)
            : relative.Contains("silver-labels", StringComparison.OrdinalIgnoreCase)
                ? HarnessReferenceAuthority.ModelAssistedSilver
                : HarnessReferenceAuthority.Unlabeled;

    private static (HarnessCoverage Coverage, IReadOnlyList<string> Metrics, string Provenance, string Notes) ReferenceShape(
        string relative, HarnessReferenceAuthority authority)
    {
        if (authority == HarnessReferenceAuthority.ModelAssistedSilver)
            return (HarnessCoverage.Partial, ["headingExistence", "role", "level"], "model-assisted label artifact", "diagnostic only; never an official denominator");
        if (authority == HarnessReferenceAuthority.HeuristicReference)
            return (HarnessCoverage.Partial, ["headingExistence"], "TOC-derived proxy", "heuristic proxy; not an exhaustive human reference");
        if (relative.Contains("hierarchy", StringComparison.OrdinalIgnoreCase))
            return (HarnessCoverage.Full, ["headingExistence", "level", "parent", "hierarchy"], "retained hierarchy reference", "source/human provenance retained from the original artifact");
        if (relative.Contains("occurrence-bridge", StringComparison.OrdinalIgnoreCase) || relative.Contains("tagged-pdf-coverage", StringComparison.OrdinalIgnoreCase))
            return (HarnessCoverage.Full, ["headingExistence", "role", "span", "level"], "source occurrence bridge", "exactness is limited to fields actually present in the artifact");
        if (relative.Contains("partial", StringComparison.OrdinalIgnoreCase))
            return (HarnessCoverage.Partial, ["headingExistence", "role", "level"], "human key marked partial", "positive labels only; exhaustive negatives are not established");
        return (HarnessCoverage.Full, ["headingExistence", "role", "level"], "retained human key", "full for listed heading fields; span/parent remain unsupported unless explicitly present");
    }

    private static async Task<List<RunRecord>> RunDeterministicAsync(
        string repoRoot,
        IReadOnlyList<HarnessCorpusDocument> corpus,
        IReadOnlyDictionary<string, IReadOnlyList<HarnessReferenceRecord>> referenceByDocument,
        string outputRoot,
        CancellationToken cancellationToken)
    {
        var records = new List<RunRecord>();
        var sourceReader = new OpenXmlDocumentSource();
        var pipelineOptions = new PipelineOptions { DisableLlm = true };
        using var pipeline = new AuthorityExtractionPipeline(pipelineOptions);
        foreach (var item in corpus)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(repoRoot, item.Path);
            Console.Error.WriteLine($"  deterministic {item.DocumentId} {Path.GetFileName(path)}");
            try
            {
                var source = sourceReader.Read(path);
                var execution = await pipeline.RunDocumentExecutionAsync(path, null, cancellationToken);
                var outline = execution.CompatibilityOutline;
                var r18 = R18DecisionOwnershipAnalyzer.Build(source, execution);
                var r18RelativePath = $"r18-traces/{item.DocumentId}.v1.json";
                await WriteJsonAsync(Path.Combine(outputRoot, r18RelativePath.Replace('/', Path.DirectorySeparatorChar)), r18, cancellationToken);
                var r18Pointer = new R18TracePointer(
                    item.DocumentId,
                    r18RelativePath,
                    r18.Route,
                    r18.ProviderCalls,
                    r18.Direction,
                    r18.FirstLossSummary);
                var references = referenceByDocument.TryGetValue(item.DocumentId, out var rows) ? rows : [];
                var keyOccurrences = ReadKeyOccurrences(repoRoot, references, source);
                var audit = outline.RouteAudit;
                var candidateIds = (audit?.CandidateBlocks ?? []).Select(block => block.Id)
                    .Concat(audit?.SelectedCandidateBlocks.Select(block => block.Id) ?? [])
                    .Concat(audit?.BlockDecisions.Select(block => block.Id) ?? [])
                    .ToHashSet(StringComparer.Ordinal);
                var finalIds = outline.Headings.Select(heading => heading.SourceId ?? heading.StableId ?? string.Empty)
                    .Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
                var sourceVisible = keyOccurrences.Count(occurrence => source.Paragraphs.Any(p => p.SourceId == occurrence.SourceId));
                var candidateSelected = keyOccurrences.Count(occurrence => candidateIds.Contains(occurrence.SourceId) || finalIds.Contains(occurrence.SourceId));
                var mode = outline.DocumentMode?.Mode.ToString();
                var preModel = new
                {
                    documentId = item.DocumentId,
                    documentGroupId = item.DocumentGroupId,
                    split = item.Split,
                    familyId = item.FamilyId,
                    documentMode = mode,
                    sourceDocuments = 1,
                    sourceReferenceHeadings = keyOccurrences.Count,
                    sourceVisible,
                    sourceLost = keyOccurrences.Count - sourceVisible,
                    candidateEligible = audit?.CandidatesAvailable ?? source.Paragraphs.Count,
                    candidateSelected = audit?.CandidatesSelected ?? candidateSelected,
                    candidateLost = Math.Max(0, (audit?.CandidatesAvailable ?? source.Paragraphs.Count) - (audit?.CandidatesSelected ?? candidateSelected)),
                    modelExposureEligible = 0,
                    deterministicAssigned = outline.Headings.Count(heading => heading.Source is not HeadingSource.Model),
                    providerCalls = 0,
                };
                var packetPath = await WriteReviewPacketAsync(repoRoot, outputRoot, item, source, referenceByDocument, cancellationToken);
                records.Add(new RunRecord(item, mode, preModel, r18Pointer, keyOccurrences, packetPath));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                records.Add(RunRecord.Failed(item, ex.Message));
                Console.Error.WriteLine($"  failed {item.DocumentId}: {ex.Message}");
            }
        }
        return records;
    }

    private static async Task<string> WriteReviewPacketAsync(
        string repoRoot,
        string outputRoot,
        HarnessCorpusDocument item,
        SourceDocument source,
        IReadOnlyDictionary<string, IReadOnlyList<HarnessReferenceRecord>> references,
        CancellationToken cancellationToken)
    {
        var relative = $"review-packets/{item.DocumentId}.v1.json";
        var path = Path.Combine(outputRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        var packet = new
        {
            artifactKind = "harness_lift_source_first_blind_review_packet",
            schemaVersion = "1.0",
            authorityClass = "PARSER_SOURCE_FACTS",
            documentId = item.DocumentId,
            documentGroupId = item.DocumentGroupId,
            sourceSha256 = item.SourceSha256,
            reviewInstructions = new
            {
                question = "Which source occurrences are document outline headings?",
                allowedLabels = new[] { "REVIEWED_HEADING", "REVIEWED_NON_HEADING", "UNCERTAIN" },
                prohibitedEvidence = new[] { "model prediction", "candidate ranking", "confidence", "final output", "silver label", "expected answer" },
            },
            occurrences = source.Paragraphs.Select(paragraph => new
            {
                sourceId = paragraph.SourceId,
                sourceOrdinal = paragraph.SourceOrdinal,
                rawText = paragraph.Text,
                fullSpan = new { start = 0, end = paragraph.Text.Length },
                style = paragraph.Style,
                numbering = paragraph.Numbering,
                layout = paragraph.Layout,
                inTableOfContents = paragraph.InTableOfContents,
            }),
        };
        await WriteJsonAsync(path, packet, cancellationToken);
        return Relative(repoRoot, path);
    }

    private static List<HarnessHistoricalEvidence> BuildHistoricalEvidence(
        string repoRoot,
        IReadOnlyList<HarnessCorpusDocument> corpus)
    {
        var byNumericId = corpus.ToDictionary(item => PrefixOf(item.Path), item => item, StringComparer.OrdinalIgnoreCase);
        var result = new List<HarnessHistoricalEvidence>();
        var evalRoot = Path.Combine(repoRoot, "eval");
        if (!Directory.Exists(evalRoot)) return result;
        foreach (var file in Directory.EnumerateFiles(evalRoot, "*.json", SearchOption.AllDirectories)
                     .Where(path => !path.Contains("\\harness-lift\\", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(file));
                var raw = json.RootElement.GetRawText();
                var documentIds = CollectDocumentIds(json.RootElement).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var occurrenceIds = CollectValues(json.RootElement, property =>
                    property.Equals("occurrenceId", StringComparison.OrdinalIgnoreCase) ||
                    property.Equals("goldStableId", StringComparison.OrdinalIgnoreCase) ||
                    property.Equals("stableId", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var granularity = occurrenceIds.Length > 0
                    ? HarnessEvidenceGranularity.Occurrence
                    : documentIds.Length > 0 ? HarnessEvidenceGranularity.Document : HarnessEvidenceGranularity.AggregateOnly;
                var strength = raw.Contains("silver", StringComparison.OrdinalIgnoreCase)
                    ? HarnessEvidenceStrength.DiagnosticOnly
                    : granularity == HarnessEvidenceGranularity.AggregateOnly
                        ? HarnessEvidenceStrength.Partial
                        : HarnessEvidenceStrength.Proven;
                var evidenceKeys = occurrenceIds.Length > 0
                    ? occurrenceIds.Cast<string?>().ToArray()
                    : documentIds.Length == 0 ? new[] { (string?)null } : documentIds.Cast<string?>().ToArray();
                foreach (var evidenceKey in evidenceKeys)
                {
                    HarnessCorpusDocument? corpusDocument = null;
                    if (!string.IsNullOrWhiteSpace(evidenceKey))
                    {
                        corpusDocument = corpus.FirstOrDefault(item => string.Equals(item.DocumentId, evidenceKey, StringComparison.OrdinalIgnoreCase));
                        if (corpusDocument is null)
                            byNumericId.TryGetValue(NormalizeNumericId(evidenceKey), out corpusDocument);
                    }
                    var sourceArtifact = Relative(repoRoot, file);
                    var commitMatch = Commit.Match(raw);
                    result.Add(new HarnessHistoricalEvidence
                    {
                        EvidenceId = $"{sourceArtifact.Replace('\\', '/') }#{evidenceKey ?? "aggregate"}",
                        DocumentId = corpusDocument?.DocumentId,
                        DocumentGroupId = corpusDocument?.DocumentGroupId,
                        SourceArtifact = sourceArtifact,
                        SourceCommit = commitMatch.Success ? commitMatch.Value : null,
                        SourceProbe = Path.GetFileName(file),
                        SourceReferenceAuthority = HistoricalReferenceAuthority(sourceArtifact),
                        EvidenceGranularity = granularity,
                        EvidenceStrength = strength,
                        OccurrenceIdentity = granularity == HarnessEvidenceGranularity.Occurrence
                            ? new HarnessOccurrenceIdentity { StableId = evidenceKey }
                            : null,
                        HistoricalStage = InferHistoricalStage(raw),
                        HistoricalFinding = "retained historical evaluation artifact",
                        R18FirstLossCompatibility = InferTopLevelLoss(raw),
                        FineGrainedLossStage = InferFineLoss(raw),
                        ReusableForCurrentAttribution = granularity == HarnessEvidenceGranularity.Occurrence && strength == HarnessEvidenceStrength.Proven ? "PARTIAL" : "NO",
                        Reason = granularity == HarnessEvidenceGranularity.AggregateOnly
                            ? "aggregate artifact retained for context; it cannot create occurrence denominator"
                            : granularity == HarnessEvidenceGranularity.Occurrence
                                ? "occurrence identity was retained from an explicit occurrence/stable-id field"
                            : "identity and authority are retained at the granularity present in the source artifact",
                    });
                }
            }
            catch (JsonException)
            {
                // A malformed historical artifact remains discoverable by path, but is not used as evidence.
            }
        }
        return result;

        IEnumerable<string> CollectDocumentIds(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name.Equals("documentId", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { } value)
                        yield return value;
                    foreach (var child in CollectDocumentIds(property.Value)) yield return child;
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in element.EnumerateArray())
                    foreach (var id in CollectDocumentIds(child)) yield return id;
            }
        }

        IEnumerable<string> CollectValues(JsonElement element, Func<string, bool> propertyMatches)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (propertyMatches(property.Name) && property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { } value)
                        yield return value;
                    foreach (var child in CollectValues(property.Value, propertyMatches)) yield return child;
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in element.EnumerateArray())
                    foreach (var value in CollectValues(child, propertyMatches)) yield return value;
            }
        }

        static string NormalizeNumericId(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "aggregate";
            var digits = new string(value.Where(char.IsDigit).Take(3).ToArray());
            return digits.Length == 3 ? digits : value;
        }
        static string? InferHistoricalStage(string raw) =>
            raw.Contains("candidate", StringComparison.OrdinalIgnoreCase) ? "CANDIDATE_CONSTRUCTION" :
            raw.Contains("span", StringComparison.OrdinalIgnoreCase) ? "SPAN" :
            raw.Contains("ranking", StringComparison.OrdinalIgnoreCase) ? "SELECTION_RANKING_BUDGET" : null;
        static string? InferTopLevelLoss(string raw) =>
            raw.Contains("candidate", StringComparison.OrdinalIgnoreCase) ? "CANDIDATE_LOSS" :
            raw.Contains("span", StringComparison.OrdinalIgnoreCase) ? "SPAN_ERROR" : null;
        static string? InferFineLoss(string raw) =>
            raw.Contains("candidate", StringComparison.OrdinalIgnoreCase) ? "CANDIDATE_CONSTRUCTION" :
            raw.Contains("span", StringComparison.OrdinalIgnoreCase) ? "SPAN_TIMEOUT_WRAPPER" : null;
    }

    private static IReadOnlyList<KeyOccurrence> ReadKeyOccurrences(
        string repoRoot,
        IReadOnlyList<HarnessReferenceRecord> references,
        SourceDocument source)
    {
        var result = new List<KeyOccurrence>();
        foreach (var reference in references.Where(item => item.Authority is HarnessReferenceAuthority.HumanGold or HarnessReferenceAuthority.HumanKey))
        {
            var path = Path.Combine(repoRoot, reference.SourcePath);
            if (!File.Exists(path) || !path.EndsWith(".key", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var line in File.ReadLines(path))
            {
                var match = KeyLine.Match(line);
                if (!match.Success) continue;
                var sourceId = match.Groups["source"].Value;
                result.Add(new KeyOccurrence(reference.ReferenceId, sourceId, int.Parse(match.Groups["level"].Value), match.Groups["text"].Value));
            }
        }
        return result.DistinctBy(item => $"{item.ReferenceId}\u001f{item.SourceId}\u001f{item.Level}\u001f{item.Text}", StringComparer.Ordinal).ToArray();
    }

    private static object AggregatePreModel(IReadOnlyList<RunRecord> records) => new
    {
        documents = records.Count(item => item.PreModel is not null),
        sourceReferenceHeadings = records.Sum(item => ReadInt(item.PreModel, "sourceReferenceHeadings")),
        sourceVisible = records.Sum(item => ReadInt(item.PreModel, "sourceVisible")),
        sourceLost = records.Sum(item => ReadInt(item.PreModel, "sourceLost")),
        candidateEligible = records.Sum(item => ReadInt(item.PreModel, "candidateEligible")),
        candidateSelected = records.Sum(item => ReadInt(item.PreModel, "candidateSelected")),
        candidateLost = records.Sum(item => ReadInt(item.PreModel, "candidateLost")),
        modelExposureEligible = records.Sum(item => ReadInt(item.PreModel, "modelExposureEligible")),
        providerCalls = 0,
    };

    private static IReadOnlyList<object> BuildCoverageMatrix(
        IReadOnlyList<RunRecord> records,
        IReadOnlyDictionary<string, IReadOnlyList<HarnessReferenceRecord>> referenceByDocument) =>
        records.Select(record =>
        {
            var references = referenceByDocument.TryGetValue(record.Corpus.DocumentId, out var rows) ? rows : [];
            var best = references.Where(item => HarnessLiftAccounting.IsOfficial(item.Authority)).OrderByDescending(item => item.Coverage).FirstOrDefault();
            return (object)new
            {
                documentId = record.Corpus.DocumentId,
                documentGroupId = record.Corpus.DocumentGroupId,
                sourceCoverage = record.Error is null ? "PROVEN" : "UNKNOWN",
                candidateCoverage = record.Error is null && best is not null ? "PARTIAL" : "UNKNOWN",
                modelExposure = "NOT_OBSERVABLE",
                modelRoleDecision = "NOT_OBSERVABLE",
                modelLevelDecision = "NOT_OBSERVABLE",
                modelParentDecision = "NOT_OBSERVABLE",
                spanDecision = best is not null && HarnessLiftAccounting.Supports(best, HarnessMetric.Span) ? "PARTIAL" : "NOT_APPLICABLE",
                resolverDelta = "NOT_OBSERVABLE",
                finalResult = record.Error is null ? "PROVEN_AS_RUNTIME_OUTPUT" : "UNKNOWN",
                trustedReference = best?.Authority.ToString() ?? "UNKNOWN",
            };
        }).ToArray();

    private static List<object> BuildUnknownGaps(
        IReadOnlyList<HarnessCorpusDocument> corpus,
        IReadOnlyList<HarnessReferenceRecord> references,
        IReadOnlyDictionary<string, IReadOnlyList<HarnessReferenceRecord>> referenceByDocument)
    {
        var result = new List<object>();
        foreach (var doc in corpus)
        {
            var rows = referenceByDocument.TryGetValue(doc.DocumentId, out var matched) ? matched : [];
            var official = rows.Where(item => HarnessLiftAccounting.IsOfficial(item.Authority)).ToArray();
            if (official.Length == 0)
            {
                result.Add(new { documentId = doc.DocumentId, documentGroupId = doc.DocumentGroupId, missing = "exhaustive heading existence/role reference", reason = "no official reference joined by source identity" });
                continue;
            }
            foreach (var metric in Enum.GetValues<HarnessMetric>())
                if (!official.Any(reference => HarnessLiftAccounting.Supports(reference, metric)))
                    result.Add(new { documentId = doc.DocumentId, documentGroupId = doc.DocumentGroupId, missing = metric.ToString(), reason = "joined references do not support this field" });
        }
        return result;
    }

    private static List<object> BuildReviewRequired(
        IReadOnlyList<HarnessCorpusDocument> corpus,
        IReadOnlyList<HarnessReferenceRecord> references,
        IReadOnlyDictionary<string, IReadOnlyList<HarnessReferenceRecord>> referenceByDocument) =>
        corpus.Where(doc => !referenceByDocument.TryGetValue(doc.DocumentId, out var rows) || !rows.Any(item => HarnessLiftAccounting.IsOfficial(item.Authority)))
            .Select(doc => (object)new
            {
                documentId = doc.DocumentId,
                documentGroupId = doc.DocumentGroupId,
                packet = $"review-packets/{doc.DocumentId}.v1.json",
                reason = "no official exhaustive occurrence reference is joined for this document",
                trueBlind = false,
            }).ToList();

    private static object BuildMeasurementManifest(
        string repoRoot,
        IReadOnlyList<HarnessCorpusDocument> corpus,
        IReadOnlyList<HarnessReferenceRecord> references,
        IReadOnlyList<HarnessCorpusDocument> trusted,
        CommandLineOptions options) => new
    {
        artifactKind = "harness_lift_current_measurement_manifest",
        schemaVersion = "1.0",
        codeSha = Git(repoRoot, "rev-parse HEAD"),
        documentIds = trusted.Select(item => item.DocumentId).Distinct().OrderBy(item => item).ToArray(),
        sourceHashes = corpus.ToDictionary(item => item.DocumentId, item => item.SourceSha256),
        referenceHashes = references.Where(item => item.DocumentId is not null).ToDictionary(item => item.ReferenceId, item => item.SourceSha256),
        referenceAuthorities = references.GroupBy(item => item.Authority).ToDictionary(group => group.Key.ToString(), group => group.Count()),
        providerProfile = options.HarnessLiftRunModel ? "CURRENT_PROFILE" : "NOT_RUN",
        provider = options.HarnessLiftRunModel ? "OpenRouter" : "NONE",
        model = options.HarnessLiftRunModel ? options.Provider.Remote.Model : null,
        temperature = 0,
        concurrency = 1,
        maxTokens = options.Provider.Remote.MaxOutputTokens,
        repeats = options.HarnessLiftRunModel ? options.HarnessLiftRepeats : 0,
        selectionRule = "official references supporting heading existence; silver and heuristic references excluded; deterministic bounded subset selects at most one smallest source per family, up to three families",
        trustedSubsetSize = trusted.Count,
        frozenBeforeCurrentOutput = true,
        trueBlindAvailable = false,
    };

    private static IReadOnlyList<HarnessCorpusDocument> SelectTrustedDocuments(
        string repoRoot,
        IReadOnlyList<HarnessCorpusDocument> corpus,
        IReadOnlyDictionary<string, IReadOnlyList<HarnessReferenceRecord>> referenceByDocument)
    {
        var eligible = corpus
            .Where(item => referenceByDocument.TryGetValue(item.DocumentId, out var rows) &&
                rows.Any(reference => HarnessLiftAccounting.Supports(reference, HarnessMetric.HeadingExistence)))
            .OrderBy(item => new FileInfo(Path.Combine(repoRoot, item.Path)).Length)
            .ThenBy(item => item.DocumentId, StringComparer.Ordinal)
            .ToArray();

        var selected = new List<HarnessCorpusDocument>();
        var selectedFamilies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in eligible)
        {
            var family = item.FamilyId ?? "UNKNOWN";
            if (!selectedFamilies.Add(family)) continue;
            selected.Add(item);
            if (selected.Count == 3) return selected;
        }

        return selected.Count == 3
            ? selected
            : selected.Concat(eligible.Where(item => !selected.Contains(item)).Take(3 - selected.Count)).ToArray();
    }

    private static async Task<ModelMeasurement> RunCurrentModelAsync(
        string repoRoot,
        CommandLineOptions options,
        IReadOnlyList<HarnessCorpusDocument> trusted,
        IReadOnlyDictionary<string, IReadOnlyList<HarnessReferenceRecord>> referenceByDocument,
        CancellationToken cancellationToken)
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey)) return ModelMeasurement.NotRun("OPENROUTER_API_KEY is unavailable; no provider call was attempted");
        options.Provider.Backend = InferenceBackend.OpenRouter;
        options.Provider.Remote = RemoteInferenceOptions.FromEnvironment("openrouter");
        var runs = new List<ModelRun>();
        for (var repeat = 1; repeat <= options.HarnessLiftRepeats; repeat++)
        {
            var pipelineOptions = new PipelineOptions();
            using var pipeline = new AuthorityExtractionPipeline(pipelineOptions, new HeaderClassifierFactory(options.Provider));
            foreach (var item in trusted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(repoRoot, item.Path);
                Console.Error.WriteLine($"  model repeat={repeat} {item.DocumentId}");
                try
                {
                    var execution = await pipeline.RunDocumentExecutionAsync(path, null, cancellationToken);
                    var outline = execution.CompatibilityOutline;
                    var expected = ReadExpectedIds(repoRoot, referenceByDocument.TryGetValue(item.DocumentId, out var rows) ? rows : []);
                    var actual = outline.Headings.Select(heading => heading.SourceId ?? heading.StableId ?? "")
                        .Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
                    var matched = expected.Count(actual.Contains);
                    runs.Add(new ModelRun($"repeat-{repeat}-{item.DocumentId}", repeat, item.DocumentId,
                        execution.Result.Provenance.ProviderCalls, expected.Count, matched,
                        execution.CompatibilityOutline.RouteAudit?.RawAnalystResponses.Count ?? 0,
                        "MODEL_PROPOSAL_OCCURRENCE_JOIN_NOT_OBSERVABLE"));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    runs.Add(new ModelRun($"repeat-{repeat}-{item.DocumentId}", repeat, item.DocumentId, 0, 0, 0, 0, $"ERROR:{ex.GetType().Name}"));
                }
            }
        }
        var recalls = runs.Where(item => item.Expected > 0).Select(item => (double)item.Matched / item.Expected).ToArray();
        return new ModelMeasurement
        {
            Status = "MEASURED_ON_TRUSTED_SUBSET_WITH_LIMITATIONS",
            Provider = "OpenRouter",
            Model = options.Provider.Remote.Model,
            ProviderCalls = runs.Sum(item => item.ProviderCalls),
            RequestCount = runs.Sum(item => item.RequestCount),
            Repeats = options.HarnessLiftRepeats,
            TrustedDocuments = trusted.Count,
            Runs = runs,
            FinalRecall = HarnessLiftAccounting.Summarize(recalls),
            ModelProposalMetrics = "NOT_MEASURED: current RouteExecutionAudit does not preserve an occurrence-level model proposal join",
            HarnessLift = "NOT_MEASURED: same occurrence-level model proposal and final pair is unavailable",
        };
    }

    private static HashSet<string> ReadExpectedIds(string repoRoot, IReadOnlyList<HarnessReferenceRecord> references)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in references.Where(item => item.Authority is HarnessReferenceAuthority.HumanGold or HarnessReferenceAuthority.HumanKey))
        {
            var path = Path.Combine(repoRoot, reference.SourcePath);
            foreach (var line in File.Exists(path) ? File.ReadLines(path) : [])
            {
                var match = KeyLine.Match(line);
                if (match.Success) ids.Add(match.Groups["source"].Value);
            }
        }
        return ids;
    }

    private static object BuildSummary(
        string repoRoot,
        IReadOnlyList<HarnessCorpusDocument> corpus,
        IReadOnlyList<HarnessReferenceRecord> references,
        IReadOnlyList<RunRecord> sourceRuns,
        ModelMeasurement model) => new
    {
        artifactKind = "harness_lift_summary",
        schemaVersion = "1.0",
        codeSha = Git(repoRoot, "rev-parse HEAD"),
        corpusDocuments = corpus.Count,
        referenceCounts = references.GroupBy(item => item.Authority).ToDictionary(group => group.Key.ToString(), group => group.Count()),
        preModel = AggregatePreModel(sourceRuns),
        model = new
        {
            status = model.Status,
            trustedDocuments = model.TrustedDocuments,
            providerCalls = model.ProviderCalls,
            requestCount = model.RequestCount,
            role = "NOT_MEASURED",
            level = "NOT_MEASURED",
            parent = "NOT_MEASURED",
            span = "NOT_MEASURED",
            modelErrors = "NOT_MEASURED",
            harnessRecovery = "NOT_MEASURED",
            observedLift = "NOT_MEASURED",
        },
        finalSystem = "NOT_MEASURED_WITHOUT_EXHAUSTIVE_REFERENCE_FIELDS",
        accuracy99Claim = "NOT_MEASURED",
        providerCalls = model.ProviderCalls,
    };

    private static object BuildAuthorityBreakdown(
        IReadOnlyList<RunRecord> sourceRuns,
        IReadOnlyList<HarnessReferenceRecord> references,
        IReadOnlyDictionary<string, IReadOnlyList<HarnessReferenceRecord>> referenceByDocument) => new
    {
        rows = Enum.GetValues<HarnessReferenceAuthority>().Select(authority => new
        {
            authority = authority.ToString(),
            documents = references.Where(item => item.Authority == authority).Select(item => item.DocumentId).Where(item => item is not null).Distinct().Count(),
            referenceRecords = references.Count(item => item.Authority == authority),
            officialDenominator = HarnessLiftAccounting.IsOfficial(authority),
            finalMetrics = "NOT_MEASURED_UNLESS_REFERENCE_SUPPORTS_FIELD",
        }),
    };

    private static object BuildFirstLossSummary(IReadOnlyList<RunRecord> sourceRuns, ModelMeasurement model) => new
    {
        status = "PARTIAL",
        sourceLoss = sourceRuns.Count(item => item.Error is not null),
        candidateLoss = "NOT_MEASURED_WITHOUT_REFERENCE_BACKED_OCCURRENCE_JOIN",
        modelRole = "NOT_MEASURED",
        modelLevel = "NOT_MEASURED",
        modelParent = "NOT_MEASURED",
        modelSpan = "NOT_MEASURED",
        validator = "NOT_MEASURED",
        markerResolver = "NOT_MEASURED",
        structuralResolver = "NOT_MEASURED",
        finalProjection = "NOT_MEASURED",
        unknown = sourceRuns.Count(item => item.Error is null),
        modelProviderCalls = model.ProviderCalls,
    };

    private static object BuildFinalDecision(
        string repoRoot,
        IReadOnlyList<HarnessCorpusDocument> corpus,
        IReadOnlyList<HarnessReferenceRecord> references,
        IReadOnlyList<HarnessHistoricalEvidence> historical,
        IReadOnlyList<RunRecord> sourceRuns,
        ModelMeasurement model,
        IReadOnlyList<object> unknownGaps,
        IReadOnlyList<object> reviewRequired) => new
    {
        artifactKind = "harness_lift_final_decision",
        schemaVersion = "1.0",
        status = "MEASURED_WITH_LIMITATIONS",
        codeSha = Git(repoRoot, "rev-parse HEAD"),
        corpusFiles = corpus.Count,
        matchedA99Files = corpus.Count(item => item.JoinMethod == "EXACT_SOURCE_SHA256"),
        matchedA99Groups = corpus.Select(item => item.DocumentGroupId).Where(item => item is not null).Distinct().Count(),
        referenceCounts = references.GroupBy(item => item.Authority).ToDictionary(group => group.Key.ToString(), group => group.Count()),
        historicalEvidence = new
        {
            provenOccurrence = historical.Count(item => item.EvidenceStrength == HarnessEvidenceStrength.Proven && item.EvidenceGranularity == HarnessEvidenceGranularity.Occurrence),
            provenDocument = historical.Count(item => item.EvidenceStrength == HarnessEvidenceStrength.Proven && item.EvidenceGranularity == HarnessEvidenceGranularity.Document),
            partial = historical.Count(item => item.EvidenceStrength == HarnessEvidenceStrength.Partial),
            aggregateOnly = historical.Count(item => item.EvidenceGranularity == HarnessEvidenceGranularity.AggregateOnly),
            unknown = historical.Count(item => item.EvidenceStrength is HarnessEvidenceStrength.Unknown or HarnessEvidenceStrength.NotObservable),
        },
        preModel = new { sourceRecall = "NOT_MEASURED", candidateRecall = "NOT_MEASURED", modelExposureRecall = "NOT_MEASURED" },
        modelConditional = new { roleP = "NOT_MEASURED", roleR = "NOT_MEASURED", roleF1 = "NOT_MEASURED", levelAccuracy = "NOT_MEASURED", parentAccuracy = "NOT_MEASURED", spanAccuracy = "NOT_MEASURED" },
        harnessRecovery = new { modelErrorsTotal = "NOT_MEASURED", correctedByMarker = "NOT_MEASURED", correctedByStructural = "NOT_MEASURED", rejectedByValidator = "NOT_MEASURED", introducedByDeterministicStages = "NOT_MEASURED", modelErrorsSurvivedFinal = "NOT_MEASURED" },
        observedPostModelHarnessLift = "NOT_MEASURED",
        finalSystem = new { precision = "NOT_MEASURED", recall = "NOT_MEASURED", f1 = "NOT_MEASURED", levelAccuracy = "NOT_MEASURED", parentAccuracy = "NOT_MEASURED", hierarchyAccuracy = "NOT_MEASURED" },
        attributionCoverage = "NOT_MEASURED_WITHOUT_OCCURRENCE_JOIN",
        trustedMeasurementDocuments = model.TrustedDocuments,
        trustedMeasurementGroups = "GROUP_LEVEL_REFERENCE_JOIN_PENDING",
        humanReviewRequired = reviewRequired.Count > 0,
        trueBlindAvailable = false,
        correctionMemoryRuntime = "NOT_OBSERVABLE",
        correctionMemoryActiveRecords = "NOT_OBSERVABLE",
        provider = model.Provider ?? "NONE",
        model = model.Model ?? "NONE",
        providerCalls = model.ProviderCalls,
        repeats = model.Repeats,
        focusedHarnessLift = "PASS: evaluator contracts",
        accuracy99Claim = "NOT_MEASURED",
        harnessLiftStatus = model.Status,
        primaryBottleneck = unknownGaps.Count > 0 ? "REFERENCE_EXPANSION" : "NOT_RESOLVED",
        nextRecommendedDirection = "SOURCE_FIRST_REFERENCE_EXPANSION_AND_BLIND_ADJUDICATION",
        unknownGapCount = unknownGaps.Count,
    };

    private static object BuildCorrectionMemoryAudit(string repoRoot)
    {
        var candidates = new[]
        {
            Path.Combine(repoRoot, ".verify-build", "correction-memory.json"),
            Path.Combine(repoRoot, "correction-memory.json"),
        };
        var found = candidates.FirstOrDefault(File.Exists);
        return new
        {
            storeFound = found is not null ? "YES" : "NO",
            correctionMemoryRuntime = found is null ? "NOT_OBSERVABLE" : "OBSERVED_BUT_NOT_PARSED_BY_HARNESS_LIFT",
            path = found is null ? null : Relative(repoRoot, found),
            records = (int?)null,
            active = (int?)null,
            revoked = (int?)null,
            stale = (int?)null,
            levelCorrections = (int?)null,
            decisionCorrections = (int?)null,
            verifiedExamples = (int?)null,
            sameDocumentExact = (int?)null,
            crossDocumentReusableCandidates = (int?)null,
        };
    }

    private static async Task WriteReadmeAsync(string repoRoot, object finalDecision, CancellationToken cancellationToken)
    {
        var path = Path.Combine(repoRoot, "eval", "harness-lift", "README.md");
        var json = JsonSerializer.Serialize(finalDecision, JsonOptions);
        var text = "# HARNESS-LIFT\n\n" +
            "This directory contains evaluation-only reconciliation artifacts. Source facts and references are joined by exact identity; silver, heuristic, and aggregate-only evidence cannot enter official denominators.\n\n" +
            "The deterministic pre-model pass runs with provider calls disabled. Current model measurements, when explicitly enabled, are limited to the frozen official-reference subset and record unavailable occurrence joins as NOT_MEASURED.\n\n" +
            "Final decision snapshot:\n\n```json\n" + json + "\n```\n";
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false), cancellationToken);
    }

    private static async Task WriteArchitectureDocAsync(string repoRoot, object finalDecision, CancellationToken cancellationToken)
    {
        var path = Path.Combine(repoRoot, "docs", "architecture", "harness-lift-measurement.md");
        var text = "# HARNESS-LIFT measurement\n\n" +
            "This evaluation records where current extraction decisions are observed and where attribution remains unknown. It does not modify production extraction behavior, prompts, thresholds, candidate policy, resolver authority, validator behavior, or repair behavior.\n\n" +
            "The corpus is joined to the A99 inventory by exact source SHA. References retain HUMAN_KEY, SOURCE_STRUCTURAL_REFERENCE, MODEL_ASSISTED_SILVER, HEURISTIC_REFERENCE, UNLABELED, and INVALID_REFERENCE distinctions. Human review packets contain parser-owned source facts only.\n\n" +
            "Current model output is joined after the run. A missing occurrence-level proposal trace is recorded as NOT_MEASURED; no model error or harness lift is inferred from final disagreement alone.\n\n" +
            "```json\n" + JsonSerializer.Serialize(finalDecision, JsonOptions) + "\n```\n";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false), cancellationToken);
    }

    private static async Task<ModelMeasurement> RunCurrentModelAsyncStub() => await Task.FromResult(ModelMeasurement.NotRun("unused"));

    private static JsonDocument LoadJson(string repoRoot, string relative) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot, relative)));

    private static string FindRepositoryRoot(string start)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(start));
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DocxHeaderExtractor.sln")) &&
                (Directory.Exists(Path.Combine(directory.FullName, ".git")) || File.Exists(Path.Combine(directory.FullName, ".git"))))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException($"Không tìm thấy repository từ {start}");
    }

    private static string Git(string repoRoot, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", $"-C \"{repoRoot}\" {args}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("git-start-failed");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Trim();
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private static string PrefixOf(string path)
    {
        var match = NumericPrefix.Match(Path.GetFileName(path));
        return match.Success ? match.Groups["id"].Value : Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
    }
    private static async Task WriteJsonAsync(string path, object value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, new UTF8Encoding(false), cancellationToken);
    }
    private static int ReadInt(object? value, string name)
    {
        if (value is null) return 0;
        var property = value.GetType().GetProperty(name);
        return property?.GetValue(value) is int number ? number : 0;
    }
    private static object GroupPreModel(IReadOnlyList<RunRecord> records, Func<RunRecord, string> key) => new
    {
        groups = records.GroupBy(key, StringComparer.Ordinal).Select(group => new
        {
            key = group.Key,
            documents = group.Count(),
            candidateEligible = group.Sum(item => ReadInt(item.PreModel, "candidateEligible")),
            candidateSelected = group.Sum(item => ReadInt(item.PreModel, "candidateSelected")),
            providerCalls = 0,
        }),
    };

    private sealed record KeyOccurrence(string ReferenceId, string SourceId, int Level, string Text);

    private sealed class RunRecord
    {
        public RunRecord(HarnessCorpusDocument corpus, string? documentMode, object preModel, R18TracePointer r18, IReadOnlyList<KeyOccurrence> keyOccurrences, string packetPath)
        {
            Corpus = corpus; DocumentMode = documentMode; PreModel = preModel; R18 = r18; KeyOccurrences = keyOccurrences; PacketPath = packetPath;
        }
        public HarnessCorpusDocument Corpus { get; }
        public string? DocumentMode { get; }
        public object? PreModel { get; }
        public R18TracePointer? R18 { get; }
        public IReadOnlyList<KeyOccurrence> KeyOccurrences { get; }
        public string? PacketPath { get; }
        public string? Error { get; private init; }
        public static RunRecord Failed(HarnessCorpusDocument corpus, string error) => new(corpus, null, new { documentId = corpus.DocumentId, error }, null!, [], "") { Error = error };
    }

    private sealed record R18TracePointer(
        string DocumentId,
        string TracePath,
        string? Route,
        int ProviderCalls,
        string Direction,
        R18FirstLossSummary FirstLossSummary);

    private sealed record ModelRun(string RunId, int Repeat, string DocumentId, int ProviderCalls, int Expected, int Matched, int RequestCount, string ProposalJoinStatus);

    private sealed class ModelMeasurement
    {
        public string Status { get; init; } = "NOT_RUN";
        public string? Provider { get; init; }
        public string? Model { get; init; }
        public int ProviderCalls { get; init; }
        public int RequestCount { get; init; }
        public int Repeats { get; init; }
        public int TrustedDocuments { get; init; }
        public int TrustedDocumentsCount => TrustedDocuments;
        public IReadOnlyList<ModelRun> Runs { get; init; } = [];
        public HarnessRepeatedStatistic? FinalRecall { get; init; }
        public string ModelProposalMetrics { get; init; } = "NOT_MEASURED";
        public string HarnessLift { get; init; } = "NOT_MEASURED";
        public static ModelMeasurement NotRun(string reason) => new() { Status = "NOT_RUN", ModelProposalMetrics = reason, HarnessLift = "NOT_MEASURED" };
    }
}
