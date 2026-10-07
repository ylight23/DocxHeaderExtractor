# S5: materialization validator dependency audit

Baseline: `757c311`.

## Evidence-driven ownership

S1 deferred this move on a DocumentProcessing status/contract dependency concern.
At the S5 baseline that concern is not present in the validator implementation:
the Authority using is unused; every input/output domain type belongs to Core.
The class references no HeadingDecisionStatus, pipeline, catalog builder, provider,
projection context, filesystem or parser implementation.

| Responsibility | Owner |
| --- | --- |
| Source identity, observed facts, exact selection bounds, duplicate source IDs | Core validator |
| Heading type/role, level range, supplied parent-ID set gate | Core validator |
| Materialize caller-selected spans/text into validated element DTO | Core validator |
| Construct facts from DOCX/PDF occurrences and choose hierarchy/status/origin | Processing materializer |
| Admit explicit parent relation graph / derive ParentId view | Core graph factory |
| Outline/grounding compatibility metadata and stable IDs | Processing Projection context |

Move the unchanged StructuralProposalValidator to Core/Semantics/Validation.
No facade/duplicate validator is retained at the old namespace. The production caller
already imports Core.Semantics.Validation. Core gains no project/package references.
The processing materializer still chooses RequiresReview and passes StructuralDecision;
the validator does not interpret or manufacture status/origin.

## Compatibility

This is an intentional namespace/assembly public API migration; source/binary consumers
must migrate to DocxHeaderExtractor.Core.Semantics.Validation. Method names, parameter
types/defaults, returned DTOs and serialized outputs are unchanged.

The LF-normalized class body (starting at public static class, excluding using/namespace)
SHA256 before/after move is:
`89c10471390ceb5a681f6128da9e049640f326263b16dc74b17a4db1ab4522dd`.
This records a mechanical relocation, not an algorithm rewrite. No historical artifact
is edited/rebaselined.

Preserved behavior includes rejection precedence/reasons, null ProposedSources selection,
provided selection order and ordinal fallback, optional known-parent set, exact text join,
and no ParentId stamping before graph admission. Caller-supplied decisions remain opaque.
Malformed duplicate observed dictionaries in the explicit-selection path retain existing
exception behavior; this checkpoint does not claim new input hardening.

Architecture tests register validator/factory beside the other Core validation services,
assert no reverse dependency and retire the old processing path. New regression exercises
the rejection matrix, multipart text/ordinal/decision parity and implicit source selection.

## Verification

Verified in the isolated tracked verification checkout with S1-S4 plus S5 changes.
User H3 edits and local untracked research are excluded.

- Release solution build: PASS, 0 errors; existing qualification/test warnings remain.
- Normalized before/after class-body SHA256: identical.
- Focused Core ownership/validator/compatibility/envelope/structural/projection/
  materialization/replay/containment/host E2E/PDF lifecycle/architecture/
  byte-parity/product suite: 135/135 PASS, 0 failed/skipped.
- TRX SHA256:
  `afa6d2e54b7f0897410d59c014060552f2bd18ba79b30edc891c9540135c8e37`.
- git diff --check: PASS.
- Full Windows suite/GitHub CI not rerun for S3-S5. S2's prior green CI is not
  claimed as verification of this move.
- Provider calls: 0. Gold mutation: 0. Frozen artifact rebaseline: 0.
