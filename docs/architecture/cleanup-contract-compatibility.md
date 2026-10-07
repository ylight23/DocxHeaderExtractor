# Cleanup contract compatibility boundary

Reviewed baseline: `0b0f386` (including `998a371` and `0318104`). This checkpoint
documents and tests the resulting contracts; it does not restore retired members,
change model prompts, or rebaseline historical artifacts.

## Behavior, wire and public API are separate claims

| Change | Compatibility boundary |
| --- | --- |
| SourceFacts file rename | File ownership only; namespace/type name preserved. |
| ResolvedHeadingHierarchy moved to Materialization | Ownership corrected; public namespace changed, so external consumers must update imports/rebuild. |
| Placement prompt moved to HeadingParentResolver | Owner corrected; source diff preserves wording. PromptLineEndingIdentityTests checks normalization, not a historical prompt hash. |
| SourceFacts.Marker/ObservedEvidence removed | Public API removed; direct default/Web serialization loses these properties even when never populated. |
| StructuralSourceOccurrence.ObservedEvidence removed | Public API removed. Default/Web serialization already throws because ObservedSourceFacts is required and JsonIgnore; this is not a supported standalone wire DTO. A custom serializer that formerly emitted observedEvidence would have a changed shape. |
| SemanticContextPacket.VisibleEvidence removed | Public API removed; direct JSON loses the computed duplicate evidence property. No JsonPropertyName was required for it to serialize. |
| CanonicalSemanticGraphOccurrence.BindingMode removed | Public API removed; direct JSON loses the constant BindingMode/bindingMode field. Absence from pinned artifacts is not proof of universal wire parity. |
| DocumentOutline.Outcome removed | Public API removed. Previously null values were omitted, so current production null-outcome payloads can remain byte-equivalent. Non-null external uses are not preserved. |
| PipelineExecutionAudit selectedSourceIdentities/hierarchyProposals/visualLane | Retained compatibility surface; P3dMaterializationProjectionBoundaryTests pins emitted property names. No new production authority is implied by these fields. |

Consumers of retired public members must migrate. Current-shape regression tests are
not pre-cleanup byte-parity tests and must not be presented as such. No blanket
"all DTO JSON unchanged" claim is made. Existing structural heading graph byte
parity remains a separate fixture in HeadingStructuralContractTests.

## Deliberately retained live symbols

- DerivedHeadingHierarchy is returned by HeadingHierarchyResolver and consumed by
  HeadingStructureAssembler and HeadingParentResolver in production.
- CanonicalFinalStructureCounters is constructed by CanonicalFinalStructureProjection
  and emitted in the final-structure `counters` field.
- StructuralProposal remains an unvalidated materialization input. An upstream
  semantic decision does not bypass source/span/parent/level validation; renaming
  this intermediate to "decision" is not required for layering consistency.

CleanupContractCompatibilityTests covers default and Web serializer shapes and a
null-outcome outline fixture. Historical files/hashes are not modified. Provider
calls: 0; Gold mutation: 0.

## Verification

Tested against clean tracked baseline `0b0f386` plus this checkpoint's new tests,
excluding the working tree's H3 research edits and untracked research files.

- Release solution build: PASS (incremental, 0 errors/warnings in that invocation;
  preceding test-project compilation still reported existing warnings).
- Focused compatibility/materialization/hierarchy/parity/prompt/projection suite:
  71/71 PASS, 0 failed/skipped. Includes 8 new compatibility cases.
- Final focused TRX SHA256:
  `03a095fa40898b3ff5c5598882278fe12dbbed61ac50fdc7d8aadbd4d540025d`.
- `git diff --check`: PASS. No production-code changes in this checkpoint.
- Full suite and GitHub CI: not rerun here; the earlier testhost-crash limit is
  documented in heading-contract-retirement-audit.md. No full-suite PASS claim.

The first new regression run failed 2 cases because it assumed standalone
StructuralSourceOccurrence serialization was supported. The tests and compatibility
assessment were corrected to record its pre-existing required/JsonIgnore constraint;
production serialization was not changed to make the tests pass.
