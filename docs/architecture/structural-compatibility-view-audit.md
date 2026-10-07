# S4: Core structural compatibility views

Baseline: `1e10e40`. Decision: retain both views deliberately; no public/wire retirement.

| View | Authority source | Live behavior | S4 decision |
| --- | --- | --- | --- |
| ValidatedStructuralElement.ParentId | ValidatedStructureFactory projects admitted ParentChild relations | Public JSON parentId; production structural projections do not read it to infer hierarchy | KEEP compatibility only |
| ValidatedStructure.OutlineElements | Same read-only list as Elements | Public default OutlineElements / Web outlineElements wire property | KEEP alias; production uses Elements |

## Reachability and identity

Prior to S4, HeadingOutlineProjection, CanonicalGroundingProjection and
StructuralSectionProjection read OutlineElements. All three now read Elements,
which is the identical ordered collection, not a newly filtered/reordered list.
Section parentage continues to come from Relations, not element.ParentId.

Other production ParentId usages belong to ResolvedHeadingHierarchy or product/final
heading DTOs, not ValidatedStructuralElement. The hierarchy resolver/materializer's
ProposedParentId path remains explicit; S4 does not rename or remove those contracts.

ParentId still has a public init setter for existing DTO consumers/deserialization.
A standalone DTO, including one with a deserialized ParentId, is not admitted graph
authority. Factory admission overwrites that view from explicit validated relations;
omitting relation proposals clears it. The factory does not mutate the incoming DTO.
This is not a recursive immutability or arbitrary graph rehydration guarantee.

## Compatibility decision

Deleting either property would change raw graph JSON and public API. They are not
dead symbols merely because graph semantics no longer depend on them. S4 deliberately
preserves names, getters, order, values and default/Web serialization. No Obsolete
attribute, JsonIgnore change, setter restriction or graph construction API is added.

StructuralValidation.Accepted is an existing JsonIgnore computed validation view;
StructuralDecision and the remaining structural proposal/source/validation data are
live contracts, not generic graph taxonomy residue. They are not retired in S4.
The data-only boundary permits pure compatibility getters; it does not import a
semantic service into DTOs.

Regression covers read-only alias identity, default/Web wire views, deserialized stale
parent admission and production projection ownership. Existing S1 graph and S2/S3 byte
fixtures remain unchanged. S5 validator dependency audit is a separate checkpoint.

## Verification

Verified in the isolated tracked verification checkout with S1/S2/S3 plus S4 changes;
user H3 edits and untracked research are excluded.

- Release solution build: PASS, 0 errors; existing test warnings remain.
- Focused compatibility/envelope/structural/projection/materialization/replay/containment/
  host E2E/PDF lifecycle/architecture/byte-parity/product suite: 116/116 PASS,
  0 failed/skipped.
- TRX SHA256:
  `b4f33b033475e53e13db2031eee28c88875958cffa83501f0f7de6ae1d500ab3`.
- git diff --check: PASS.
- Full Windows suite/GitHub CI not rerun for S3/S4; S2 CI success is not claimed
  as S4 verification.
- Provider calls: 0. Gold mutation: 0. Frozen artifact rebaseline: 0.
