# R5-3D Generic Structural Producer Replay Closure

Status: PASS

Execution revision: `85a9cff996bc36a82ec84713bfa51b2b6bd09d2a`
Publication revision: containing-closure-commit

## Replay gate

The frozen source-keyed replay cohort 028, 056, and 091 was executed at the
exact execution revision with an in-memory classifier. Current candidate
construction, semantic/span validation, grounding, hierarchy, final structure,
output decisions, product serialization, and heading projection all ran.

- Replay tests: 9 passed, 0 failed, 0 skipped
- Diagnostic/producer rows joined: 3
- Structure, decision, product, and heading deltas: 0
- Provider calls during replay: 0
- Classifier invocations: 102, all in-memory replay calls

The replay report is source-keyed and retains the baseline revision
`0b98ade75f4c5ada46d8af6b4c3fffd3e829d2b8`. Raw audit/checkpoint and TRX files
remain execution evidence outside the repository and are not published.

## Host gate

The deterministic host fixture was exercised through the canonical tool,
AgentHarness, MCP, Web, and CLI normal extraction surfaces.

- Host tests: 2 passed, 0 failed
- All five fingerprints joined
- Fingerprint: `16284414abee710236b27fe92f710b95efb32928e169ba0e1ede2e63891b8429`
- Unjoined host results: 0
- Provider calls: 0

## Full suite

The unfiltered Release full suite ran at the exact execution revision with no
test exclusions:

- Total: 828
- Passed: 826
- Failed: 2
- Skipped: 0

The two failures are the frozen known universe and retain their existing
fingerprints:

- `DocxHeaderExtractor.Tests.PdfC1CrossDocumentRegressionInventoryProbe.IndependentPartialTimeoutEvidenceNowExistsAndWasConsumedByN35`
- `DocxHeaderExtractor.Tests.PdfN15RankingLossDiagnosisProbe.CommittedDiagnosisReproducesByteForByte`

Reconciliation:

- New failures: 0
- Changed failure fingerprints: 0
- Unjoined failures: 0
- Known failures rebased: 0

## Production fix

`HeadingOutlineProjection` now preserves the canonical order already present
in `ValidatedStructure.Elements`. It no longer sorts by source ordinal, which
could reorder distinct PDF occurrences sharing one source paragraph. Generic
structural levels remain authoritative while compatibility projection preserves
the legacy heading level contract, including intentional null levels.

R5-3D is closed. R5-3E is authorized.
