using DocxHeaderExtractor.Contracts.Routing;
using DocxHeaderExtractor.DocumentProcessing.Audit;
using DocxHeaderExtractor.DocumentProcessing.Output;
using DocxHeaderExtractor.Contracts.Inference;
using DocxHeaderExtractor.DocumentProcessing.Structure;
using DocxHeaderExtractor.DocumentProcessing.Selection;
using DocxHeaderExtractor.Sources.Common;

namespace DocxHeaderExtractor.DocumentProcessing.Harness;

/// <summary>
/// The stages every heading lane shares, whatever the format: run under a lane lease and refuse anything but a completed result; let the
/// selected-lines authority decide on the source; bind, place and assemble the structure; record the route audit. A lane supplies only what
/// belongs to its format: how its source is built, its layout blocks, the source blocks it audits, and the facts it adds to the audit.
/// </summary>
internal static class LaneHarness
{
    /// <summary>What a lane says when its execution does not complete. Each lane keeps its own wording.</summary>
    internal sealed record Messages(string TimedOut, string Failed, string LeaseLost);

    /// <summary>
    /// Runs <paramref name="work"/> under the lane deadline and returns its value only when it completed and still holds its lease.
    /// <paramref name="afterRun"/> runs once the work has stopped, before the outcome is judged (the PDF lane drains its checkpoint there).
    /// </summary>
    internal static async Task<T> CompleteAsync<T>(
        Func<LaneExecutionLease, CancellationToken, Task<T>> work,
        TimeSpan deadline,
        Messages messages,
        CancellationToken ct,
        Func<Task>? afterRun = null) where T : class
    {
        var execution = await LaneExecution.RunAsync(work, deadline, ct).ConfigureAwait(false);
        if (afterRun is not null) await afterRun().ConfigureAwait(false);
        if (execution.State == LaneExecutionState.TimedOut)
            throw new TimeoutException(messages.TimedOut);
        if (execution.State == LaneExecutionState.Cancelled)
            throw new OperationCanceledException(ct);
        if (execution.State == LaneExecutionState.Failed)
            throw execution.Fault ?? new InvalidOperationException(messages.Failed);
        if (!execution.Lease.CanPublishCompletedResult || execution.Value is null)
            throw new InvalidOperationException(messages.LeaseLost);
        return execution.Value;
    }

    /// <summary>The selected-lines authority decides on <paramref name="source"/>, with the placement transport bound to the lane lease.</summary>
    internal static Task<HeadingAuthorityResult> DecideAsync(
        DocumentSourceSnapshot source,
        IHeadingSelector selector,
        string path,
        string apiKey,
        IReadOnlyDictionary<string, string> layoutBlockByAtom,
        IInferenceTransport? transport,
        LaneExecutionLease lease,
        CancellationToken ct) =>
        new SelectionAuthority(
            selector,
            path,
            apiKey,
            layoutBlockByAtom,
            transport is null ? null : LeaseTransport.Bind(transport, lease),
            () => { if (!lease.IsActive) throw new LaneLeaseLostException(); })
        .DecideAsync(source, ct);

    /// <summary>The shared stages bind, place and assemble the decided headings.</summary>
    internal static Task<HeadingStructureAssembly> AssembleAsync(DocumentSourceSnapshot source, HeadingAuthorityResult decided, CancellationToken ct) =>
        // The selected-lines authority defines no semantic node identity, so every validated source occurrence reaches the canonical structure.
        StructureAssembler.AssembleAsync(source, decided, PrimaryOccurrenceSelection.EverySourceOccurrence, ct);

    /// <summary>The route audit of a lane whose source blocks were all available and all selected.</summary>
    internal static RunAudit Audit(
        string routeId, IReadOnlyList<SourceBlockAudit> blocks, int pages, HeadingAuthorityResult decided, HeadingStructureAssembly assembly) =>
        AuditBoundary.Create(
            routeId,
            blocks.Count,
            blocks.Count,
            pages,
            pages,
            blocks,
            blocks,
            decided.Decisions.Select(decision => new SourceBlockDecisionAudit(decision.Id, decision.SemanticFunction)).ToArray(),
            assembly.Validated.Select(item => item.SourceId).ToArray());

    /// <summary>The route audit completed with the authority's own facts and the placement observation.</summary>
    internal static RunAudit Completed(HeadingAuthorityResult decided, RunAudit audit, HeadingStructureAssembly assembly) =>
        decided.CompleteAudit(audit, assembly) with { PlacementExecution = assembly.PlacementObservation };

    /// <summary>The lane's result: the assembled structure, every element emitted, the source catalog and the projection context.</summary>
    internal static LaneResult Result(
        string reason, DocumentSourceSnapshot source, HeadingStructureAssembly assembly, RunAudit audit) =>
        new(
            assembly.Structure,
            audit,
            reason,
            assembly.Structure.Elements.Select(value => value.Id).ToHashSet(StringComparer.Ordinal))
        { SourceCatalog = source.Catalog, ProjectionContext = assembly.ProjectionContext };
}
