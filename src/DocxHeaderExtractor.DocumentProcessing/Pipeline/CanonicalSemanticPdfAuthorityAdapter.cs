using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Inference;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// The PDF lane. Same semantic stage as the DOCX lane, different source parser.
/// <para>
/// A PDF upload is extracted from that PDF and nothing else. It never consults a DOCX, and its
/// result is not reconciled with one: two uploads are two documents, and merging them is something
/// a user has to ask for, not something a lane decides.
/// </para>
/// <para>
/// Everything after source occurrences is <see cref="CanonicalSemanticEngine"/>, shared verbatim
/// with the DOCX lane - the prompt, segmentation, contract, binder, hierarchy resolver and
/// placement pass. What differs is only how the source is read: OOXML gives paragraphs, a PDF
/// gives text blocks grouped from positioned lines.
/// </para>
/// </summary>
internal static class CanonicalSemanticPdfAuthorityAdapter
{
    public static async Task<StructuralAuthorityResult> RunAsync(
        string pdfPath,
        IHeaderClassifier? transport,
        CancellationToken cancellationToken,
        CanonicalSemanticExperiment? experiment = null,
        SemanticLaneOptions? semanticLaneOptions = null,
        SemanticAuthorityReplayCaptureRequest? replayCapture = null,
        PdfExperimentExecutionGate? experimentGate = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);

        var universe = PdfCanonicalSourceUniverseBuilder.Build(pdfPath);
        experimentGate?.EnsureLiveSourceUniverse(universe.SourceUniverseSha256);
        if (universe.ParserLineCount == 0)
            return new StructuralAuthorityResult(new ValidatedStructure([]), null, "pdf-no-text-layer");
        if (universe.Blocks.Count == 0)
            return new StructuralAuthorityResult(new ValidatedStructure([]), null, "pdf-no-source-blocks");

        var scope = ProductionCheckpointScope.Create();
        var checkpoint = new PdfStageCheckpoint(
            scope.CheckpointPath,
            resume: false,
            Path.GetFileNameWithoutExtension(pdfPath));
        var execution = await PdfLaneExecution.RunAsync(
            (lease, ct) => RunSemanticCoreAsync(
                pdfPath, universe, transport, experiment, replayCapture, lease, checkpoint, ct),
            (semanticLaneOptions ?? SemanticLaneOptions.Default).LaneDeadline,
            cancellationToken).ConfigureAwait(false);

        if (execution.DetachedTask is { } detached)
        {
            var cleanup = DisposeCheckpointAfterDetachedAsync(detached, checkpoint);
            scope.DeferCleanup([cleanup]);
            await scope.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            await checkpoint.DisposeAsync().ConfigureAwait(false);
            await scope.DisposeAsync().ConfigureAwait(false);
        }

        if (execution.State == PdfLaneExecutionState.TimedOut)
            throw new TimeoutException("PDF semantic execution exceeded its lane deadline.");
        if (execution.State == PdfLaneExecutionState.Cancelled)
            throw new OperationCanceledException(cancellationToken);
        if (execution.State == PdfLaneExecutionState.Failed)
            throw execution.Fault ?? new InvalidOperationException("PDF semantic execution failed.");
        if (!execution.Lease.CanPublishCompletedResult || execution.Value is null)
            throw new InvalidOperationException("PDF semantic result lost its execution lease.");

        return execution.Value;
    }

    private static async Task<StructuralAuthorityResult> RunSemanticCoreAsync(
        string pdfPath,
        PdfCanonicalSourceUniverse universe,
        IHeaderClassifier? transport,
        CanonicalSemanticExperiment? experiment,
        SemanticAuthorityReplayCaptureRequest? replayCapture,
        PdfLaneExecutionLease lease,
        PdfStageCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        var input = universe.CreateProductionInput(Path.GetFileNameWithoutExtension(pdfPath)) with
        {
            ReplayCapture = replayCapture?.Metadata,
        };
        await checkpoint.RecordSelectionAsync(
            universe.Blocks.Select(block => new PdfSelectedSourceIdentity(
                block.Id,
                block.Page,
                block.Lines.Select(PdfLineIdentity.Of).ToArray(),
                block.DisplayText)).ToArray(),
            cancellationToken,
            lease).ConfigureAwait(false);

        CanonicalSemanticProductionResult result;
        CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel? canonicalModel = null;
        if (transport is null)
        {
            result = CanonicalSemanticProductionEntryPoint.Run(input with { SemanticProposals = [] });
        }
        else
        {
            canonicalModel = new CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel(
                new LeaseBoundHeaderClassifier(transport, lease),
                experiment ?? CanonicalSemanticExperiment.Baseline);
            result = await CanonicalSemanticProductionEntryPoint.RunAsync(
                input, canonicalModel,
                requestId: $"pdf:{Path.GetFileNameWithoutExtension(pdfPath)}",
                cancellationToken: cancellationToken);
        }

        var replayPersistence = replayCapture?.Persist(result.ReplayBundle);

        var decisions = result.TextPipeline.BoundHeadings.Select(item => new PdfBlockDecision(
            item.SourceId,
            PdfBlockRole.HeadingTopic,
            1,
            "canonical-vnext-semantic-contract",
            new TextOffsetSpan(item.Start, item.End),
            SemanticRole: CanonicalSemanticEngine.ParseSemanticRole(item.SemanticRole))).ToArray();
        await checkpoint.RecordSemanticBatchAsync(universe.Blocks, decisions, cancellationToken, lease)
            .ConfigureAwait(false);
        var validated = PdfSemanticProposalBinder.BindAndValidate(universe, decisions);

        var boundHeadings = transport is null
            ? result.TextPipeline.BoundHeadings
            : await CanonicalSemanticPlacementCoordinator.PlaceUnresolvedHeadingsAsync(
                result.TextPipeline.BoundHeadings, transport, cancellationToken);

        var derived = ModelRelationHierarchyResolver
            .DeriveHierarchyFromModelRelations(boundHeadings)
            .Where(item => universe.Contexts.ContainsKey(item.SourceId))
            .ToArray();
        var structures = derived.ToDictionary(item => item.SourceId, item =>
        {
            var facts = universe.Contexts[item.SourceId].Source;
            return new PdfValidatedStructure(
                item.SourceId, item.Level, item.ParentSourceId, item.Resolution, "requires_review")
            {
                DomainRole = facts.DomainRole,
                StructuralScope = facts.StructuralScope,
                DomainExclusionProposed = facts.DomainEvidence.ProposesOutlineExclusion,
            };
        }, StringComparer.Ordinal);

        var primarySourceIds = derived
            .Where(item => item.IsPrimaryOccurrence)
            .Select(item => item.SourceId)
            .ToHashSet(StringComparer.Ordinal);
        var occurrences = universe.Contexts.ToDictionary(
            pair => pair.Key,
            pair => new CanonicalSourceOccurrence(
                pair.Value.Source.SourceId,
                universe.OrdinalBySourceId.GetValueOrDefault(pair.Key),
                pair.Value.Source.RawText,
                null),
            StringComparer.Ordinal);

        var structure = CanonicalStructureMaterializer.Materialize(
            validated,
            structures, occurrences, "pdf", primarySourceIds);

        var audit = CanonicalRouteAuditBoundary.Create(
            "pdf-canonical-vnext",
            universe.Blocks.Count,
            universe.Blocks.Count,
            0,
            0,
            universe.Blocks.Select(block => new RouteBlockAudit(block.Id, block.Page, block.DisplayText)).ToArray(),
            universe.Blocks.Select(block => new RouteBlockAudit(block.Id, block.Page, block.DisplayText)).ToArray(),
            [],
            decisions.Select(decision => new RouteBlockDecisionAudit(
                decision.Id, decision.Role.ToString(), decision.Confidence)
            {
                SemanticRole = decision.SemanticRole.ToString(),
                ProposedSourceSpan = decision.ProposedSourceSpan,
            }).ToArray(),
            validated.Select(item => item.SourceId).ToArray(),
            [],
            validated.Select(item => item.SourceId).ToArray()) with
        {
            RawAnalystResponses = canonicalModel?.RawResponses ?? [],
            ModelInputContracts = canonicalModel is null ? [] : [CanonicalSemanticContract.ProtocolVersion],
            ValidatedStructures = structures.Values.ToArray(),
            HierarchyFacts = PdfHierarchyFactsInventory.Inspect(validated, universe.Contexts),
            ConflictCensus = SemanticConflictCensus.Take(
                result.ConflictNormalization,
                CanonicalSemanticGlobalConflictDetector.Detect(
                    result.NormalizedModelProposals,
                    universe.Aliases,
                    result.SemanticConflicts),
                result.SemanticAdjudicationCalls,
                result.GlobalReopenCalls,
                result.TextPipeline.BoundHeadings.Select(item => item.SourceId)
                    .ToHashSet(StringComparer.Ordinal)),
            SemanticLane = new RouteLaneExecutionAudit("complete", universe.Blocks.Count, validated.Count, 0, 0),
            SpanLane = new RouteLaneExecutionAudit("canonical-binder", result.TextPipeline.BoundHeadings.Count,
                result.TextPipeline.BoundHeadings.Count, 0, result.TextPipeline.BindingFailureCount),
        };

        return new StructuralAuthorityResult(structure, audit, "pdf-canonical-vnext")
        {
            EmittedElementIds = structure.Elements.Select(element => element.Id).ToHashSet(StringComparer.Ordinal),
            // The catalog the model was shown and the binder bound against, handed out rather than
            // left to be reconstructed. The audit beside it records readable text for a person;
            // these are the occurrences themselves.
            SourceCatalog = universe.Catalog,
            ReplayBundle = result.ReplayBundle,
            ReplayPersistence = replayPersistence,
        };
    }

    private static async Task DisposeCheckpointAfterDetachedAsync(
        Task detached,
        PdfStageCheckpoint checkpoint)
    {
        try
        {
            await detached.ConfigureAwait(false);
        }
        finally
        {
            await checkpoint.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class LeaseBoundHeaderClassifier : IHeaderClassifier
    {
        private readonly IHeaderClassifier _inner;
        private readonly PdfLaneExecutionLease _lease;

        public LeaseBoundHeaderClassifier(IHeaderClassifier inner, PdfLaneExecutionLease lease)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _lease = lease ?? throw new ArgumentNullException(nameof(lease));
        }

        public string ModelName => _inner.ModelName;
        public int ContextSize => _inner.ContextSize;
        public string RuntimeDescription => _inner.RuntimeDescription;
        public int SharedPrefixTokens => _inner.SharedPrefixTokens;

        public Task<ChunkResult> ClassifyAsync(
            string chunkXml,
            IReadOnlyList<int> allowedIndexes,
            CancellationToken ct = default) =>
            StartAndObserve(() => _inner.ClassifyAsync(chunkXml, allowedIndexes, ct));

        public Task<ChunkResult> CritiqueAsync(
            string chunkXml,
            IReadOnlyList<int> allowedIndexes,
            CancellationToken ct = default) =>
            StartAndObserve(() => _inner.CritiqueAsync(chunkXml, allowedIndexes, ct));

        public Task<ChunkResult> ClassifyHierarchyAsync(
            IReadOnlyList<HierarchyItem> context,
            IReadOnlyList<HierarchyItem> headings,
            CancellationToken ct = default) =>
            StartAndObserve(() => _inner.ClassifyHierarchyAsync(context, headings, ct));

        public Task<string> BoundaryCutAsync(
            string systemPrompt,
            string userMessage,
            CancellationToken ct = default,
            int expectedItemCount = 0) =>
            StartAndObserve(() => _inner.BoundaryCutAsync(systemPrompt, userMessage, ct, expectedItemCount));

        public void Dispose() { }

        private Task<T> StartAndObserve<T>(Func<Task<T>> start)
        {
            Task<T>? task = null;
            if (!_lease.TryStartDownstream(() => task = start()))
                throw new PdfExecutionLeaseLostException();
            return ObserveResultAsync(task!, _lease);
        }

        private static async Task<T> ObserveResultAsync<T>(Task<T> task, PdfLaneExecutionLease lease)
        {
            var result = await task.ConfigureAwait(false);
            if (!lease.IsActive)
                throw new PdfExecutionLeaseLostException();
            return result;
        }
    }
}
