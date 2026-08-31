# R4-7 Slim Consumer Migration

Status: PASS

R4-7 migrates executable consumers to the native source and policy contracts. It does not
physically remove `DocxSlimExtractor`, `SlimDocument`, `SlimParagraph`, or the compatibility
boundary; those are R4-8 through R4-10 work.

## Revision

- Base closure: `d9eaf830971cf645bd4251a9b35d1842edc84f0c`
- Migration execution: `a4c36fa9d4209798e04b65c02dda4dec019b9991`
- Branch: `round4/legacy-free-authority-runtime`

## Census

The census was rerun on the current branch. CLI, Web, Eval, Repair, PDF audit, PDF writeback,
and source-inspection paths no longer construct `DocxSlimExtractor` or call its extraction APIs.
They now build or receive the smallest applicable native contract:

- source facts: `SourceDocument`
- numbering and style facts: `NumberingStyleFeatures`
- candidate and policy state: `DocxPolicyState`
- diagnostics: native paragraph contracts and native diagnostic APIs
- output and writeback: source/policy state plus product structures

| Gate | Result |
| --- | ---: |
| `EXECUTABLE_DOCX_SLIM_EXTRACT_CALLERS` | 0 |
| `CLI_SLIM_RUNTIME_CALLERS` | 0 |
| `WEB_SLIM_RUNTIME_CALLERS` | 0 |
| `REPAIR_SLIM_CALLERS` | 0 |
| `EVAL_EXECUTABLE_SLIM_CALLERS` | 0 |
| `UNKNOWN_CALLERS` | 0 |
| `EXPECTED_CHANGED` | false |
| `PROVIDER_CALLS` | 0 |

The remaining direct Slim references are confined to legacy owners, compatibility adapters, and
tests. In particular, `HeaderExtractionPipeline` still owns its historical implementation and
is intentionally deferred to R4-9. No new native path creates a Slim object or invokes the old
algorithm.

## Validation

- Release solution build: PASS, 0 errors.
- Focused native and migration set: 74 passed, 0 failed, 0 skipped.
- The broader tagged/route probe was also run against the clean `d9eaf83` baseline. Its existing
  tagged coverage and `pdf-first-authority-v1` route failures reproduce there, so they are not
  R4-7 regressions.
- Full suite: not run by design.
- Physical legacy retirement: not started.

R4-7 is closed. R4-8 may now migrate the remaining compatibility-boundary callers; physical
deletion remains gated on the later zero-reference proof.
