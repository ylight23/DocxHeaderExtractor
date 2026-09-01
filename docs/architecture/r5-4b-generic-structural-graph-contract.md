# R5-4B Generic Structural Graph Contract

Status: PASS

R5-4B promotes validated relations to the generic structural graph authority without expanding the element taxonomy. The closed element set remains `Title`, `Subtitle`, and `Heading`.

## Authority contract

- `StructuralRelationProposal` is the untrusted relation input.
- `StructuralRelationProposalValidator` validates structural endpoints, rejects self references, rejects unsupported relation types, and rejects conflicting parents.
- `ValidatedStructure.Relations` contains only materialized, validated relations.
- `ValidatedStructuralElement.ParentId` is a compatibility projection of validated `ParentChild` relations; it is not an independent authority.
- Quarantine filters structural elements and relations, then reconstructs the graph from surviving validated relation proposals. It does not rebuild authority from `ParentId`.

The relation shape is extensible for future relation types such as `CaptionOf`, `ContinuationOf`, and `References`; no new relation or element type is introduced in this checkpoint.

## Execution

```ini
executionRevision = 9bc2fbf63d04f19d25db971d16c8c2f43da15c08
publicationRevision = containing-closure-commit
providerCalls = 0
expectedChanged = false
```

Release build, focused structural authority tests, host E2E, replay, and full-suite verification were run at the execution revision. `git diff --check` passed and runtime-generated tracked files were restored before publication.

## Gates

```ini
RELATION_ENDPOINT_UNJOINED = 0
DANGLING_RELATIONS = 0
RELATION_AUTHORITY_SOURCE = validated relation proposal
PARENT_ID_DUAL_AUTHORITY = 0
STRUCTURAL_ELEMENT_TYPE_EXPANSION = 0
PRODUCTION_BEHAVIOR_DELTA = 0

REPLAY_028_056_091 = 3/3
REPLAY_DELTAS = 0
HOST_E2E = 2/2
HOST_FINGERPRINT_CHANGED = false
HOST_FINGERPRINT = 16284414abee710236b27fe92f710b95efb32928e169ba0e1ede2e63891b8429
RELEASE_BUILD = PASS
DIFF_CHECK = PASS
```

Replay preserved final structure, output decisions, product output, and projected headings for fixtures 028, 056, and 091. The deterministic replay made zero provider calls.

## Full suite

The current tree measured 832 tests: 830 passed, 2 failed, and 0 skipped. The two failures remain the frozen C1 and N15 failures with their existing fingerprints; no new failure or changed fingerprint was introduced.

```ini
FULL_SUITE = 832/830/2/0
NEW_FAILURES = 0
CHANGED_FINGERPRINTS = 0
UNJOINED_FAILURES = 0
FROZEN_FAILURES = C1,N15
```

R5-4B is closed. R5-4C is authorized for taxonomy work; no taxonomy expansion is included here.
