# R5-4C3B-D Figure/Table Structural Semantics

R5-4C3B-D activates the figure/table semantic lane on the generic structural
authority contracts. The PDF semantic roles `figure_title` and `figure_caption`
map to `FigureTitle` and `Caption` respectively. Both are represented as
structural proposals and materialized only through `StructuralProposalValidator`.
`FigureTitle` is never inferred from `FigureCaption`.

The batch also adds source-grounded `Figure` and `Table` container nodes. A
container must be supplied by parser/layout evidence with its own source
identity and span; title or caption text is not used to infer a container.
Non-heading proposals use an explicit semantic source span when one is
available. Otherwise a full-block span is retained only when the proposal
identifies the whole block as the structural element.

Relations are graph authority, not a side effect of `ParentId`:

- `FigureTitle --Labels--> Figure`
- `Caption --CaptionOf--> Figure`
- `TableTitle --Labels--> Table`

Each relation passes through `StructuralRelationProposalValidator`, including
endpoint existence and type compatibility. `Caption --CaptionOf--> FigureTitle`
is rejected. Heading projection continues to include only `Title`, `Subtitle`,
and `Heading`; the new non-heading types cannot leak into the compatibility
heading API.

## Verification

- Base revision: `cd80a1a89f831b427ab3ac8164aa9dfb81cca3c7`
- Execution revision: `44cd42001b29ea9bd1371358dedfc438c5b69d73`
- Publication revision: `containing-closure-commit`
- Focused semantic/authority suite: `91/91` passed
- Host E2E: `2/2` passed
- Host fingerprint: `16284414abee710236b27fe92f710b95efb32928e169ba0e1ede2e63891b8429`
- Host fingerprint changed: `false`
- Deterministic replay `028/056/091`: joined `3/3`; structure, decision, product,
  and final-heading deltas `0`
- Provider calls: `0`
- Release build: `PASS` (`0` warnings, `0` errors)
- `git diff --check`: `PASS`

The contract-backed producer lane emitted one each of `FigureTitle`, `Caption`,
`TableTitle`, `ListItem`, `Figure`, and `Table`. It validated two `Labels`
relations and one `CaptionOf` relation, with zero endpoint-unjoined relations,
dangling relations, invalid spans, or validator bypasses. `FigureTitle` was not
materialized as `Caption`, and `Caption` was not materialized as `FigureTitle`.

The unfiltered full suite at the exact execution revision measured `852 total,
850 passed, 2 failed, 0 skipped`. The failures are the frozen C1 and N15
diagnostic probes. Reconciliation reports `NEW_FAILURES=0`,
`CHANGED_FINGERPRINTS=0`, and `UNJOINED_FAILURES=0`; no expected value or frozen
failure was rebased.

## Closure

`R5-4C3B = PASS`, `R5-4C3C = PASS`, `R5-4C3D = PASS`, and `R5-4C3 = PASS`.
The next architectural work is section/chunk projection and generic document
extraction output. `CaptionOf` remains limited to a real `Figure` container;
additional relations or producer taxonomy require their own evidence and gate.
