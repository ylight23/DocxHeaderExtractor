# Round 5 Merge Readiness

Round 5 closes the generic structural authority and deterministic downstream consumer path.

## Revisions

- Base revision: `2951a2d5f5420efee0babfb17e5411af478b219e`
- Execution revision: `17dc586e01275255a0c1857cdadf6c8b367d7119`
- Publication revision: containing closure commit

The execution revision is the exact branch tip used for the final Release build, focused
verification, and unfiltered full-suite execution. This publication contains no source or test
behavior change.

## Architecture gates

- Normal DOCX and PDF routes use `ValidatedStructure`.
- Generic validation has no domain dependency or direct domain hierarchy authority.
- Structural element identity is independent from source identity.
- Source catalogs are parser-owned; structure-to-source reconstruction is `0`.
- Sections and chunks are source-backed; chunk text is not invented.
- Retrieval, search-index, and IE projections are deterministic and do not depend on
  `HeadingRecord` or Slim types.
- Structural types are `Title`, `Subtitle`, `Heading`, `ListItem`, `Caption`, `TableTitle`,
  `FigureTitle`, `Figure`, and `Table`.
- Structural relations are `ParentChild`, `CaptionOf`, and `Labels`.
- Source and relation endpoint joins are complete; dangling relations are `0`.

## Compatibility and runtime verification

- Replays `028/056/091`: `3/3`, structure/decision/product/final-heading deltas `0`.
- Host E2E: `2/2`, unchanged fingerprint:
  `16284414abee710236b27fe92f710b95efb32928e169ba0e1ede2e63891b8429`.
- Provider calls: `0`.
- Release build: PASS.
- `git diff --check`: PASS.

## Final full suite

The unfiltered suite was executed at the exact execution revision:

- `859 total / 857 passed / 2 failed / 0 skipped`
- Frozen failures: `C1`, `N15`
- New failures: `0`
- Changed failure fingerprints: `0`
- Unjoined failures: `0`

The two failures remain frozen known failures. They are neither rebaselined nor counted as
resolved by retirement.

## Result

`R5-1` through `R5-6`: PASS

`ROUND5 = PASS`

`MERGE_READY = true`

The branch is ready for one clean `--no-ff` merge into `main`. No post-merge full-suite rerun is
required when the merge tree is identical to this publication tree.
