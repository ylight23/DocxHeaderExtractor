# R5-4C1 Generic Structural Taxonomy

Status: PASS

R5-4C1 expands the generic structural contract without activating new producer behavior. The supported element types are now `Title`, `Subtitle`, `Heading`, `ListItem`, `Caption`, `TableTitle`, and `FigureTitle`. Existing producers continue to materialize only the original three types.

## Contract changes

- Structural type/role compatibility is validated as schema consistency, with no domain-specific authority.
- Accepted mappings include `ListItem` with `ListItemTopic`, `Caption` with `Caption`, and `TableTitle` or `FigureTitle` with `Caption`.
- Invalid combinations such as `FigureTitle` with `SignatureLabel` are rejected before materialization.
- New types use the same source and exact-span validation as existing structural elements.
- New types can participate in validated `ParentChild` graph relations.
- `HeadingOutlineProjection` remains restricted to `Title`, `Subtitle`, and `Heading`; new types cannot enter the legacy heading API.

No producer was changed to emit a new type, and no taxonomy-specific extraction heuristic was added.

## Execution

```ini
executionRevision = 935e0cd91027c866a6feb15ce6728c5176184ba5
publicationRevision = containing-closure-commit
providerCalls = 0
expectedChanged = false
```

Release build, focused contract/authority tests, host E2E, deterministic replay, and the required full suite were run at the execution revision. Runtime-generated files were kept outside the repository and tracked side effects were restored before publication.

## Gates

```ini
STRUCTURAL_TYPES = Title,Subtitle,Heading,ListItem,Caption,TableTitle,FigureTitle
TYPE_ROLE_INVALID_ACCEPTED = 0
NEW_TYPE_SOURCE_GROUNDING = PASS
NEW_TYPE_SPAN_VALIDATION = PASS
NEW_TYPE_RELATION_VALIDATION = PASS
HEADING_PROJECTION_NEW_TYPES = 0
PRODUCTION_NEW_TYPE_EMISSION = 0

REPLAY_028_056_091 = 3/3
REPLAY_DELTAS = 0
HOST_E2E = 2/2
HOST_FINGERPRINT_CHANGED = false
HOST_FINGERPRINT = 16284414abee710236b27fe92f710b95efb32928e169ba0e1ede2e63891b8429
PROVIDER_CALLS = 0
RELEASE_BUILD = PASS
DIFF_CHECK = PASS
```

## Full suite

The current tree measured 841 tests: 839 passed, 2 failed, and 0 skipped. The only failures remain the frozen C1 and N15 failures with their existing fingerprints.

```ini
FULL_SUITE = 841/839/2/0
NEW_FAILURES = 0
CHANGED_FINGERPRINTS = 0
UNJOINED = 0
FROZEN_FAILURES = C1,N15
```

R5-4C1 is closed. R5-4C2 is the next checkpoint for deliberate producer activation of the new taxonomy.
