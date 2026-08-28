# VERIFY-6D DocumentDiagnosticRunner Provenance

Status: `DIAGNOSED_BUT_TREE_PROVENANCE_INCOMPLETE`

## Exact Failure

- FQN: `DocxHeaderExtractor.Tests.DocumentDiagnosticRunnerTests.Pipeline_tra_diagnostic_report_trong_outline`
- Source: `tests/DocxHeaderExtractor.Tests/DocumentDiagnosticRunnerTests.cs:75`
- Expected: an accepted `auto:rfc-toc-dictionary` diagnostic candidate with `BodyAnchorRatio >= 0.90`.
- Actual: no matching candidate; the retained route evidence reports `pdf-first-authority-v1`.
- Retained failure fingerprint: `d70614ef1bf1c7079d06a20aae2dcbed2e85852024fb3cfff7562e5cd19789ec`.

## Tree Comparison

The repository contains retained failure packets, but not independent checkout directories for
VERIFY-3, RFC-closed, or the architecture pre-integration tree. Their existence and pass/fail
outcomes are therefore `NOT_OBSERVABLE`. VERIFY-5 combined is observed failing. The same FQN and
assertion line also occur in the retained baseline packet, so `failsOnlyAfterComposition=false`.

This does not prove a composition-only failure, a contract rollback, or a production regression.
The expected and production-path logic were introduced together in `3fbd3f6a`; no later known
correction commit for this exact assertion is retained.

## Decision

Classification: `UNKNOWN`.

`ROOT_CAUSE=UNKNOWN` and `REMEDIATION_JUSTIFIED=NO`. No expected assertion, production code, or
provider behavior was changed. VERIFY-6B remains gated until VERIFY-6A plus the missing tree-level
provenance are available.

`PROVIDER_CALLS=0` and `PRODUCTION_CODE_CHANGED=false`.

Output artifact: `eval/verification/document-diagnostic-runner-provenance.v1.json`.
