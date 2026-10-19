# Evidence-based hierarchy placement

Production placement uses `HEADING_PARENT_PROPOSAL_V2_CSHARP_GRAPH_VALIDATION`.
The source-aware model proposes immediate parent aliases, `ROOT`, `NONE`, or `UNRESOLVED`.
There is no semantic verifier or model repair in the production authorization path. A structurally
valid proposal is used directly; semantic accuracy remains a known model limitation and is not
claimed by this contract. Historical verifier and repair experiments remain in qualification
records and do not describe current production behavior.

## Source and graph checks

Placement sees the already-bound heading list, aliases/parts, source ordinals, measured styles,
nearby source context, document style measurements, source identity, and upstream withheld-line
states. It cannot add, remove, rename, or rebind headings. Withheld lines are context, not known
ancestors, and placement never restores them.

C# validates the proposal envelope, exact requested alias coverage, parent values, and that a
heading parent is bound and earlier in source order. `HeadingHierarchyResolver` derives levels
only from these accepted relation hints. It rejects missing, forward, self, and out-of-hierarchy
parents by leaving the affected relation unresolved; it does not infer a replacement parent,
consult numbering/style heuristics, or assign depth from heading words. Since accepted edges point
strictly backward in source order, cycles cannot be formed.

These checks establish protocol and graph structure, not semantic truth. For example, a false but
earlier bound parent can pass structural validation. Qualification work has demonstrated that a
second model verifier can also make false accepts and false rejects, so it no longer gates
production placement.

## Bounded work and audit

At most 16 targets are sent per proposal request, with a fixed concurrency cap and bounded
protocol retries. Invalid or missing proposals stay unresolved. No verifier, edge-repair, or
graph-repair request follows. Cancellation and transport lease handling remain on the same
structured inference path.

`PlacementExecution` reports the proposal contract, provider-call observations, requested and
enforced output caps, finish reason, response size, protocol errors, proposed parent, final
resolution, and source/binding trace. `VerifiedCount` is zero and decision `VerificationStatus` is
`NOT_RUN`; these fields remain for safe-audit compatibility and must not be read as an implicit
verification state. Raw prompts, responses, reasons, and source text are excluded from the safe
projection.
