using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.DocumentProcessing.Audit;
using DocxHeaderExtractor.DocumentProcessing.Output;
using DocxHeaderExtractor.Contracts.Provider;
using DocxHeaderExtractor.DocumentProcessing.Structure;
using DocxHeaderExtractor.DocumentProcessing.Selection;
using DocxHeaderExtractor.Sources.Snapshot;

namespace DocxHeaderExtractor.DocumentProcessing.Harness;

/// <summary>
/// The stages every heading lane shares, whatever the format: refuse a run that is not on the authorized agent route; run under a lane
/// lease and refuse anything but a completed result; let the selected-lines authority decide on the source; bind, place and assemble the
/// structure; record the route audit. A lane supplies only what belongs to its format: how its source is built, its layout blocks, the
/// source blocks it audits, and the facts it adds to the audit.
/// </summary>
internal static class LaneHarness
{
    /// <summary>What a lane says when its execution does not complete. Each lane keeps its own wording.</summary>
    internal sealed record Messages(string TimedOut, string Failed, string LeaseLost);

    /// <summary>What a lane says when a run has no selector, no provider key, or no placer.</summary>
    internal sealed record AccessMessages(string NoSelector, string NoKey, string NoPlacer);

    /// <summary>The model route, the agents and the provider key a lane runs with.</summary>
    internal sealed record Access(ModelRoute Route, IHeadingSelector Selector, IHeadingPlacer Placer, string ApiKey);

    /// <summary>
    /// What a lane gives the shared run: its route id, its source, the layout blocks its selected lines group by, and the source blocks it
    /// audits with the number of pages they span.
    /// </summary>
    internal sealed record Input(
        string RouteId,
        DocumentSourceSnapshot Source,
        IReadOnlyDictionary<string, string> LayoutBlockByAtom,
        IReadOnlyList<SourceBlockAudit> Blocks,
        int Pages);

    /// <summary>The selector's decision, the assembled structure, the completed route audit and how long assembly took.</summary>
    internal sealed record Outcome(SelectionResult Decided, StructureAssembly Assembly, RunAudit Audit, double AssembleSeconds);

    /// <summary>Refuses a run without a selector, one without a provider key, and one without a placer: a lane never runs with part of its
    /// agents and quietly leaves the hierarchy out.</summary>
    internal static Access Authorize(ModelRoute route, IHeadingSelector? selector, IHeadingPlacer? placer, AccessMessages messages)
    {
        if (selector is null)
            throw new InvalidOperationException(messages.NoSelector);
        var apiKey = string.IsNullOrWhiteSpace(route.ApiKey) ? throw new InvalidOperationException(messages.NoKey) : route.ApiKey;
        if (placer is null)
            throw new InvalidOperationException(messages.NoPlacer);
        return new Access(route, selector, placer, apiKey);
    }

    /// <summary>The route audit of a run that never called the model: every source block available and selected, no lane run.</summary>
    internal static RunAudit SourceOnlyAudit(Input lane) =>
        AuditBoundary.Create(lane.RouteId, lane.Blocks.Count, lane.Blocks.Count, lane.Pages, lane.Pages, lane.Blocks, lane.Blocks, [], []) with
        {
            ModelInputContracts = [],
            SemanticLane = new LaneExecutionAudit("not-run", lane.Source.Atoms.Count, 0, 0, lane.Source.Atoms.Count, "llm-disabled"),
            SpanLane = new LaneExecutionAudit("not-run", 0, 0, 0, 0, "llm-disabled"),
        };

    /// <summary>
    /// Runs <paramref name="work"/> under the lane deadline and returns its value only when it completed and still holds its lease.
    /// <paramref name="afterRun"/> runs once the work has stopped, before the outcome is judged (the PDF lane drains its checkpoint there).
    /// </summary>
    internal static async Task<T> CompleteAsync<T>(
        Func<LaneLease, CancellationToken, Task<T>> work,
        TimeSpan deadline,
        Messages messages,
        CancellationToken ct,
        Func<Task>? afterRun = null) where T : class
    {
        var execution = await LaneExecution.RunAsync(work, deadline, ct).ConfigureAwait(false);
        if (afterRun is not null) await afterRun().ConfigureAwait(false);
        if (execution.State == LaneState.TimedOut)
            throw new TimeoutException(messages.TimedOut);
        if (execution.State == LaneState.Cancelled)
            throw new OperationCanceledException(ct);
        if (execution.State == LaneState.Failed)
            throw execution.Fault ?? new InvalidOperationException(messages.Failed);
        if (!execution.Lease.CanPublishCompletedResult || execution.Value is null)
            throw new InvalidOperationException(messages.LeaseLost);
        return execution.Value;
    }

    /// <summary>
    /// Inside the lease: the selector decides, the shared stages bind, place (the placer proposes, C# checks) and assemble the decided
    /// headings, and the route audit is recorded and completed with the authority's own facts and the placement observation.
    /// </summary>
    internal static async Task<Outcome> DecideAndAssembleAsync(
        Input lane, IHeadingSelector selector, IHeadingPlacer? placer, string path, string apiKey, ModelRoute? route, LaneLease lease,
        CancellationToken ct)
    {
        var decided = await DecideAsync(lane.Source, selector, placer, path, apiKey, lane.LayoutBlockByAtom, route, lease, ct).ConfigureAwait(false);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // The selected-lines authority defines no semantic node identity, so every validated source occurrence reaches the canonical structure.
        var assembly = await StructureAssembler.AssembleAsync(lane.Source, decided, PrimaryOccurrences.EverySourceOccurrence, ct).ConfigureAwait(false);
        var assembleSeconds = clock.Elapsed.TotalSeconds;
        var audit = AuditBoundary.Create(
            lane.RouteId,
            lane.Blocks.Count,
            lane.Blocks.Count,
            lane.Pages,
            lane.Pages,
            lane.Blocks,
            lane.Blocks,
            decided.Decisions.Select(decision => new SourceBlockDecisionAudit(decision.Id, decision.SemanticFunction)).ToArray(),
            assembly.Validated.Select(item => item.SourceId).ToArray());
        var completed = decided.CompleteAudit(audit, assembly) with { PlacementExecution = assembly.PlacementObservation };
        return new Outcome(decided, assembly, completed, assembleSeconds);
    }

    /// <summary>The lane's result: the assembled structure, every element emitted, the source catalog and the projection context.</summary>
    internal static LaneResult Result(string reason, DocumentSourceSnapshot source, StructureAssembly assembly, RunAudit audit) =>
        new(
            assembly.Structure,
            audit,
            reason,
            assembly.Structure.Elements.Select(value => value.Id).ToHashSet(StringComparer.Ordinal))
        { SourceCatalog = source.Catalog, ProjectionContext = assembly.ProjectionContext };

    /// <summary>The selected-lines authority decides on <paramref name="source"/>; the placer's access to the model ends with the lane lease.</summary>
    private static Task<SelectionResult> DecideAsync(
        DocumentSourceSnapshot source,
        IHeadingSelector selector,
        IHeadingPlacer? placer,
        string path,
        string apiKey,
        IReadOnlyDictionary<string, string> layoutBlockByAtom,
        ModelRoute? route,
        LaneLease lease,
        CancellationToken ct)
    {
        void EnsureActive() { if (!lease.IsActive) throw new LaneLeaseLostException(); }
        return new SelectionAuthority(
            selector,
            path,
            apiKey,
            layoutBlockByAtom,
            route is null ? null : new ModelAccess(apiKey, EnsureActive),
            placer,
            EnsureActive)
        .DecideAsync(source, ct);
    }
}
