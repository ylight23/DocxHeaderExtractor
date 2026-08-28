# INT-3 Round-2 Artifact Delta Reconciliation

Status: `PASS`

Target branch: `integration/final-authority-clean-architecture`

Round-1 canonical revision: `92cd2d6d3cba29986858d30a91d5da0468044cff`

Focused execution revision: `92ed397e0538de06cbdee9d700f0640f3b4c2bb2`

Artifact publication revision: `052688b35ed76b5e175821543939278fa8eea960`

## Revision Semantics

`HEAD_VERIFIED = 92ed397e...` in the INT-2 artifact is not stale. It is the
revision where the focused build/test evidence was produced. `052688b...` is
the later publication commit that added the INT-2 ledger itself.

This reconciliation therefore keeps two identities:

- `executionRevision = 92ed397e...`
- `artifactPublicationRevision = 052688b...`

No bulk rewrite from `92ed397e` to `052688b` is justified.

## Delta

The range `92cd2d6..052688b` contains 27 commits. Within `src/`, `tests/`,
`docs/`, and `eval/`, 61 files changed.

The docs/eval evidence delta is additive: 50 docs/eval files were added, with
no docs/eval modifications or deletions in this range.

Source/test files of interest include the ARCH-4P/Q/R source boundary changes
and the hierarchy authority probe tests:

- `BuiltInHeadingStyleIdentity.cs`
- `NumberingStyleFeatures.cs`
- `SourceDocument.cs`
- `DocxSourceFactsBuilder.cs`
- `HeadingHeuristics.cs`
- `SlimSourceFactsAdapter.cs`
- `RepairCorpusAudit.cs`
- `NumberingStyleFeatureBoundaryTests.cs`
- `PdfG0HierarchyHumanAuthorityPacketProbe.cs`
- `PdfG1HumanHierarchyAnnotationProbe.cs`
- `PdfG1aHumanHierarchyPilotExecutionProbe.cs`

No `bin/`, `obj/`, `TestResults/`, `.trx`, or `.env` file appears in the delta.

## Reconciled Evidence

INT-2 focused gates are complete and traceable:

- RFC: `5/5 PASS`
- RFC-2: `67/67/0/1.0 PASS`
- MCP: `7/7 PASS`
- F regression: `2/2 PASS`
- ARCH-4P: `PASS`
- ARCH-4Q: `PASS`
- ARCH-4R: `PASS`
- Release: `PASS`

The VERIFY-6C materialized artifact set is also internally consistent:

- manifest entries: `683`
- authority resolved: `683`
- present/readable: `683/683`
- authority hash match: `683`
- hash mismatches after MAT: `0`
- unresolved provenance: `0`
- `VERIFY_6C_READY = true`

## Boundary

`eval/verification/canonical-integrated-full-suite.v1.json` remains a valid
Round-1 canonical full-suite baseline for `92cd2d6`, with `1326 total`, `1296
passed`, `30 failed`, `0 skipped`, `NEW_FAILURES = 0`,
`CHANGED_FINGERPRINTS = 0`, and `UNJOINED = 0`.

It is not reused as the Round-2 full-suite result because the Round-2 delta adds
source, tests, and authority artifacts after `92cd2d6`. The next task must be
`INT-4 -- Round-2 Canonical Full Suite`.

## Closure

`FULL_SUITE_EXECUTED = false`

`PROVIDER_CALLS = 0`

`PRODUCTION_CODE_CHANGED = false`

`TEST_EXPECTED_CHANGED = false`

`REMEDIATION_PERFORMED = false`

`readyForInt4 = true`
