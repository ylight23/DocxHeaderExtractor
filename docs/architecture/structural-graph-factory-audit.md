# S1: structural graph construction ownership

Baseline: `ea3edff`. The attached review referenced the retired `57a2f08` monolith,
but its constructor/validator dependency and implicit ParentId authority concerns
still applied to Models/ValidatedStructure.cs.

## Implemented boundary

- ValidatedStructure is a passive result with an internal constructor; its structural
  data contracts no longer import Core.Semantics services.
- Core.Semantics.Validation.ValidatedStructureFactory.Create admits heading elements,
  checks IDs, validates/materializes explicit relation proposals and projects ParentId.
  No relation is inferred from an incoming element ParentId. Omitted proposals mean
  an empty relation graph, not a legacy parent fallback.
- HeadingStructureMaterializer builds relation proposals explicitly from the resolved
  upstream hierarchy's proposed parent, not from materialized element ParentId.
  StructuralProposalValidator retains parent validity checks but no longer stamps a
  ParentId view before the relation graph has been validated.
- All production callers and tracked tests use the factory. Empty-source paths and
  filtered DOCX structures preserve their existing explicit relations.
- The factory snapshots top-level element/relation collections as read-only lists.
  Nested DTO collections are not claimed to be recursively immutable.

## Compatibility and scope

Elements/Relations/OutlineElements property names, ordering, source selections,
validation, decision and projection metadata stay unchanged. The pre-cleanup heading
graph SHA256 remains `931fc4b2312baf43bf7ccaef1efbd604dd2b8838e72f5ed0146ae2476fff39cd`;
the regression now passes its relation proposal explicitly instead of using fallback.
No historical fixture/hash is rebaselined.

This is an intentional public construction API migration: public constructors and
FromElements are retired. Consumers must call ValidatedStructureFactory.Create with
explicit relation proposals. No silent legacy adapter is supplied.

The guard applies to structural/source data DTO files, not every file in Core.Models.
SemanticCoordinateContract still composes behavior from binding/validation/parsing;
this checkpoint does not claim that broader ownership debt is closed.
StructuralProposalValidator stays in DocumentProcessing.Materialization because it
uses DocumentProcessing authority/status contracts; moving it into Core wholesale
would reverse the assembly dependency. The graph factory and relation validator,
which depend only on Core DTOs, belong in Core.Semantics.Validation.

S2 projection metadata/stable IDs is deferred. Source ObservedEvidence retirement
was already handled by 998a371 and its compatibility limits are documented separately.
Prompt, provider body, Gold, inference runtime and frozen artifacts are not changed.
Provider calls: 0.

## Verification

- Clean tracked verification checkout at ea3edff plus this S1 patch; H3 edits and
  untracked research files excluded.
- Release solution build: PASS, 0 errors (incremental invocation 0 warnings;
  preceding test build still reported existing warnings).
- Focused structural/factory/source-span/output/projection/hierarchy/architecture/
  byte-parity/lifecycle/compatibility suite: 84/84 PASS, 0 failed/skipped.
  Includes the unchanged pre-cleanup heading graph SHA256 fixture.
- Focused TRX SHA256:
  `c53dbba70ba33aba85246794587a3a4de67ae6dde6fdd0258949f5ab98a0f010`.
- git diff --check: PASS. Full suite and GitHub CI not rerun for S1; no blanket
  qualification or full-suite PASS claim.

No provider calls, Gold mutation, frozen artifact edits or rebaseline.
