using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Eval.HarnessLift;

namespace DocxHeaderExtractor.Cli;

/// <summary>
/// HL3 is an evaluation-only reconciliation. It consumes the immutable HL2 trace, reruns only the
/// deterministic source route for namespace evidence, and deliberately never changes production
/// policy or calls an inference provider.
/// </summary>
internal static class HarnessLiftCausalReconciliationRunner
{
    private const string ExpectedBase = "fcf44b824fe51590f9c987d41b58d71cd4028341";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly string[] FrozenInputs =
    [
        "eval/harness-lift/final-decision.v2.json",
        "eval/harness-lift/reference-occurrence-bridge.v1.json",
        "eval/harness-lift/model-occurrence-bridge.v1.json",
        "eval/harness-lift/current-measurement-manifest.v2.json",
        "eval/harness-lift/harness-lift-runs.v2.json",
        "eval/harness-lift/harness-lift-by-field.v1.json",
        "eval/harness-lift/first-loss-summary.v2.json",
        "eval/harness-lift/pre-model-coverage.v2.json",
        "eval/harness-lift/source-structural-reference-expansion.v1.json",
        "eval/harness-lift/unknown-gap-manifest.v2.json",
        "eval/harness-lift/review-manifest.v2.json",
    ];

    public static async Task<int> RunAsync(CommandLineOptions options, CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot(options.HarnessLiftRoot ?? Directory.GetCurrentDirectory());
        var baseSha = Git(repoRoot, "rev-parse", "HEAD");
        if (!string.Equals(baseSha, ExpectedBase, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"HL3 requires base {ExpectedBase}, current is {baseSha}");

        var outputRoot = Path.Combine(repoRoot, "eval", "harness-lift");
        Directory.CreateDirectory(outputRoot);
        var frozenHashes = FrozenInputs.ToDictionary(path => path, path => Sha256(Path.Combine(repoRoot, path)), StringComparer.Ordinal);
        var joins = ReadJoins(repoRoot);
        var traces = ReadTraces(repoRoot);
        var trustedIds = ReadTrustedDocumentIds(repoRoot);
        var corpus = ReadCorpus(repoRoot);
        var live = await RunDeterministicAuditAsync(repoRoot, trustedIds, corpus, cancellationToken);
        var modelRun = ReadModelRuns(repoRoot);

        var uniqueTrustedReferences = joins
            .Where(row => trustedIds.Contains(row.DocumentId) && row.OfficialMetricEligible &&
                row.ResolvedSourceId is not null && row.ExpectedRole is not null)
            .GroupBy(row => Key(row.DocumentId, row.ResolvedSourceId!), StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(AuthorityRank).First())
            .OrderBy(row => row.DocumentId, StringComparer.Ordinal)
            .ThenBy(row => row.ResolvedOrdinal)
            .ThenBy(row => row.ResolvedSourceId, StringComparer.Ordinal)
            .ToArray();

        var namespaceCensus = BuildNamespaceCensus(live);
        var lineage = BuildOccurrenceLineage(uniqueTrustedReferences, live, traces);
        var routeOwnership = BuildRouteOwnership(uniqueTrustedReferences, live, traces, modelRun);
        var reconciliationRows = BuildCandidateLossReconciliation(uniqueTrustedReferences, live, traces);
        if (reconciliationRows.Count != 261)
            throw new InvalidDataException($"HL2 CandidateLoss reconciliation expected 261 rows, got {reconciliationRows.Count}");

        var modelExposure = BuildModelExposure(uniqueTrustedReferences, live, traces, modelRun);
        var finalLineage = BuildFinalLineage(uniqueTrustedReferences, live);
        var allocation = BuildDecisionAllocation(uniqueTrustedReferences, routeOwnership);
        var positiveRecall = BuildPositiveRecall(uniqueTrustedReferences, live, routeOwnership);
        var postModel = BuildPostModelRecovery(uniqueTrustedReferences, live, traces);
        var firstLoss = BuildFirstLossV3(uniqueTrustedReferences, live, traces, reconciliationRows);
        var historical = BuildHistoricalReconciliation(repoRoot, trustedIds, uniqueTrustedReferences);
        var review = BuildReviewPreservation(repoRoot);
        var semantics = BuildSemanticsAudit(baseSha, frozenHashes, traces, live, modelRun, review);
        var contribution = BuildContributionSummary(baseSha, allocation, modelExposure, postModel, positiveRecall, firstLoss);
        var finalDecision = BuildFinalDecision(baseSha, frozenHashes, joins, uniqueTrustedReferences, reconciliationRows,
            allocation, modelExposure, postModel, positiveRecall, firstLoss, historical, review, modelRun, live);

        await WriteJsonAsync(Path.Combine(outputRoot, "hl3-measurement-semantics-audit.v1.json"), semantics, cancellationToken);
        await WriteJsonAsync(Path.Combine(outputRoot, "trace-namespace-census.v1.json"), namespaceCensus, cancellationToken);
        await WriteJsonAsync(Path.Combine(outputRoot, "occurrence-lineage.v1.json"), new
        {
            artifactKind = "harness_lift_hl3_occurrence_lineage",
            schemaVersion = "3.0",
            baseRevision = baseSha,
            identityPolicy = new { allowed = new[] { "EXACT_SOURCE_ID", "EXACT_STABLE_ID", "EXACT_SOURCE_SPAN", "EXACT_PARSER_OWNED_LINEAGE", "EXPLICIT_ALIGNMENT", "EXPLICIT_BLOCK_SOURCE_REFERENCE" }, fuzzy = false },
            occurrenceCount = lineage.Count,
            occurrences = lineage,
        }, cancellationToken);
        await WriteJsonAsync(Path.Combine(outputRoot, "route-ownership.v1.json"), new
        {
            artifactKind = "harness_lift_hl3_route_ownership",
            schemaVersion = "3.0",
            baseRevision = baseSha,
            occurrenceCount = routeOwnership.Count,
            routes = routeOwnership,
        }, cancellationToken);
        await WriteJsonAsync(Path.Combine(outputRoot, "candidate-loss-reconciliation.v1.json"), new
        {
            artifactKind = "harness_lift_hl3_candidate_loss_reconciliation",
            schemaVersion = "3.0",
            baseRevision = baseSha,
            hl2ReportedCandidateLoss = 261,
            rows = reconciliationRows,
            dispositions = CountDispositions(reconciliationRows),
            total = reconciliationRows.Count,
        }, cancellationToken);
        await WriteJsonAsync(Path.Combine(outputRoot, "model-exposure-reconciliation.v1.json"), modelExposure, cancellationToken);
        await WriteJsonAsync(Path.Combine(outputRoot, "final-lineage-reconciliation.v1.json"), finalLineage, cancellationToken);
        await WriteJsonAsync(Path.Combine(outputRoot, "decision-allocation.v1.json"), allocation, cancellationToken);
        await WriteJsonAsync(Path.Combine(outputRoot, "positive-recall.v1.json"), positiveRecall, cancellationToken);
        await WriteJsonAsync(Path.Combine(outputRoot, "post-model-recovery.v1.json"), postModel, cancellationToken);
        await WriteJsonAsync(Path.Combine(outputRoot, "first-loss-summary.v3.json"), firstLoss, cancellationToken);
        await WriteJsonAsync(Path.Combine(outputRoot, "hl3-historical-reconciliation.v1.json"), historical, cancellationToken);
        await WriteJsonAsync(Path.Combine(outputRoot, "harness-contribution-summary.v3.json"), contribution, cancellationToken);
        await WriteJsonAsync(Path.Combine(outputRoot, "final-decision.v3.json"), finalDecision, cancellationToken);
        await WriteReadmeAsync(repoRoot, finalDecision, cancellationToken);
        await WriteArchitectureDocAsync(repoRoot, finalDecision, cancellationToken);

        Console.WriteLine($"HL3 trusted={uniqueTrustedReferences.Length} candidateLossRows={reconciliationRows.Count} providerCalls=0");
        Console.WriteLine($"HL3 dispositions={FormatCounts(reconciliationRows)}");
        return 0;
    }

    private static IReadOnlyList<HarnessOccurrenceJoinResult> ReadJoins(string repoRoot)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot, "eval/harness-lift/reference-occurrence-bridge.v1.json")));
        return json.RootElement.GetProperty("occurrences").EnumerateArray()
            .Select(item => item.Deserialize<HarnessOccurrenceJoinResult>(JsonOptions) ?? throw new InvalidDataException("join row missing"))
            .ToArray();
    }

    private static IReadOnlyList<HarnessModelOccurrenceTrace> ReadTraces(string repoRoot)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot, "eval/harness-lift/model-occurrence-bridge.v1.json")));
        return json.RootElement.GetProperty("traces").EnumerateArray()
            .Select(item => item.Deserialize<HarnessModelOccurrenceTrace>(JsonOptions) ?? throw new InvalidDataException("trace row missing"))
            .ToArray();
    }

    private static IReadOnlySet<string> ReadTrustedDocumentIds(string repoRoot)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot, "eval/harness-lift/current-measurement-manifest.v2.json")));
        return json.RootElement.GetProperty("documentIds").EnumerateArray().Select(item => item.GetString()!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, CorpusEntry> ReadCorpus(string repoRoot)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot, "eval/harness-lift/corpus-map.v1.json")));
        return json.RootElement.GetProperty("documents").EnumerateArray()
            .Select(item => new CorpusEntry(
                item.GetProperty("documentId").GetString()!,
                item.GetProperty("path").GetString()!))
            .ToDictionary(item => item.DocumentId, StringComparer.Ordinal);
    }

    private static async Task<IReadOnlyDictionary<string, LiveDocument>> RunDeterministicAuditAsync(
        string repoRoot,
        IReadOnlySet<string> trustedIds,
        IReadOnlyDictionary<string, CorpusEntry> corpus,
        CancellationToken cancellationToken)
    {
        var sourceReader = new OpenXmlDocumentSource();
        var pipelineOptions = new PipelineOptions { DisableLlm = true };
        using var pipeline = new AuthorityExtractionPipeline(pipelineOptions);
        var result = new Dictionary<string, LiveDocument>(StringComparer.Ordinal);
        foreach (var documentId in trustedIds.OrderBy(item => item, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!corpus.TryGetValue(documentId, out var entry))
                throw new InvalidDataException($"trusted document {documentId} missing from corpus map");
            var path = Path.Combine(repoRoot, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            var source = sourceReader.Read(path);
            var execution = await pipeline.RunDocumentExecutionAsync(path, null, cancellationToken);
            var audit = execution.CompatibilityOutline.RouteAudit;
            var candidateIds = audit?.CandidateBlocks.Select(item => item.Id).ToHashSet(StringComparer.Ordinal) ?? [];
            var selectedIds = audit?.SelectedCandidateBlocks.Select(item => item.Id).ToHashSet(StringComparer.Ordinal) ?? [];
            var decisionIds = audit?.BlockDecisions.Select(item => item.Id).ToHashSet(StringComparer.Ordinal) ?? [];
            var stageIds = audit?.CandidateStageTraces.Select(item => item.Id).ToHashSet(StringComparer.Ordinal) ?? [];
            var hierarchyIds = audit?.HierarchyFacts.Select(item => item.Id).ToHashSet(StringComparer.Ordinal) ?? [];
            var budgetIds = audit?.BudgetExcluded.Select(item => item.Id).ToHashSet(StringComparer.Ordinal) ?? [];
            var finalBySource = execution.Result.Structure.Elements
                .SelectMany(element => element.Sources.Select(reference => (reference.SourceId, Element: element)))
                .GroupBy(item => item.SourceId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Element, StringComparer.Ordinal);
            var compatibilityBySource = execution.CompatibilityOutline.Headings
                .SelectMany(heading => new[] { heading.SourceId, heading.StableId }.Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => (Value: value!, Heading: heading)))
                .GroupBy(item => item.Value, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Heading, StringComparer.Ordinal);
            result[documentId] = new LiveDocument(documentId, source, execution, audit, candidateIds, selectedIds,
                decisionIds, stageIds, hierarchyIds, budgetIds, finalBySource, compatibilityBySource);
        }
        return result;
    }

    private static IReadOnlyDictionary<string, ModelRunInfo> ReadModelRuns(string repoRoot)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot, "eval/harness-lift/harness-lift-runs.v2.json")));
        var result = new Dictionary<string, ModelRunInfo>(StringComparer.Ordinal);
        foreach (var item in json.RootElement.GetProperty("runs").EnumerateArray())
        {
            var doc = item.GetProperty("documentId").GetString()!;
            result[doc] = new ModelRunInfo(item.GetProperty("providerCalls").GetInt32() > 0,
                item.GetProperty("providerCalls").GetInt32());
        }
        return result;
    }

    private static object BuildSemanticsAudit(
        string baseSha,
        IReadOnlyDictionary<string, string> hashes,
        IReadOnlyList<HarnessModelOccurrenceTrace> traces,
        IReadOnlyDictionary<string, LiveDocument> live,
        IReadOnlyDictionary<string, ModelRunInfo> modelRuns,
        object review)
    {
        var assumptions = new[]
        {
            new { assumption = "CandidateBlocks.Id is the same namespace as SourceDocument.Paragraph.SourceId", evidence = "HL3 live namespace census compares exact sets; no string-shape inference", validity = "PARTIAL", impactOnHl2 = "candidate flags are not accepted as source-level loss without an explicit intersection" },
            new { assumption = "RawAnalystResponses.Count > 0 proves every occurrence was exposed", evidence = "HL2 model trace has 9 retained per-occurrence roles and many rows with no retained proposal", validity = "INVALID", impactOnHl2 = "document model execution cannot populate per-occurrence exposure" },
            new { assumption = "CandidateSelected=false is a candidate loss", evidence = "HL3 requires candidate-required route, proven construction and proven selection namespace", validity = "INVALID", impactOnHl2 = "261 historical rows require reclassification" },
            new { assumption = "final source identity must equal raw paragraph SourceId", evidence = "HL3 accepts explicit parser-owned source/span lineage and compatibility projection separately", validity = "PARTIAL", impactOnHl2 = "final absence is not inferred from an unproven identity equality" },
            new { assumption = "fuzzy text or nearest paragraph can close an occurrence", evidence = "occurrence identity contract and HL3 lineage policy forbid fuzzy joins", validity = "INVALID", impactOnHl2 = "unproven lineage remains UNKNOWN" },
        };
        return new
        {
            artifactKind = "harness_lift_hl3_measurement_semantics_audit",
            schemaVersion = "3.0",
            baseRevision = baseSha,
            frozenInputSha256 = hashes,
            providerCalls = 0,
            providerTraceReuse = "HL2_COMMITTED_TRACES_ONLY",
            trustedDocuments = live.Keys.OrderBy(item => item, StringComparer.Ordinal).ToArray(),
            modelRunOccurredInDocuments = modelRuns.Where(item => item.Value.Occurred).Select(item => item.Key).OrderBy(item => item, StringComparer.Ordinal).ToArray(),
            review,
            assumptions,
        };
    }

    private static object BuildNamespaceCensus(IReadOnlyDictionary<string, LiveDocument> live)
    {
        var all = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var byDocument = new List<object>();
        foreach (var item in live.Values.OrderBy(item => item.DocumentId, StringComparer.Ordinal))
        {
            var sets = NamespaceSets(item);
            foreach (var pair in sets)
            {
                if (!all.TryGetValue(pair.Key, out var values)) all[pair.Key] = values = new(StringComparer.Ordinal);
                values.UnionWith(pair.Value);
            }
            byDocument.Add(new { documentId = item.DocumentId, namespaces = sets.ToDictionary(pair => pair.Key, pair => pair.Value.Count), comparisons = CompareNamespaces(sets) });
        }
        return new
        {
            artifactKind = "harness_lift_hl3_trace_namespace_census",
            schemaVersion = "3.0",
            identityPolicy = "exact intersections are descriptive; similar strings are not equivalence",
            aggregate = new { namespaces = all.ToDictionary(pair => pair.Key, pair => pair.Value.Count), comparisons = CompareNamespaces(all) },
            documents = byDocument,
        };
    }

    private static Dictionary<string, HashSet<string>> NamespaceSets(LiveDocument item) => new(StringComparer.Ordinal)
    {
        ["SOURCE_SOURCE_IDS"] = item.Source.Paragraphs.Select(paragraph => paragraph.SourceId).ToHashSet(StringComparer.Ordinal),
        ["SOURCE_STABLE_IDS"] = item.Source.Paragraphs.Select(paragraph => paragraph.StableId).ToHashSet(StringComparer.Ordinal),
        ["CANDIDATE_BLOCK_IDS"] = item.CandidateIds,
        ["SELECTED_CANDIDATE_IDS"] = item.SelectedIds,
        ["BLOCK_DECISION_IDS"] = item.DecisionIds,
        ["CANDIDATE_STAGE_IDS"] = item.StageIds,
        ["HIERARCHY_FACT_IDS"] = item.HierarchyIds,
        ["FINAL_SOURCE_IDS"] = item.FinalBySource.Keys.ToHashSet(StringComparer.Ordinal),
        ["FINAL_ELEMENT_IDS"] = item.Execution.Result.Structure.Elements.Select(element => element.Id).ToHashSet(StringComparer.Ordinal),
        ["COMPATIBILITY_HEADING_IDS"] = item.Execution.CompatibilityOutline.Headings
            .SelectMany(heading => new[] { heading.SourceId, heading.StableId }.Where(value => !string.IsNullOrWhiteSpace(value)))
            .Cast<string>().ToHashSet(StringComparer.Ordinal),
    };

    private static IReadOnlyList<HarnessHl3NamespaceComparison> CompareNamespaces(IReadOnlyDictionary<string, HashSet<string>> sets)
    {
        var names = sets.Keys.OrderBy(item => item, StringComparer.Ordinal).ToArray();
        var result = new List<HarnessHl3NamespaceComparison>();
        for (var left = 0; left < names.Length; left++)
        for (var right = left + 1; right < names.Length; right++)
            result.Add(HarnessLiftCausalAccounting.Compare(names[left], sets[names[left]], names[right], sets[names[right]]));
        return result;
    }

    private static IReadOnlyList<object> BuildOccurrenceLineage(
        IReadOnlyList<HarnessOccurrenceJoinResult> references,
        IReadOnlyDictionary<string, LiveDocument> live,
        IReadOnlyList<HarnessModelOccurrenceTrace> traces)
    {
        var result = new List<object>();
        foreach (var reference in references)
        {
            var item = live[reference.DocumentId];
            var source = item.Source.Paragraphs.FirstOrDefault(paragraph => paragraph.SourceId == reference.ResolvedSourceId);
            var hasCandidate = reference.ResolvedSourceId is not null && item.CandidateIds.Contains(reference.ResolvedSourceId);
            var hasSelected = reference.ResolvedSourceId is not null && item.SelectedIds.Contains(reference.ResolvedSourceId);
            var hasFinal = reference.ResolvedSourceId is not null && item.FinalBySource.ContainsKey(reference.ResolvedSourceId);
            var relevantTraces = traces.Where(trace => trace.DocumentId == reference.DocumentId && trace.SourceId == reference.ResolvedSourceId).ToArray();
            result.Add(new
            {
                referenceId = reference.ReferenceId,
                documentId = reference.DocumentId,
                referenceIdentity = reference.ReferenceOccurrenceIdentity,
                source = source is null
                    ? new { status = "UNKNOWN", sourceId = (string?)null, sourceOrdinal = (int?)null, span = (HarnessSpan?)null }
                    : new { status = "EXACT_SOURCE_ID", sourceId = (string?)source.SourceId, sourceOrdinal = (int?)source.SourceOrdinal, span = (HarnessSpan?)new HarnessSpan(0, source.Text.Length) },
                representations = new
                {
                    candidate = hasCandidate ? new { status = "EXACT_SOURCE_ID", representationId = reference.ResolvedSourceId } : new { status = "UNKNOWN", representationId = (string?)null },
                    selected = hasSelected ? new { status = "EXACT_SOURCE_ID", representationId = reference.ResolvedSourceId } : new { status = "UNKNOWN", representationId = (string?)null },
                },
                model = relevantTraces.Select(trace => new
                {
                    trace.RunId,
                    trace.Repeat,
                    status = trace.ModelRole is not null ? "RETAINED_PER_OCCURRENCE_DECISION" : "REQUEST_MEMBERSHIP_NOT_RETAINED",
                    modelRole = trace.ModelRole,
                }).ToArray(),
                final = hasFinal ? new { status = "EXACT_SOURCE_ID", sourceId = reference.ResolvedSourceId } : new { status = "UNKNOWN", sourceId = (string?)null },
                forbiddenJoinUsed = false,
            });
        }
        return result;
    }

    private static IReadOnlyList<RouteOwnershipRow> BuildRouteOwnership(
        IReadOnlyList<HarnessOccurrenceJoinResult> references,
        IReadOnlyDictionary<string, LiveDocument> live,
        IReadOnlyList<HarnessModelOccurrenceTrace> traces,
        IReadOnlyDictionary<string, ModelRunInfo> modelRuns)
    {
        return references.Select(reference =>
        {
            var item = live[reference.DocumentId];
            var source = item.Source.Paragraphs.First(paragraph => paragraph.SourceId == reference.ResolvedSourceId);
            var model = traces.Where(trace => trace.DocumentId == reference.DocumentId && trace.SourceId == reference.ResolvedSourceId).ToArray();
            var modelExposed = model.Any(trace => trace.ModelRole is not null);
            var deterministic = item.FinalBySource.ContainsKey(source.SourceId) || item.HierarchyIds.Contains(source.SourceId);
            var finalCorrect = item.FinalBySource.TryGetValue(source.SourceId, out var element) && IsFinalCorrect(element, reference);
            var route = deterministic && modelExposed ? HarnessHl3RouteOwner.MixedModelPlusDeterministic :
                modelExposed ? HarnessHl3RouteOwner.SemanticModelRoute :
                deterministic && item.HierarchyIds.Contains(source.SourceId) ? HarnessHl3RouteOwner.DeterministicMarkerRoute :
                deterministic && source.Numbering.NumberingId is not null ? HarnessHl3RouteOwner.DeterministicNumberingRoute :
                deterministic ? HarnessHl3RouteOwner.DeterministicSourceRoute :
                modelRuns.TryGetValue(reference.DocumentId, out var run) && run.Occurred ? HarnessHl3RouteOwner.RouteNotObservable :
                HarnessHl3RouteOwner.RouteNotObservable;
            return new RouteOwnershipRow
            {
                ReferenceId = reference.ReferenceId,
                DocumentId = reference.DocumentId,
                SourceId = source.SourceId,
                RouteOwner = route,
                ModelRequired = route is HarnessHl3RouteOwner.SemanticModelRoute or HarnessHl3RouteOwner.MixedModelPlusDeterministic,
                ModelActuallyExposed = modelExposed ? "PROVEN" : modelRuns.TryGetValue(reference.DocumentId, out var occurred) && occurred.Occurred ? "NOT_OBSERVABLE" : "NOT_APPLICABLE",
                DeterministicAuthorityUsed = deterministic,
                DeterministicCorrect = deterministic && finalCorrect,
                DeterministicWrong = deterministic && !finalCorrect,
            };
        }).ToArray();
    }

    private static IReadOnlyList<LossRow> BuildCandidateLossReconciliation(
        IReadOnlyList<HarnessOccurrenceJoinResult> references,
        IReadOnlyDictionary<string, LiveDocument> live,
        IReadOnlyList<HarnessModelOccurrenceTrace> traces)
    {
        var expected = references.GroupBy(reference => Key(reference.DocumentId, reference.ResolvedSourceId!), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var rows = new List<LossRow>();
        foreach (var trace in traces.Where(trace => trace.CandidateSelected != true && expected.ContainsKey(Key(trace.DocumentId, trace.SourceId))))
        {
            var reference = expected[Key(trace.DocumentId, trace.SourceId)];
            var item = live[trace.DocumentId];
            var final = item.FinalBySource.TryGetValue(trace.SourceId, out var element);
            var candidateConstructed = item.CandidateIds.Contains(trace.SourceId) || item.StageIds.Contains(trace.SourceId) || item.DecisionIds.Contains(trace.SourceId);
            var candidateSelected = item.SelectedIds.Contains(trace.SourceId);
            var candidateRequired = item.Audit is not null && item.CandidateIds.Count > 0 && !item.HierarchyIds.Contains(trace.SourceId) && !final;
            var deterministic = final && item.Audit?.RawAnalystResponses.Count == 0;
            var finalCorrect = final && IsFinalCorrect(element!, reference);
            var input = new HarnessHl3LossClassificationInput(
                SourceProven: true,
                RepresentationProven: candidateConstructed,
                DeterministicRouteProven: deterministic,
                DeterministicCorrect: finalCorrect,
                DeterministicWrong: deterministic && !finalCorrect,
                CandidateRequired: candidateRequired,
                CandidateConstructedProven: candidateConstructed,
                CandidateSelectedProven: candidateSelected,
                RankingBudgetProven: item.BudgetIds.Contains(trace.SourceId),
                ModelExposureProven: trace.ModelRole is not null,
                ModelProposalWrong: trace.ModelRole is not null && !RoleMatches(trace.ModelRole, reference.ExpectedRole),
                ProposalValidationRejected: trace.ValidationStatus is "rejected" or "invalid",
                MarkerResolutionError: false,
                StructuralResolutionError: false,
                FinalSourceLineageMismatch: item.CompatibilityBySource.ContainsKey(trace.SourceId) && !final,
                FinalProjectionError: false);
            var disposition = HarnessLiftCausalAccounting.ClassifyCandidateLoss(input);
            rows.Add(new LossRow
            {
                RunId = trace.RunId,
                Repeat = trace.Repeat,
                DocumentId = trace.DocumentId,
                SourceId = trace.SourceId,
                ReferenceId = reference.ReferenceId,
                Hl2FirstLoss = "CandidateLoss",
                Disposition = disposition,
                Evidence = new
                {
                    sourceProven = true,
                    representationProven = candidateConstructed,
                    candidateRepresentationIds = item.CandidateIds.Where(id => id == trace.SourceId).ToArray(),
                    candidateRequired,
                    candidateConstructedProven = candidateConstructed,
                    candidateSelectedProven = candidateSelected,
                    rankingBudgetProven = item.BudgetIds.Contains(trace.SourceId),
                    deterministicRouteProven = deterministic,
                    deterministicCorrect = finalCorrect,
                    modelExposure = trace.ModelRole is not null ? "PROVEN" : "NOT_OBSERVABLE",
                    finalSourceLineage = final ? "EXACT_SOURCE_ID" : "UNKNOWN",
                },
            });
        }
        return rows;
    }

    private static object BuildModelExposure(
        IReadOnlyList<HarnessOccurrenceJoinResult> references,
        IReadOnlyDictionary<string, LiveDocument> live,
        IReadOnlyList<HarnessModelOccurrenceTrace> traces,
        IReadOnlyDictionary<string, ModelRunInfo> modelRuns)
    {
        var rows = references.Select(reference =>
        {
            var occurrence = traces.Where(trace => trace.DocumentId == reference.DocumentId && trace.SourceId == reference.ResolvedSourceId).ToArray();
            var run = modelRuns.TryGetValue(reference.DocumentId, out var value) && value.Occurred;
            var retained = occurrence.Any(trace => trace.ModelRole is not null);
            var status = HarnessLiftCausalAccounting.ModelExposure(run, retained, !run);
            return new
            {
                referenceId = reference.ReferenceId,
                documentId = reference.DocumentId,
                sourceId = reference.ResolvedSourceId,
                status,
                modelRunOccurredInDocument = run,
                modelOccurrenceSelected = retained ? "PROVEN_BY_RETAINED_DECISION" : "NOT_OBSERVABLE",
                modelOccurrenceRequested = "NOT_OBSERVABLE",
                modelOccurrenceDecisionRetained = retained,
            };
        }).ToArray();
        return new
        {
            artifactKind = "harness_lift_hl3_model_exposure_reconciliation",
            schemaVersion = "3.0",
            providerCalls = 0,
            rows,
            totals = new
            {
                referenceOccurrences = rows.Length,
                modelRunOccurredInDocument = rows.Count(row => row.modelRunOccurredInDocument),
                modelExposureProven = rows.Count(row => row.modelOccurrenceDecisionRetained),
                modelExposureUnknown = rows.Count(row => row.status == HarnessHl3ModelExposureStatus.ModelOccurrenceExposureUnknown),
            },
        };
    }

    private static object BuildFinalLineage(IReadOnlyList<HarnessOccurrenceJoinResult> references, IReadOnlyDictionary<string, LiveDocument> live)
    {
        var rows = references.Select(reference =>
        {
            var item = live[reference.DocumentId];
            ValidatedStructuralElement? element = null;
            HeadingRecord? heading = null;
            var present = reference.ResolvedSourceId is not null && item.FinalBySource.TryGetValue(reference.ResolvedSourceId, out element);
            var compatibility = reference.ResolvedSourceId is not null && item.CompatibilityBySource.TryGetValue(reference.ResolvedSourceId, out heading);
            var status = present ? IsFinalCorrect(element!, reference) ? "FINAL_PRESENT_CORRECT" : "FINAL_PRESENT_WRONG_FIELD" : "FINAL_ABSENT";
            return new
            {
                referenceId = reference.ReferenceId,
                documentId = reference.DocumentId,
                sourceId = reference.ResolvedSourceId,
                status,
                finalStructureSourceId = present ? reference.ResolvedSourceId : null,
                compatibilityHeadingPresent = compatibility,
                compatibilityHeadingIdentity = compatibility ? heading!.StableId ?? heading.SourceId : null,
                lineageStatus = present || compatibility ? "EXPLICIT_PARSER_OR_COMPATIBILITY_LINEAGE" : "UNKNOWN",
                countedWrong = status == "FINAL_PRESENT_WRONG_FIELD",
            };
        }).ToArray();
        return new
        {
            artifactKind = "harness_lift_hl3_final_lineage_reconciliation",
            schemaVersion = "3.0",
            rows,
            totals = new
            {
                presentCorrect = rows.Count(row => row.status == "FINAL_PRESENT_CORRECT"),
                absent = rows.Count(row => row.status == "FINAL_ABSENT"),
                presentWrongField = rows.Count(row => row.status == "FINAL_PRESENT_WRONG_FIELD"),
                lineageUnknown = rows.Count(row => row.lineageStatus == "UNKNOWN"),
            },
        };
    }

    private static object BuildDecisionAllocation(
        IReadOnlyList<HarnessOccurrenceJoinResult> references,
        IReadOnlyList<RouteOwnershipRow> routeRows)
    {
        var routes = routeRows.ToArray();
        var deterministic = routes.Count(row => IsDeterministicRoute(row.RouteOwner));
        var modelRequired = routes.Count(row => row.ModelRequired);
        var mixed = routes.Count(row => row.RouteOwner == HarnessHl3RouteOwner.MixedModelPlusDeterministic);
        var unknown = routes.Length - deterministic - modelRequired;
        return new
        {
            artifactKind = "harness_lift_hl3_decision_allocation",
            schemaVersion = "3.0",
            referencePositives = references.Count,
            deterministicRoute = deterministic,
            modelRequiredRoute = modelRequired,
            mixedRoute = mixed,
            routeUnknown = unknown,
            deterministicHandled = routes.Count(row => row.DeterministicAuthorityUsed),
            deterministicCorrect = routes.Count(row => row.DeterministicCorrect),
            deterministicWrong = routes.Count(row => row.DeterministicWrong),
            modelExposed = routes.Count(row => row.ModelActuallyExposed == "PROVEN"),
            finalCorrect = routes.Count(row => row.DeterministicCorrect),
            finalWrong = routes.Count(row => row.DeterministicWrong),
            finalUnknown = routes.Count(row => !row.DeterministicCorrect && !row.DeterministicWrong),
            note = "counts use unique trusted official source occurrences; route ownership is evidence-based and unknown is retained",
        };
    }

    private static object BuildPositiveRecall(
        IReadOnlyList<HarnessOccurrenceJoinResult> references,
        IReadOnlyDictionary<string, LiveDocument> live,
        IReadOnlyList<RouteOwnershipRow> routes)
    {
        var joined = references.Count(reference => reference.ResolvedSourceId is not null);
        var sourceRecall = Ratio(joined, references.Count);
        var represented = references.Count(reference => live[reference.DocumentId].Source.Paragraphs.Any(paragraph => paragraph.SourceId == reference.ResolvedSourceId));
        var candidateRoutes = routes.Where(row => row.ModelRequired).ToArray();
        var modelRoutes = routes.Where(row => row.ModelRequired).ToArray();
        var finalObserved = routes.Where(row => live[row.DocumentId].FinalBySource.ContainsKey(row.SourceId)).ToArray();
        return new
        {
            artifactKind = "harness_lift_hl3_positive_recall",
            schemaVersion = "3.0",
            source = new { status = "POSITIVE_OCCURRENCE_RECALL", numerator = joined, denominator = references.Count, recall = sourceRecall },
            representation = new { status = "POSITIVE_OCCURRENCE_RECALL", numerator = represented, denominator = references.Count, recall = Ratio(represented, references.Count) },
            candidateRequiredRoute = new
            {
                status = candidateRoutes.Length == 0 ? "NOT_MEASURED_NO_PROVEN_CANDIDATE_REQUIRED_ROUTE" : "POSITIVE_OCCURRENCE_TRACE_ONLY",
                numerator = candidateRoutes.Count(row => live[row.DocumentId].CandidateIds.Contains(row.SourceId)),
                denominator = candidateRoutes.Length,
                recall = Ratio(candidateRoutes.Count(row => live[row.DocumentId].CandidateIds.Contains(row.SourceId)), candidateRoutes.Length),
            },
            modelRequiredRouteExposure = new { status = "POSITIVE_OCCURRENCE_TRACE_ONLY", numerator = modelRoutes.Count(row => row.ModelActuallyExposed == "PROVEN"), denominator = modelRoutes.Length, recall = Ratio(modelRoutes.Count(row => row.ModelActuallyExposed == "PROVEN"), modelRoutes.Length) },
            final = new
            {
                status = finalObserved.Length == 0 ? "NOT_MEASURED_FINAL_LINEAGE_UNKNOWN" : "POSITIVE_OCCURRENCE_RECALL_WITH_LINEAGE_LIMITS",
                numerator = finalObserved.Count(row => IsFinalCorrect(live[row.DocumentId].FinalBySource[row.SourceId], references.First(reference => reference.ReferenceId == row.ReferenceId))),
                denominator = finalObserved.Length,
                recall = Ratio(finalObserved.Count(row => IsFinalCorrect(live[row.DocumentId].FinalBySource[row.SourceId], references.First(reference => reference.ReferenceId == row.ReferenceId))), finalObserved.Length),
            },
            precision = "NOT_MEASURED",
            f1 = "NOT_MEASURED",
            reason = "official trusted references are positive-only; no exhaustive negative denominator",
        };
    }

    private static object BuildPostModelRecovery(
        IReadOnlyList<HarnessOccurrenceJoinResult> references,
        IReadOnlyDictionary<string, LiveDocument> live,
        IReadOnlyList<HarnessModelOccurrenceTrace> traces)
    {
        var rows = references.Select(reference =>
        {
            var trace = traces.Where(item => item.DocumentId == reference.DocumentId && item.SourceId == reference.ResolvedSourceId && item.ModelRole is not null)
                .OrderBy(item => item.Repeat).FirstOrDefault();
            var final = live[reference.DocumentId].FinalBySource.TryGetValue(reference.ResolvedSourceId!, out var element);
            var modelCorrect = trace is not null && RoleMatches(trace.ModelRole, reference.ExpectedRole);
            var finalCorrect = final && IsFinalCorrect(element!, reference);
            return new { referenceId = reference.ReferenceId, modelExposed = trace is not null, modelCorrect, finalCorrect, lift = trace is null ? (bool?)null : finalCorrect && !modelCorrect };
        }).Where(row => row.modelExposed).ToArray();
        var modelCorrect = rows.Count(row => row.modelCorrect);
        var finalCorrect = rows.Count(row => row.finalCorrect);
        return new
        {
            artifactKind = "harness_lift_hl3_post_model_recovery",
            schemaVersion = "3.0",
            sameExactModelExposedPopulation = true,
            modelExposed = rows.Length,
            modelCorrect,
            finalCorrect,
            modelProposalCorrectRate = Ratio(modelCorrect, rows.Length),
            finalCorrectRateSamePopulation = Ratio(finalCorrect, rows.Length),
            observedPostModelHarnessLift = rows.Length == 0 ? null : $"{finalCorrect - modelCorrect}/{rows.Length}",
            deterministicAutomationExcluded = true,
            modelNotExposedExcluded = true,
        };
    }

    private static object BuildFirstLossV3(
        IReadOnlyList<HarnessOccurrenceJoinResult> references,
        IReadOnlyDictionary<string, LiveDocument> live,
        IReadOnlyList<HarnessModelOccurrenceTrace> traces,
        IReadOnlyList<LossRow> reconciliationRows)
    {
        var expected = references.ToDictionary(reference => Key(reference.DocumentId, reference.ResolvedSourceId!), StringComparer.Ordinal);
        var stages = new List<HarnessHl3FirstLossStage>();
        foreach (var trace in traces.Where(trace => expected.ContainsKey(Key(trace.DocumentId, trace.SourceId))))
        {
            var reference = expected[Key(trace.DocumentId, trace.SourceId)];
            var item = live[trace.DocumentId];
            var final = item.FinalBySource.TryGetValue(trace.SourceId, out var element);
            var candidateConstructed = item.CandidateIds.Contains(trace.SourceId) || item.StageIds.Contains(trace.SourceId) || item.DecisionIds.Contains(trace.SourceId);
            var candidateSelected = item.SelectedIds.Contains(trace.SourceId);
            var candidateRequired = item.Audit is not null && item.CandidateIds.Count > 0 && !item.HierarchyIds.Contains(trace.SourceId) && !final;
            var deterministic = final && item.Audit?.RawAnalystResponses.Count == 0;
            var finalCorrect = final && IsFinalCorrect(element!, reference);
            stages.Add(HarnessLiftCausalAccounting.ChooseFirstLoss(new HarnessHl3FirstLossInput(
                true, candidateConstructed, deterministic, finalCorrect, deterministic && !finalCorrect,
                candidateRequired, candidateConstructed, candidateSelected, item.BudgetIds.Contains(trace.SourceId),
                trace.ModelRole is not null, trace.ModelRole is not null && !RoleMatches(trace.ModelRole, reference.ExpectedRole),
                trace.ValidationStatus is "rejected" or "invalid", false, false, false)));
        }
        var byStage = Enum.GetValues<HarnessHl3FirstLossStage>().ToDictionary(stage => stage.ToString(), stage => stages.Count(item => item == stage), StringComparer.Ordinal);
        var trueLoss = reconciliationRows.Count(row => row.Disposition is HarnessHl3CandidateLossDisposition.TrueCandidateNotConstructed or HarnessHl3CandidateLossDisposition.TrueCandidateNotSelected or HarnessHl3CandidateLossDisposition.TrueRankingBudgetLoss);
        return new
        {
            artifactKind = "harness_lift_hl3_first_loss_summary",
            schemaVersion = "3.0",
            referencePositiveOccurrences = references.Count,
            observedRows = stages.Count,
            byFirstLoss = byStage,
            hl2CandidateLoss = 261,
            truePreModelLoss = trueLoss,
            routeAwareRule = "deterministic route is evaluated before candidate/model stages; unknown is never coerced into loss",
        };
    }

    private static object BuildHistoricalReconciliation(string repoRoot, IReadOnlySet<string> trustedIds, IReadOnlyList<HarnessOccurrenceJoinResult> current)
    {
        var path = Path.Combine(repoRoot, "eval/harness-lift/historical-evidence-ledger.v1.json");
        if (!File.Exists(path)) return new { artifactKind = "harness_lift_hl3_historical_reconciliation", schemaVersion = "3.0", rows = Array.Empty<object>(), note = "historical ledger unavailable; no identities recreated" };
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var rows = json.RootElement.GetProperty("evidence").EnumerateArray().Select(item => new
        {
            evidenceId = GetString(item, "evidenceId"),
            documentId = GetString(item, "documentId"),
            sourceArtifact = GetString(item, "sourceArtifact"),
            sourceCommit = GetString(item, "sourceCommit"),
            occurrenceIdentityPresent = item.TryGetProperty("occurrenceIdentity", out var identity) && identity.ValueKind == JsonValueKind.Object,
            trustedDocument = GetString(item, "documentId") is { } id && trustedIds.Contains(id),
            reusableForCurrentAttribution = GetString(item, "reusableForCurrentAttribution") ?? "NO",
            currentJoin = GetString(item, "documentId") is { } currentId && trustedIds.Contains(currentId) ? "DOCUMENT_JOINED_REQUIRES_EXACT_OCCURRENCE_REVIEW" : "UNJOINED",
            identityRecreated = false,
        }).ToArray();
        return new
        {
            artifactKind = "harness_lift_hl3_historical_reconciliation",
            schemaVersion = "3.0",
            currentTrustedOccurrenceCount = current.Count,
            rows,
            policy = "historical aggregate evidence never creates current occurrence identities",
        };
    }

    private static object BuildReviewPreservation(string repoRoot)
    {
        var manifestPath = Path.Combine(repoRoot, "eval/harness-lift/review-manifest.v2.json");
        var packetRoot = Path.Combine(repoRoot, "eval/harness-lift/review-packets-v2");
        var packetHashes = Directory.Exists(packetRoot)
            ? Directory.EnumerateFiles(packetRoot, "*.json", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(path => Path.GetRelativePath(repoRoot, path).Replace('\\', '/'), Sha256, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        return new
        {
            packetCount = manifest.RootElement.GetProperty("packetCount").GetInt32(),
            packetHashes,
            humanReviewQueuePreserved = true,
            newHumanKeys = 0,
            newHumanGold = 0,
            newHoldoutLabels = 0,
            newReservedLabels = 0,
            adjudication = "HL3 measurement only; no Codex/LLM/human adjudication",
        };
    }

    private static object BuildContributionSummary(
        string baseSha,
        object allocation,
        object modelExposure,
        object postModel,
        object recall,
        object firstLoss) => new
        {
            artifactKind = "harness_lift_contribution_summary",
            schemaVersion = "3.0",
            baseRevision = baseSha,
            decisionAllocation = allocation,
            modelExposure,
            postModelRecovery = postModel,
            positiveRecall = recall,
            firstLoss,
            status = "MEASURED_WITH_LIMITATIONS",
            interpretation = "deterministic automation, model-conditional quality, and post-model recovery use separate populations",
            accuracy99Claim = "NOT_MEASURED",
        };

    private static object BuildFinalDecision(
        string baseSha,
        IReadOnlyDictionary<string, string> hashes,
        IReadOnlyList<HarnessOccurrenceJoinResult> joins,
        IReadOnlyList<HarnessOccurrenceJoinResult> uniqueReferences,
        IReadOnlyList<LossRow> reconciliation,
        object allocation,
        object exposure,
        object postModel,
        object recall,
        object firstLoss,
        object historical,
        object review,
        IReadOnlyDictionary<string, ModelRunInfo> modelRuns,
        IReadOnlyDictionary<string, LiveDocument> live)
    {
        var counts = CountDispositions(reconciliation);
        var reclassified = 261 - counts.TrueCandidateNotConstructed - counts.TrueCandidateNotSelected - counts.TrueRankingBudgetLoss;
        var measurementUnknown = counts.RepresentationMismatch + counts.TraceNotObservable;
        var primary = measurementUnknown > 261 / 2 ? "MEASUREMENT_OBSERVABILITY" :
            counts.TrueCandidateNotConstructed + counts.TrueCandidateNotSelected + counts.TrueRankingBudgetLoss > 261 / 2 ? "PRE_MODEL_PIPELINE" :
            "NOT_RESOLVED";
        return new
        {
            artifactKind = "harness_lift_final_decision",
            schemaVersion = "3.0",
            status = "MEASURED_WITH_LIMITATIONS",
            baseRevision = baseSha,
            frozenInputSha256 = hashes,
            hl2Baseline = new { referencePositives = 1098, runObservations = 264, candidateLoss = 261, modelRole = 3, providerCalls = 314 },
            hl3 = new { trustedUniqueOfficialOccurrences = uniqueReferences.Count, allOccurrenceBridgeRows = joins.Count, providerCalls = 0, reusedHl2Traces = true, rerunRequired = false },
            candidateLoss = new { hl2Reported = 261, hl3TrueCandidateLoss = counts.TrueCandidateNotConstructed + counts.TrueCandidateNotSelected + counts.TrueRankingBudgetLoss, reclassified = reclassified, dispositions = counts, total = reconciliation.Count },
            decisionAllocation = allocation,
            modelExposure = exposure,
            postModelRecovery = postModel,
            positiveRecall = recall,
            firstLossV3 = firstLoss,
            historical,
            humanReview = review,
            newHumanKeys = 0,
            newHumanGold = 0,
            newHoldoutLabels = 0,
            newReservedLabels = 0,
            accuracy99Claim = "NOT_MEASURED",
            harnessContributionStatus = "MEASURED_WITH_LIMITATIONS",
            primaryBottleneck = primary,
            nextRecommendedDirection = primary == "MEASUREMENT_OBSERVABILITY" ? "preserve per-occurrence candidate/request trace before any production policy change" : "causal remediation only after measurement proof",
            provider = new { reusedHl2Traces = true, rerunRequired = false, newProviderCalls = 0, model = "qwen/qwen3.5-9b", repeats = 3 },
            productionSourceDelta = 0,
            noN15Rebaseline = true,
            frozenHumanReviewQueue = true,
        };
    }

    private static DispositionCounts CountDispositions(IReadOnlyList<LossRow> rows)
    {
        var values = rows.Select(row => row.Disposition).ToArray();
        return new(
            values.Count(value => value == HarnessHl3CandidateLossDisposition.TrueCandidateNotConstructed),
            values.Count(value => value == HarnessHl3CandidateLossDisposition.TrueCandidateNotSelected),
            values.Count(value => value == HarnessHl3CandidateLossDisposition.TrueRankingBudgetLoss),
            values.Count(value => value == HarnessHl3CandidateLossDisposition.DeterministicBypassCorrect),
            values.Count(value => value == HarnessHl3CandidateLossDisposition.DeterministicBypassWrong),
            values.Count(value => value == HarnessHl3CandidateLossDisposition.RepresentationMismatch),
            values.Count(value => value == HarnessHl3CandidateLossDisposition.FinalSourceLineageMismatch),
            values.Count(value => value == HarnessHl3CandidateLossDisposition.TraceNotObservable),
            values.Count(value => value == HarnessHl3CandidateLossDisposition.OtherProvenStage));
    }

    private static string FormatCounts(IReadOnlyList<LossRow> rows) => string.Join(", ", CountDispositions(rows).AsPairs().Select(pair => $"{pair.Key}={pair.Value}"));

    private static bool IsDeterministicRoute(HarnessHl3RouteOwner route) => route is HarnessHl3RouteOwner.DeterministicSourceRoute or HarnessHl3RouteOwner.DeterministicMarkerRoute or HarnessHl3RouteOwner.DeterministicNumberingRoute or HarnessHl3RouteOwner.DeterministicPdfFallback;

    private static bool IsFinalCorrect(ValidatedStructuralElement element, HarnessOccurrenceJoinResult reference)
    {
        var roleCorrect = reference.ExpectedRole is null || RoleMatches(element.Role.ToString(), reference.ExpectedRole) || element.Type is StructuralElementType.Title or StructuralElementType.Subtitle or StructuralElementType.Heading;
        var levelCorrect = reference.ExpectedLevel is null || element.Level is null || reference.ExpectedLevel == element.Level;
        return roleCorrect && levelCorrect;
    }

    private static bool RoleMatches(string? actual, string? expected)
    {
        if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(expected)) return false;
        var normalizedActual = actual.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        var normalizedExpected = expected.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        return normalizedActual == normalizedExpected || normalizedActual is "headingtopic" or "localheading" && normalizedExpected == "heading";
    }

    private static int AuthorityRank(HarnessOccurrenceJoinResult row) => row.ReferenceAuthority switch
    {
        HarnessReferenceAuthority.HumanGold => 4,
        HarnessReferenceAuthority.HumanKey => 3,
        HarnessReferenceAuthority.SourceStructuralReference => 2,
        _ => 1,
    };

    private static string Key(string? documentId, string? sourceId) => $"{documentId}|{sourceId}";
    private static double? Ratio(int numerator, int denominator) => denominator == 0 ? null : (double)numerator / denominator;
    private static string? GetString(JsonElement node, string property) => node.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;

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

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static async Task WriteJsonAsync(string path, object value, CancellationToken cancellationToken) =>
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false), cancellationToken);

    private static async Task WriteReadmeAsync(string repoRoot, object decision, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(decision, JsonOptions);
        var content = "# Harness-Lift HL3\n\n" +
            "HL3 replaces the coarse HL2 CandidateLoss bucket with route-aware, provenance-safe causal attribution.\n\n" +
            "Provider calls: 0. HL2 artifacts, the 42-packet review queue, holdout labels, and N15 history remain immutable.\n\n" +
            "```json\n" + json + "\n```\n";
        await File.WriteAllTextAsync(Path.Combine(repoRoot, "eval/harness-lift/README-v3.md"), content, new UTF8Encoding(false), cancellationToken);
    }

    private static async Task WriteArchitectureDocAsync(string repoRoot, object decision, CancellationToken cancellationToken)
    {
        var path = Path.Combine(repoRoot, "docs/architecture/harness-lift-route-aware-attribution.md");
        var json = JsonSerializer.Serialize(decision, JsonOptions);
        var content = "# HL3 route-aware harness attribution\n\n" +
            "HL2 reported CandidateLoss=261. HL3 audits identity namespaces first, distinguishes deterministic ownership from model exposure, and retains UNKNOWN where request membership or lineage was not recorded.\n\n" +
            "The official flow is: reference positive -> source presence -> route owner -> deterministic final or candidate -> selected -> occurrence exposure -> proposal/validation -> final. Fuzzy text and final semantic similarity are not lineage.\n\n" +
            "Human review remains frozen: no new human keys or gold labels were created. This report is measurement only; it authorizes no production remediation.\n\n" +
            "## Decision snapshot\n\n```json\n" + json + "\n```\n";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), cancellationToken);
    }

    private sealed record CorpusEntry(string DocumentId, string Path);
    private sealed record ModelRunInfo(bool Occurred, int ProviderCalls);
    private sealed record RouteOwnershipRow
    {
        public required string ReferenceId { get; init; }
        public required string DocumentId { get; init; }
        public required string SourceId { get; init; }
        public required HarnessHl3RouteOwner RouteOwner { get; init; }
        public required bool ModelRequired { get; init; }
        public required string ModelActuallyExposed { get; init; }
        public required bool DeterministicAuthorityUsed { get; init; }
        public required bool DeterministicCorrect { get; init; }
        public required bool DeterministicWrong { get; init; }
    }
    private sealed record LossRow
    {
        public required string RunId { get; init; }
        public required int Repeat { get; init; }
        public required string DocumentId { get; init; }
        public required string SourceId { get; init; }
        public required string ReferenceId { get; init; }
        public required string Hl2FirstLoss { get; init; }
        public required HarnessHl3CandidateLossDisposition Disposition { get; init; }
        public required object Evidence { get; init; }
    }
    private sealed record LiveDocument(
        string DocumentId,
        SourceDocument Source,
        AuthorityPipelineExecutionResult Execution,
        RouteExecutionAudit? Audit,
        HashSet<string> CandidateIds,
        HashSet<string> SelectedIds,
        HashSet<string> DecisionIds,
        HashSet<string> StageIds,
        HashSet<string> HierarchyIds,
        HashSet<string> BudgetIds,
        IReadOnlyDictionary<string, ValidatedStructuralElement> FinalBySource,
        IReadOnlyDictionary<string, HeadingRecord> CompatibilityBySource);

    private sealed record DispositionCounts(
        int TrueCandidateNotConstructed,
        int TrueCandidateNotSelected,
        int TrueRankingBudgetLoss,
        int DeterministicBypassCorrect,
        int DeterministicBypassWrong,
        int RepresentationMismatch,
        int FinalSourceLineageMismatch,
        int TraceNotObservable,
        int OtherProvenStage)
    {
        public IEnumerable<KeyValuePair<string, int>> AsPairs()
        {
            yield return new("TRUE_CANDIDATE_NOT_CONSTRUCTED", TrueCandidateNotConstructed);
            yield return new("TRUE_CANDIDATE_NOT_SELECTED", TrueCandidateNotSelected);
            yield return new("TRUE_RANKING_BUDGET_LOSS", TrueRankingBudgetLoss);
            yield return new("DETERMINISTIC_BYPASS_CORRECT", DeterministicBypassCorrect);
            yield return new("DETERMINISTIC_BYPASS_WRONG", DeterministicBypassWrong);
            yield return new("REPRESENTATION_MISMATCH", RepresentationMismatch);
            yield return new("FINAL_SOURCE_LINEAGE_MISMATCH", FinalSourceLineageMismatch);
            yield return new("TRACE_NOT_OBSERVABLE", TraceNotObservable);
            yield return new("OTHER_PROVEN_STAGE", OtherProvenStage);
        }
    }
}
