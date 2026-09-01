# R5-4A - Domain authority decoupling

## Status

R5-4A = PASS

`DocumentDomainPolicy` remains the detector for document-family signals, but its output is now
`DomainStructuralEvidence`. The evidence may propose a domain role, level, structural role, or
outline exclusion; it does not materialize or validate a `ValidatedStructure`.

The production flow is:

`domain detector -> DomainStructuralEvidence -> candidate/proposal and product policy -> generic structural authority`

The generic `StructuralProposalValidator` has no reference to `DocumentDomainPolicy` or
`PdfDomainRole`. Candidate admission, hierarchy fallback, visual recovery, conflict handling, and
output policy consume detector evidence instead of calling domain authority methods directly.

## Evidence boundary

- domain role and level mappings are detector evidence, not validated structure
- domain exclusion is an exclusion proposal carried into product policy
- `PdfValidatedStructure` and `PdfFinalHeading` retain the proposal as non-serialized metadata
- legacy serialized facts can rehydrate the proposal for compatibility without creating authority
- no new taxonomy beyond `Title`, `Subtitle`, and `Heading` was introduced
- no file-specific or corpus-ID runtime rule was added

Compatibility calls that remain in tests and the evaluation-only legacy projection are outside the
normal authority route and are retained to exercise or reproduce historical behavior.

## Verification

- focused domain/structure suite: 49/49 PASS
- Release build: PASS
- replay 028/056/091: 9/9 PASS
- replay structure/decision/product/projected-heading deltas: 0
- host E2E: 2/2 PASS
- host fingerprint: `16284414abee710236b27fe92f710b95efb32928e169ba0e1ede2e63891b8429`
- provider calls: 0
- `git diff --check`: PASS
- generic validator domain dependency: 0
- production direct domain level/parent/exclusion calls: 0

## Full suite

The full suite ran at the exact execution revision without filters:

- total: 830
- passed: 828
- failed: 2
- skipped: 0
- new failures: 0
- changed failure fingerprints: 0
- unjoined failures: 0

The failures are the frozen C1 and N15 diagnosis probes, with the same FQNs and messages as the
R5-3E full-suite execution. They were not changed or reclassified.

## Revision authority

- execution revision: `4d8fe1a0df053369bbcd7763c5a6c6abff8ce2c8`
- publication revision: `containing-closure-commit`
- preceding R5-3E publication: `3c0c17fdc801da5c2c749f11d7f43c5652c0e3db`

R5-4A = PASS
R5-4B = AUTHORIZED
