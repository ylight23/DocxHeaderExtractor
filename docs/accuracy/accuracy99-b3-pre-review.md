# Accuracy-99 B3 Pre-Review Closure

Status: `HUMAN_REFERENCE_REQUIRED`

Execution revision: `a31a78258d1ec3684bdabd411d3fe3a18f2d44f8` (`feat: close A99 pre-review identity and support gates`)

This checkpoint separates physical parser source identity from logical heading occurrence identity.

- Physical source identity is scoped by `documentId + sourceId`.
- Heading identity is `sourceId@headingSpan.start:headingSpan.end`.
- Multiple headings in one physical source are legal; duplicate exact source/span pairs are rejected.
- Parent and hierarchy joins use heading occurrence IDs, never source-only grouping.
- The v3 Gold contract, reviewer output, evaluator, and DOC-0205 representation canary use the same occurrence identity.

The DOC-0205 reconciliation remains fail-closed: 71 retained references are unresolved because exact reference spans are not retained. The synthetic representability canary passes, but no historical reference is promoted to Human Gold.

The DOC-0027 result is an audit-only support canary with `referenceStatus=NOT_AVAILABLE`, `DocumentMode=Unknown`, and `reliabilityStatus=SUPPORT_NOT_PROVEN`. DOC-0022 remains sealed as `GENERALIZATION_HOLDOUT`.

Verification:

- Focused gate: `372/372` passed.
- Release build: passed.
- Full suite: `1111` total, `1110` passed, `1` frozen `N15` failure, `0` skipped.
- New failures: `0`.
- Provider calls: `0`.
- Valid Human Gold v3 documents: `0`.

The v3 reviewer is ready at `C:\A99-Gold\reviewer-v3\index.html` and the authorized DEV review directory is `C:\A99-Gold\dev-v3`. The next action is independent human review; no accuracy claim is made before that review.
