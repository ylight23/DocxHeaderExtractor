# Generic Modular Pipeline and Evidence Fabric

**IMPLEMENTATION STATUS: NOT YET IMPLEMENTED**

This is a design note only. It does not change the current extraction, identity, promotion, hierarchy, or level behavior.

## Boundary and responsibility

The future pipeline keeps semantic interpretation separate from source identity and coordinates:

```text
SOURCE
  -> source identity and fidelity
  -> source occurrences and stable aliases
  -> extraction / role / identity / parent / ROOT modules
  -> EvidenceRequest when a module lacks sufficient facts
  -> EvidenceOrchestrator
  -> EvidenceGraph
  -> DecisionResult<T>
  -> deterministic binding and graph validation
  -> task projection
```

The LLM may decide meaning or propose a relation. The harness owns source identity, coordinates, admissibility, and deterministic graph construction. `level` remains derived from validated tree depth; it is not an Evidence Fabric decision.

`SemanticOccurrence` is not the same thing as `TextOccurrence`. A source-backed occurrence may be `TEXT`, `IMAGE`, or `IMAGE_REGION` (and may later include `VECTOR`). An image XObject proves source ownership but does not, by itself, identify a heading region inside the image; a visual region is exact only after a reproducible locator supplies its pixel bounds and region hash. IR-018 is the motivating case: the outer title is native PDF text while the distinct inner title is image-only content in `/Im1`. A text-extractor gap and non-textual source content are different failure classes.

An image-backed source is also separated into an asset/container and any
occurrences localized inside it:

```text
SourceAsset (image XObject, source-owned bytes and placement)
  -> ILocalVisualRegionDetector (where text-bearing regions are)
  -> ILocalTextRecognizer (what a region says, if recognition is available)
  -> ImageRegionSourceOccurrence (source-backed pixel bbox and crop hash)
```

Region detection and text recognition are independent capabilities. A future
engine may implement both, but the source-occurrence contract must not depend on
an OCR engine object or silently treat recognized text as native PDF text.
`SourceAsset` and the exact pixel crop are `SOURCE_DIRECT` once grounded to the
source bytes; recognized text is `VISUAL_DERIVED` interpretation. Region
identity is derived only from the parent image occurrence, exact pixel bbox, and
region pixel hash, never from an IR, Gold label, semantic node, or expected
heading text. If a local detector cannot provide reproducible coordinates, the
image region remains unbound.

## Evidence Fabric

```text
                         EvidenceGraph
                              ^
                     EvidenceOrchestrator
                 ┌────────────┼────────────┐
                 │            │            │
          EvidencePlanner ProviderRegistry EvidenceValidator
                 │                         │
                 └──── SufficiencyEngine ──┘
                              ^
                      BudgetController
                              ^
                       EvidenceRequest
                              ^
       Extraction / Role / Identity / Parent / ROOT / future modules
```

Modules request facts, not tools. A module may return `NEED_MORE_EVIDENCE` with generic `EvidenceNeed` requirements. The Evidence Fabric chooses how to acquire those facts.

Good requirements:

- `ANCESTOR_SCOPE`
- `CONTINUATION_SIGNAL`
- `DOCUMENT_OWNERSHIP`
- `INTERVENING_PEER_STRUCTURE`

Tool-specific requests are deliberately not part of the module contract. `render page 14`, `call VLM`, and `read five paragraphs above` describe an acquisition strategy rather than a missing fact.

## Generic contracts

Future modules should exchange contracts equivalent to:

- `EvidenceRequest`: requester, target, requirements, budget, and provenance boundary;
- `EvidenceTarget`: source occurrence, semantic node, pair, region, or document scope;
- `EvidenceRequirement` / `EvidenceNeed`: a fact required to continue reasoning;
- `EvidenceItem`: one source-backed or derived fact with dependencies;
- `EvidenceAuthority`: authority and contamination metadata for a fact;
- `DecisionResult<T>`: `ACCEPTED`, `REJECTED`, `UNRESOLVED`, or `NEED_MORE_EVIDENCE` with decision provenance.

Each evidence item should record:

```text
evidenceId
kind
targets
value
sourceAliases
sourceHash
page/span/bbox when available
derivation
dependencies
authorityClass
deterministic
confidence
contaminationFlags
```

## Authority classes

```text
SOURCE_DIRECT
SOURCE_DERIVED
VISUAL_DERIVED
MODEL_INTERPRETED
MODEL_HYPOTHESIS
GOLD
```

`GOLD` is evaluation authority only and is forbidden from production decision input, candidate generation, model requests, promotion, and pre-freeze routing.

## Progressive acquisition tiers

```text
Tier 0: existing metadata

Tier 1: neighbor text, reading order, ancestor/sibling,
        numbering, style/layout

Tier 2: expanded structural context, intervening headings,
        cross-page/day/section boundaries

Tier 3: page rendering and bounding-box crop

Tier 4: VLM interpretation

still insufficient -> UNRESOLVED
```

The controller should stop at the lowest sufficient tier, preserve the evidence lineage, and fail closed when evidence remains insufficient.

## Identity case studies

### IR-018 — ownership boundary

Two near-identical `INDEPENDENT AUDITOR'S REVIEW REPORT` occurrences are distinct because one belongs to the enclosing IBRD report and the other belongs to an embedded Deloitte review letter. Text similarity and page proximity do not override document ownership.

### IR-019 — explicit continuation

`SESSION V: Current Research (Cont’d)` continues `SESSION V: Current Research` because the source provides the same session number/core title, an explicit continuation marker, compatible agenda scope, valid reading order, and no competing Session V node. The marker alone is not a universal merge rule.

### IR-022 — mutually exclusive branches

Identical `SPECIFIED PROVISIONAL SUMS for ES OUTCOMES` headings under `OPTION 1` and `OPTION 2` remain distinct. Same text/style/local appearance does not authorize same-node promotion across a `MUTUALLY_EXCLUSIVE_BRANCH` boundary.

## Production safety

Model-only identity positives remain hypotheses until an independent promotion policy accepts them. Evidence Fabric may acquire and validate facts, but it may not silently turn a model hypothesis into a merge, use Gold to fill missing evidence, or fabricate source coordinates. If requirements remain unsatisfied, the module returns `UNRESOLVED` and the downstream policy keeps occurrences split.
