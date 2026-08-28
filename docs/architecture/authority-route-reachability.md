# Authority Route Reachability Reconciliation

**Scope:** the 11 C2-P route-diversion failures.  
**Source revision:** `521ab902d89d4f3c8c7a68cb528b84ca6ebccfb2`  
**Mode:** audit-only; no provider calls, production changes, or test changes.

## Result

All 11 failures are reproduced through tests that directly construct the
legacy `HeaderExtractionPipeline`. None constructs the normal product chain:

```text
CLI / Web / MCP / AgentHarness
    -> PipelineDocumentExtractionTool
    -> AuthorityExtractionPipeline
```

Therefore the ARCH-2 route result is:

```text
normalAuthority = 0/11
evalOnly        = 11/11
fallbackReached = 11/11
```

The C1 ledger should not be edited by this audit. The recommendation is that
the 11 rows need route-aware reclassification: they are not evidence of a
normal-product `AuthorityExtractionPipeline` failure. They are evidence that
legacy/evaluation fixtures still pass through `HeaderExtractionPipeline` and
its `PdfFirstValidatedFallback` policy.

## First-loss groups

- Split-merged paragraph: 2 tests. The fallback returns before merged
  paragraph splitting is integrated.
- Critic preservation/anchor: 4 tests. The fallback diverts before critic
  preservation and anchor processing.
- Rolling outline: 4 tests. The fallback diverts before `BuildRollingOutline`.
- Slim reviewed-candidate projection: 1 test. The fallback returns before the
  heuristic-only review projection.

In every group, the first observed loss is route diversion, not the downstream
splitter, critic, rolling, or slim operation named by the test. This does not
prove that those downstream implementations are correct; it means these 11
failures do not test them under the current route.

## Reachability implications

`AuthorityExtractionPipeline` remains the normal product owner through
`PipelineDocumentExtractionTool` in Web, MCP, AgentHarness, and the primary CLI
path. The CLI also retains a direct `HeaderExtractionPipeline` construction for
compatibility/evaluation behavior. The 11 failing fixtures use the latter
shape directly from tests, so their route classification is `EVAL_ONLY` and
`legacyOnly=true`.

The separate `RfcTocDictionaryOutlineTests` failure is intentionally excluded:
it calls `RfcTocDictionaryOutline.Analyze` directly and is not a
`PdfFirstValidatedFallback` route-diversion failure.

## Decision

```text
C1_REAL_PRODUCTION_FAILURE_CLASSIFICATION = NEEDS_REVISION
production remediation from this packet = NOT_JUSTIFIED
```

No production fix or test-expectation update is justified by ARCH-2 alone. A
future decision would need either a normal-authority reproduction through
`AuthorityExtractionPipeline`, or an explicit decision to preserve and support
the legacy/evaluation contracts separately.

The machine-readable per-test evidence is in
`eval/architecture/authority-route-reachability.v1.json`.

```text
PROVIDER_CALLS = 0
PRODUCTION_CODE_CHANGED = false
TEST_EXPECTATIONS_CHANGED = false
```
