# V4R2 P05 Owner-Fallback Candidate

Status: `DESIGN_NOTE_ONLY`

This note records a provider-free candidate arbitration rule. It does not
implement or activate fallback, change the P05 treatment, alter the OOS rule,
modify Gold, or authorize production promotion.

## Frozen P05 conclusion

The boundary attribution audit does not support boundary packing as the
primary cause of the observed semantic residuals:

```text
BOUNDARY_PRIMARY_CAUSE = NOT_SUPPORTED

FP near boundary = 20
FP interior      = 41

FN owner omission                 = 13
FN present only in neighbor halo  = 1
FN crosses primary boundary      = 0
FN crosses adaptive split        = 0

Partial boundary-related = 1
Partial interior         = 4
```

The ordinal-030 `D` case remains a useful architectural counterexample: the
actual owner omitted the claim, a neighboring child emitted a correctly bound
claim in halo, and the ownership gate rejected it as
`OUT_OF_OWNED_SEGMENT`. It is one case, not evidence to relax OOS globally.

The OOS audit found duplicate-owner evidence for `22/31` rejects (`6/8` on
`SRC-089` and `16/23` on `SRC-095`). Unrestricted halo acceptance would
therefore introduce competing authority and duplicate risk.

## Candidate rule

An OOS halo claim may become eligible for deterministic transfer only when all
of the following predicates hold:

1. The exact source span belongs to another execution leaf's owned segment.
2. The actual owner emitted no claim for that exact occurrence.
3. Exactly one neighboring leaf emitted the claim.
4. The candidate binds exactly to the source occurrence under the canonical
   binder; no span widening or semantic reinterpretation is allowed.
5. The transfer decision is deterministic and fully auditable.
6. No duplicate owner claim exists anywhere in the accepted lineage.

Failure of any predicate keeps the claim rejected as OOS. In particular,
multiple neighboring candidates, an owner duplicate, an inexact bind, or an
ambiguous source occurrence must never be resolved by preference heuristics.

## Deterministic adjudication sketch

This is a design sketch only; it is not production code.

1. Resolve the canonical source identity and exact span for the OOS claim.
2. Resolve the sole owning parent/leaf from the frozen ownership map.
3. Check the owner's accepted lineage for an exact claim at that occurrence.
4. Enumerate neighboring halo emissions and require exactly one candidate.
5. Re-run canonical binding against the candidate without changing the packet,
   prompt, model, or source universe.
6. Record either `TRANSFER_ELIGIBLE` or a deterministic rejection reason.
7. If eligible in a future experiment arm, transfer claim authority at the
   claim level only; do not merge response ownership or synthesize a provider
   response.

The transfer must preserve the ownership invariant for the source universe:

```text
accepted ownership = one authoritative claim per exact occurrence
accepted ownership ∩ duplicate claims = ∅
```

## Required audit record for a future arm

Each evaluated candidate should record at least:

```text
parentOrdinal
ownerLeaf
neighborLeaf
sourceAlias
sourceSpan / atom identity
leftBoundaryDistance
rightBoundaryDistance
adaptiveSplitParent (nullable)
ownerResponseHash
neighborResponseHash
ownerExactClaimPresent
neighborCandidateCount
exactBindResult
duplicateOwnerCheck
decision
rejectionReason (nullable)
providerCalls
```

The record must make it possible to reproduce the decision from immutable
responses and the frozen ownership map. No Gold-derived rule or score-driven
exception is permitted.

## Causal ordering

The matched control must precede this fallback arm. The control is
`FIXED_OWNED_COUNT_120 + reasoning=medium` with the same model, Alibaba route,
timeout, completion ceiling, response format, prompt/schema, facts, binder,
bounded retry/split policy, Gold, and scorer as P05. The only primary change is
resource-bounded P05 packing versus fixed owned-count 120.

Compare semantic and execution metrics first, including source-level F1,
extent errors, FP classes, owner omission, primary/total calls, input size,
completion saturation, retries, adaptive splits, latency, and final coverage.
Only after that comparison may this candidate be evaluated as a separate
ownership-arbitration treatment. It must not be combined with the packing
treatment or used to tune P05 against Gold.

## Explicit non-actions

```text
providerCalls = 0
P05 parameters changed = false
OOS policy changed = false
Gold modified = false
production code changed = false
production promotion = not authorized
```

