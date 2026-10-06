using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Semantics.HeadingAuthority.Protocols;

namespace DocxHeaderExtractor.DocumentProcessing.Semantics.HeadingAuthority;

/// <summary>
/// Shared heading authority promoted from the F1 → G2A → H2-C V2 qualification chain.
/// Each stage is strict and fail-closed: a rejected F1/G2A pack emits no headings and a rejected
/// boundary decision withholds only its anchor. This class intentionally has no semantic fallback.
/// </summary>
internal static class HeadingAuthorityPipeline
{
    internal const string AuthorityId = "pdf-function-conditioned-heading-authority-v1";
    private const int ResponseCap = PdfQualifiedInferencePolicy.ResponseUtf8ByteCap;
    // Qualification serializes with the framework default encoder.  Do not use the
    // relaxed encoder here: escaping is part of the provider-body identity.
    private const int P05CompletionTokens = PdfQualifiedInferencePolicy.CompletionTokenCeiling;
    public static async Task<StructuralAuthorityResult> RunAsync(
        SourceOccurrenceUniverse authority,
        string sourceName,
        IHeaderClassifier? classifier,
        SemanticLaneOptions? semanticLaneOptions,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        if (authority.ParserLineCount == 0 || authority.Blocks.Count == 0)
            return new StructuralAuthorityResult(new ValidatedStructure([]), null, "pdf-no-text-layer") { SourceCatalog = authority.Catalog };
        if (classifier is null)
            return new StructuralAuthorityResult(
                new ValidatedStructure([]), SourceOnlyAudit(authority), "pdf-function-conditioned-llm-disabled")
            { SourceCatalog = authority.Catalog };
        if (classifier is not IFrozenInferenceTransport frozen)
            throw new InvalidOperationException("PDF_H2C_PRODUCTION_ROUTE_REQUIRES_FROZEN_REQUEST_TRANSPORT");

        await using var scope = ProductionCheckpointScope.Create();
        await using var checkpoint = new PdfStageCheckpoint(scope.CheckpointPath, Path.GetFileNameWithoutExtension(sourceName));
        await checkpoint.RecordSelectionAsync(
            authority.Blocks.Select(block => new PdfSelectedSourceIdentity(
                block.Id, block.Page, block.Lines.Select(PdfLineIdentity.Of).ToArray(), block.DisplayText)).ToArray(), ct).ConfigureAwait(false);
        var execution = await PdfLaneExecution.RunAsync(
            (lease, laneCt) => RunCoreAsync(authority, frozen, lease, laneCt),
            (semanticLaneOptions ?? SemanticLaneOptions.Default).LaneDeadline,
            ct).ConfigureAwait(false);
        await checkpoint.StopAcceptingWritesAndDrainAsync().ConfigureAwait(false);
        if (execution.State == PdfLaneExecutionState.TimedOut)
            throw new TimeoutException("PDF semantic execution exceeded its lane deadline.");
        if (execution.State == PdfLaneExecutionState.Cancelled)
            throw new OperationCanceledException(ct);
        if (execution.State == PdfLaneExecutionState.Failed)
            throw execution.Fault ?? new InvalidOperationException("PDF semantic execution failed.");
        if (!execution.Lease.CanPublishCompletedResult || execution.Value is null)
            throw new InvalidOperationException("PDF semantic result lost its execution lease.");
        return execution.Value;
    }

    private static RouteExecutionAudit SourceOnlyAudit(SourceOccurrenceUniverse authority)
    {
        var sourceBlocks = authority.Blocks
            .Select(block => new RouteBlockAudit(block.Id, block.Page, block.DisplayText))
            .ToArray();
        return CanonicalRouteAuditBoundary.Create(
            AuthorityId,
            authority.Blocks.Count,
            authority.Blocks.Count,
            authority.Blocks.Select(block => block.Page).Distinct().Count(),
            authority.Blocks.Select(block => block.Page).Distinct().Count(),
            sourceBlocks,
            sourceBlocks,
            [],
            []) with
        {
            ModelInputContracts = [],
            SemanticLane = new RouteLaneExecutionAudit(
                "not-run", authority.Atoms.Count, 0, 0, authority.Atoms.Count, "llm-disabled"),
            SpanLane = new RouteLaneExecutionAudit(
                "not-run", 0, 0, 0, 0, "llm-disabled"),
        };
    }

    private static async Task<StructuralAuthorityResult> RunCoreAsync(SourceOccurrenceUniverse authority, IFrozenInferenceTransport frozen, PdfLaneExecutionLease lease, CancellationToken ct)
    {
        var leaseBound = new LeaseBoundFrozenHeaderClassifier(frozen, lease);
        var atoms = authority.Atoms.ToDictionary(atom => atom.Alias, StringComparer.Ordinal);
        var decisions = new List<HeadingExtentDecision>();
        var raw = new List<string>();
        foreach (var pack in SemanticEvidencePackingPolicies.PdfResourceBoundedP05.BuildPacks(authority.Evidence, authority.LayoutBlockByAtom))
        {
            ct.ThrowIfCancellationRequested();
            if (!lease.IsActive) throw new PdfExecutionLeaseLostException();
            var ownedAliases = pack.Owned.Select(item => item.SourceAlias).ToArray();
            var owned = ownedAliases.Select(alias => atoms[alias]).ToArray();
            var visible = pack.Visible.Select(item => item.SourceAlias).ToArray();
            var context = visible.Where(alias => !ownedAliases.Contains(alias, StringComparer.Ordinal)).Select(alias => (atoms[alias].Page, atoms[alias].Text)).ToArray();
            var f1Request = OccurrenceFunctionProtocolV1.ComposeWithReadOnlyCorrespondences(owned, context, Correspondences(owned, authority.Atoms));
            var f1 = await ExecuteAsync(leaseBound, f1Request.SystemPrompt, f1Request.UserMessage, P05CompletionTokens, ct).ConfigureAwait(false);
            if (f1 is null) continue;
            raw.Add(f1.Content);
            OccurrenceFunctionResult functions;
            try { using var json = JsonDocument.Parse(f1.Content); functions = OccurrenceFunctionProtocolV1.Parse(json.RootElement, Encoding.UTF8.GetByteCount(f1.Content), ResponseCap, f1Request.Occurrences); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { continue; }
            var byId = f1Request.Occurrences.ToDictionary(value => value.Id, StringComparer.Ordinal);
            var establishes = functions.Decisions.Where(value => value.Function == OccurrenceFunction.EstablishesStructure).Select(value => byId[value.OccurrenceId]).ToArray();
            if (establishes.Length == 0) continue;

            var idByAlias = f1Request.Occurrences.ToDictionary(value => value.Atom.Alias, value => value.Id, StringComparer.Ordinal);
            var g2aUser = HeadingAnchorProtocolV1.ComposeUserMessage(owned, idByAlias, establishes.Select(value => (value.Id, value.Atom)).ToArray());
            var g2aBody = QualifiedInferenceRequestFactory.Build(HeadingAnchorProtocolV1.SystemPrompt, g2aUser, P05CompletionTokens);
            var g2a = await ExecuteAsync(leaseBound, HeadingAnchorProtocolV1.SystemPrompt, g2aUser, P05CompletionTokens, ct, g2aBody).ConfigureAwait(false);
            if (g2a is null) continue;
            raw.Add(g2a.Content);
            HashSet<string> has;
            try { has = HeadingAnchorProtocolV1.Parse(g2a.Content, establishes.Select(value => value.Id)); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { continue; }

            foreach (var anchor in establishes.Where(value => has.Contains(value.Id)))
            {
                var start = Array.IndexOf(ownedAliases, anchor.Atom.Alias);
                if (start < 0) continue;
                var tail = ownedAliases.Skip(start).ToArray(); // terminal anchors intentionally remain issued.
                var evidenceByAlias = authority.Evidence.ToDictionary(item => item.SourceAlias, StringComparer.Ordinal);
                var user = HeadingExtentProtocolV2.ComposeUserMessage(anchor.Id, tail, idByAlias, atoms, evidenceByAlias);
                var body = QualifiedInferenceRequestFactory.Build(HeadingExtentProtocolV2.SystemPrompt, user, P05CompletionTokens);
                var boundary = await ExecuteAsync(leaseBound, HeadingExtentProtocolV2.SystemPrompt, user, P05CompletionTokens, ct, body).ConfigureAwait(false);
                if (boundary is null) continue;
                raw.Add(boundary.Content);
                try { decisions.Add(HeadingExtentProtocolV2.Bind(boundary.Content, anchor.Id, tail, idByAlias, atoms)); }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
            }
        }

        var validated = HeadingProposalBinder.BindAndValidate(authority.HeadingContexts, decisions);
        var bound = decisions.Select(decision => ToBound(decision, atoms)).ToArray();
        var placed = await HeadingPlacementCoordinator.PlaceUnresolvedHeadingsAsync(bound, leaseBound, ct).ConfigureAwait(false);
        var hierarchy = HeadingHierarchyResolver.DeriveHierarchyFromModelRelations(placed);
        var structures = hierarchy.ToDictionary(item => item.SourceId, item => new ResolvedHeadingPlacement(item.SourceId, item.Level, item.ParentSourceId, item.Resolution, "requires_review") { StructuralScope = authority.Contexts[item.SourceId].Source.StructuralScope }, StringComparer.Ordinal);
        var occurrences = authority.Contexts.ToDictionary(pair => pair.Key, pair => new CanonicalSourceOccurrence(
            pair.Key,
            authority.OrdinalBySourceId.GetValueOrDefault(pair.Key),
            pair.Value.Source.RawText,
            null,
            "pdf",
            "pdf-source-pointer-span"), StringComparer.Ordinal);
        var structure = CanonicalStructureMaterializer.Materialize(validated, structures, occurrences, "pdf", StructuralDecisionOrigin.Model, structures.Keys.ToHashSet(StringComparer.Ordinal));
        var sourceBlocks = authority.Blocks.Select(block => new RouteBlockAudit(block.Id, block.Page, block.DisplayText)).ToArray();
        var audit = CanonicalRouteAuditBoundary.Create(
            AuthorityId,
            authority.Blocks.Count,
            authority.Blocks.Count,
            authority.Blocks.Select(block => block.Page).Distinct().Count(),
            authority.Blocks.Select(block => block.Page).Distinct().Count(),
            sourceBlocks,
            sourceBlocks,
            decisions.Select(decision => new RouteBlockDecisionAudit(decision.Id, decision.SemanticFunction)).ToArray(),
            validated.Select(item => item.SourceId).ToArray()) with
        {
            RawAnalystResponses = raw,
            ModelInputContracts = ["v5-total-occurrence-function-f1", "v5-function-conditioned-anchor-existence-1", "v5-function-conditioned-exact-end-pointer-clean-paired-1"],
            ValidatedStructures = structures.Values.ToArray(),
            HierarchyFacts = PdfHierarchyFactsInventory.Inspect(validated, authority.Contexts),
            SemanticLane = new RouteLaneExecutionAudit("complete", authority.Atoms.Count, decisions.Count, 0, 0),
            SpanLane = new RouteLaneExecutionAudit("exact-end-pointer", decisions.Count, validated.Count, 0, decisions.Count - validated.Count),
        };
        return new StructuralAuthorityResult(structure, audit, AuthorityId, structure.Elements.Select(value => value.Id).ToHashSet(StringComparer.Ordinal)) { SourceCatalog = authority.Catalog };
    }

    private static CanonicalSemanticBoundHeading ToBound(HeadingExtentDecision decision, IReadOnlyDictionary<string, SemanticSourceAtom> atoms)
    {
        var primary = atoms.Values.Single(atom => atom.SourceId == decision.Id);
        var parts = decision.Parts ?? [];
        return new CanonicalSemanticBoundHeading(primary.Alias, primary.SourceId, primary.Ordinal, string.Join(" ", parts.Select(value => value.Text)), "ESTABLISHES_STRUCTURE", "heading", "document_body", [], 0, primary.Text.Length) { Parts = parts };
    }

    private static async Task<FrozenHeaderExecutionResult?> ExecuteAsync(IFrozenInferenceTransport classifier, string prompt, string user, int maxTokens, CancellationToken ct, byte[]? body = null)
    {
        body ??= QualifiedInferenceRequestFactory.Build(prompt, user, maxTokens);
        var result = await classifier.ExecuteFrozenRequestAsync(body, maxTokens, prompt, user, ct).ConfigureAwait(false);
        return string.Equals(result.FinishReason, "stop", StringComparison.OrdinalIgnoreCase) && Encoding.UTF8.GetByteCount(result.Content) <= ResponseCap ? result : null;
    }

    private sealed class LeaseBoundFrozenHeaderClassifier : IFrozenInferenceTransport
    {
        private readonly IFrozenInferenceTransport _inner;
        private readonly PdfLaneExecutionLease _lease;

        public LeaseBoundFrozenHeaderClassifier(IFrozenInferenceTransport inner, PdfLaneExecutionLease lease)
        {
            _inner = inner;
            _lease = lease;
        }

        public string ModelName => _inner.ModelName;
        public int ContextSize => _inner.ContextSize;
        public string RuntimeDescription => _inner.RuntimeDescription;
        public int SharedPrefixTokens => _inner.SharedPrefixTokens;
        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default, int expectedItemCount = 0) =>
            StartAndObserve(() => _inner.BoundaryCutAsync(systemPrompt, userMessage, ct, expectedItemCount));
        public Task<FrozenHeaderExecutionResult> ExecuteFrozenRequestAsync(byte[] providerBody, int maxTokens, string systemPrompt, string userMessage, CancellationToken cancellationToken = default) =>
            StartAndObserve(() => _inner.ExecuteFrozenRequestAsync(providerBody, maxTokens, systemPrompt, userMessage, cancellationToken));
        public void Dispose() { }

        private Task<T> StartAndObserve<T>(Func<Task<T>> start)
        {
            Task<T>? task = null;
            if (!_lease.TryStartDownstream(() => task = start())) throw new PdfExecutionLeaseLostException();
            return ObserveAsync(task!, _lease);
        }

        private static async Task<T> ObserveAsync<T>(Task<T> task, PdfLaneExecutionLease lease)
        {
            var result = await task.ConfigureAwait(false);
            if (!lease.IsActive) throw new PdfExecutionLeaseLostException();
            return result;
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>> Correspondences(
        IReadOnlyList<SemanticSourceAtom> owned, IReadOnlyList<SemanticSourceAtom> all) =>
        PdfReadOnlyCorrespondenceBuilder.Build(owned, all);

}
