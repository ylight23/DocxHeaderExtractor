# Frozen inference response / observation separation

This is a separate API refactor after the S12–S15 cleanup closure verified at `c5dec2e`.
It does not reopen source ownership cleanup or change heading authority behavior.

## Ownership

- DocumentProcessing owns `FrozenInferenceResponse(Content, FinishReason)` and the neutral
  `IFrozenInferenceTransport.ExecuteFrozenRequestAsync` contract.
- Infrastructure's internal OpenRouter engine retains transport telemetry and SSE parsing.
  The production facade returns only the two runtime response fields.
- V5Qualification owns the public `OpenRouterExecutionObservation` with `Content`, `FinishReason`,
  `Usage`, `RawSse`, `SseEventCount`, and `RetryCount`. Its `ExecuteObservedAsync` API remains the
  capture/forensic seam; its frozen runtime interface returns the minimal response.

The lease-bound production wrapper, authorized PDF wrapper, F1/G2A/H2-C authority, and historical
membership adapter consume the minimal response. Qualification capture runners use observations
and project only content/finish reason back to the semantic authority, retaining their existing
raw capture fields. Production does not gain a reference to V5Qualification.

## Deliberate public API change

`FrozenInferenceResult` is retired, not kept as a compatibility facade. External implementations
of `IFrozenInferenceTransport` must change their return type to `Task<FrozenInferenceResponse>`
and construct the two-field response. Consumers that need diagnostics must use an observation
API rather than add diagnostics back into DocumentProcessing.

This is a source/binary API break and a response DTO serialization-shape change. It is not a claim
of parity for JSON serialized from the retired DTO. Existing frozen provider requests, historical
capture files, Gold, and serialized extraction/audit contracts are not rewritten or rebaselined.

## Behavior and regression gates

No system prompt, user message, provider-body composer, token budget, sampling setting, parser,
lease lifecycle, transport retry policy, or authorization gate changes in this refactor. The engine
uses the same observation execution path and projects its response after transport completes.
Provider telemetry remains internal; this does not remove SSE evidence collection inside the engine.

Provider-free facade regressions assert:

- The runtime response exposes only content and nullable finish reason; the old DTO is absent.
- Full diagnostic fields remain on the qualification observation in its separate assembly.
- Runtime and observation APIs send identical supplied UTF-8 bytes, including escaping-sensitive
  characters, without recomposition.
- Successful and `length` finishes preserve content/finish reason; a completed `length` response
  is not retried.
- A transient mock HTTP failure preserves exact request bytes on retry, and qualification retains
  raw SSE, event count, usage/reasoning tokens, and retry count.

Existing byte-parity, PDF provider policy, reachability, and timeout/cancellation tests migrate to
the minimal response without changing their semantic expectations. All transport tests use mock
HTTP or frozen replay; no live provider call is authorized by this refactor.

## Verification

Tested source revision: `caa038b105d5f613c7b57a3f66daf5a898f84b3c`.
Verification uses a clean tracked Windows checkout, excluding the main workspace's modified H3
preflight and untracked research tests. Provider keys and the provider sentinel were blank;
`A99_FREEZE_UPDATE=0`.

- Clean tracked Release solution build: PASS, 0 errors, 34 existing warnings.
- Focused transport/byte-parity/lifecycle/provider-policy/reachability/architecture regressions:
  PASS, 61/61, 0 failed/skipped.
- `git diff --check`: PASS. Provider calls: 0. Gold mutation: 0. Frozen rebaseline: 0.
- Official Windows `CoreDeterministic` tier: PASS, 1056/1056, 0 failed/skipped, 17m13s.
- Verification checkout after the deterministic suite: clean.

Commands: `dotnet build -c Release`; focused `dotnet test -c Release --no-build`;
`pwsh -NoProfile -File scripts/Invoke-A99TestTier.ps1 -Tier CoreDeterministic -Configuration Release -NoBuild`.
The official tier manifest is unchanged; no ad-hoc exclusions or frozen artifact updates were used.

CI for this API refactor is not yet verified. The prior `c5dec2e` CI establishes the earlier cleanup
closure, not verification of this subsequent API change.
