# R5-3E - PDF HeadingRecord producer contract retirement

## Status

R5-3E = PASS

The normal PDF producer now returns a structural-authority envelope. Its production result contains
`StructuralAuthorityResult`, `PdfFinalStructure`, output decisions, audit, and detached tasks; it does
not contain `HeadingRecord` or `IReadOnlyList<HeadingRecord>`.

The unregistered narrow PDF probes retain their old heading-shaped output only through
`PdfCompatibilityHeadingOracle`. This is compatibility/evaluation output and is not consumed by the
normal authority route.

## Revision authority

- execution revision: `1b3471bb90a3f2c40ec1e7e3b702fc76c506d797`
- publication revision: `containing-closure-commit`
- preceding R5-3D publication: `406c4936d0d5d68837b08c2f0362dc6ae4c2cdfc`

## Contract census

- normal PDF producer `HeadingRecord` result: 0
- normal PDF producer `.Headings` callers: 0
- `PdfTextbookOutlineResult` heading collection: 0
- normal authority route consumes `result.Authority`: PASS
- compatibility/evaluation oracle: `PdfCompatibilityHeadingOracle`
- semantic selection, span resolution, hierarchy, output policy, and taxonomy: unchanged

CLI PDF stage and hierarchy-facts evaluation paths project the generic authority through
`HeadingOutlineProjection`; they do not read a heading collection from the production result.

## Verification

- Release build: PASS
- contract test for structural result shape: PASS
- frozen replay 028/056/091: 9/9 PASS
- replay structure/decision/product/projected-heading deltas: 0
- host E2E: 2/2 PASS
- host fingerprint: `16284414abee710236b27fe92f710b95efb32928e169ba0e1ede2e63891b8429`
- provider calls: 0
- `git diff --check`: PASS

## Full suite

The full suite was run without a filter at the exact execution revision:

- total: 829
- passed: 827
- failed: 2
- skipped: 0
- new failures: 0
- changed failure fingerprints: 0
- unjoined failures: 0

The two failures are the frozen C1 and N15 diagnosis probes. Their FQNs and failure messages match
the preceding R5-3D full-suite execution; neither is classified as a new failure or resolved by
this contract migration.

## Authorization

R5-3E = PASS
R5-3 = PASS
R5-4 = AUTHORIZED

Raw TRX and runtime side effects are retained outside the repository under
`C:\DocxHeaderExtractor-r5-3e-evidence\runtime-1b3471b` and are not tracked.
