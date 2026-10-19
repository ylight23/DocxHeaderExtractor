# Bounded hierarchy non-ACCEPT repair

The heading universe is fixed before hierarchy. No step here changes selection, grouping,
heading text, membership, binding or Gold, and no missing ancestor is added automatically.

1. Propose missing parent relations and independently verify every available proposal.
2. Keep independently accepted edges. Every other bound heading is targeted once by
   `HIERARCHY_REPAIR`: REJECT, UNRESOLVED, MISSING_ANCESTOR, or no valid proposal/verdict.
   Feedback includes the prior decision, reason, evidence refs and blocking aliases. A
   missing verdict is explicitly marked NO_VALID_VERDICT, not interpreted as a rejection.
3. Remove old proposals and verdicts for those targets, install only validated repair
   proposals, and run a fresh `HIERARCHY_REVERIFY` on those edges only. It has no prior
   verifier/repair conversation history. Only independent ACCEPT authorizes a relation.
   Protocol/evidence rounds remain bounded by MaxRounds=3; semantic edge repair occurs once.
4. Accepted descendant edges retain their approvals because their immediate parents did
   not change. Derive hierarchy again: a newly verified parent chain can place them; a
   missing or unverified chain still leaves them unresolved. No descendant is auto-promoted.

Existing graph-consistency repair is separate and unchanged: contradictory NONE-as-parent
claims require model repair and fresh verification of the affected graph, including
its descendants. Its quarantine and cycle bounds remain intact. This is distinct from
ordinary parent repair resolving an uncertain chain without changing descendant edges.

## Safe diagnostics

`placementExecution.EdgeRepairs` records each target's initial verification status,
whether a valid repair proposal was received, and the fresh edge-verification status.
No source text, reason, prompt or completion is stored in these observations.

`placementExecution.UnresolvedSummary` partitions the final unresolved headings:

- `DirectUnverified + AncestorChainBlocked = UnresolvedCount`.
- `MissingAncestor + ProtocolNoVerdict + ModelUnresolved + ModelRejected +
  GraphConflictBlocked = DirectUnverified`.

Graph quarantine takes precedence over the verdict subcategories. MissingAncestor means
**terminal model-reported** missing ancestry, not ground truth established by C#.
ProtocolNoVerdict means no valid terminal verdict, including a failed repair proposal;
it is not proof of a transport or JSON failure by itself. Calls/Failures retain those
specific failure diagnostics. Historical protocol errors can coexist with a successful
repair; terminal metrics are computed from final decisions. The Web diagnostics display
these counters next to the per-heading decision table.

Offline regressions cover every non-ACCEPT input, missing proposals, fresh independent
acceptance, preserved accepted descendants, persistent uncertainty, malformed repair,
strict evidence validation, finish_reason=length, and the existing graph-conflict gate.
These tests establish policy and authority boundaries, not live extraction accuracy.
