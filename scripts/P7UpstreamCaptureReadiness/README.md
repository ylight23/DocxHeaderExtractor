# P7 upstream capture readiness (provider-free)

This command inventories the two existing P7 upstream diagnostic receipts and checks
their referenced raw hashes against the current pilot request/source freeze. It writes
a new sanitized readiness manifest. It never sends requests, imports decisions into
the pilot baseline, reads Gold rows, or reparses PDFs.

```powershell
dotnet run --project scripts/P7UpstreamCaptureReadiness -- <frozen-F1-dir> <full-source-pack> <diagnostic-capture-root> <new-receipt.json>
```

## Capture contract

`capture-receipt.schema.v1.json` describes successful raw captures. The C# receipt reader
rejects unknown/duplicate/missing fields; the validator checks source/request identities,
raw and parsed hashes, observation consistency, total ledgers and finish/attempt policy.
Raw files and observation (including usage if reported) stay private and hash-frozen.
The source mapping stays alongside them. Parsed stage decisions must be preserved too;
decision-only rows without their raw receipt are not an upstream authority.

Required order: approved attempt reservation → capture raw body/response/SSE/observation
→ raw freeze → strict parse → parsed decision and receipt freeze → downstream generation
→ request body/identity/parent hashes freeze → separate execution authorization.
The hash-addressed endpoint metadata observation is a pin of published metadata, not
proof of hidden weights or tokenizer revision. Protocol validity is not semantic truth.

Missing input is an explicit missing-capture error, not an empty ledger. Failed, partial,
non-stop, invalid, repaired/retried/fallback captures are quarantined with their original
attempt receipt and whatever raw bytes exist. They cannot drive downstream generation.
An actual valid total all-OTHER F1, or all-NO G2A, correctly suppresses later calls.
Synthetic fixtures are permitted only in tests, never as production baseline captures.

## Independent experiment modes

| Mode | Upstream source | Metric namespace |
| --- | --- | --- |
| ControlledDownstream | Control F1/Control G2A for both arms | CONTROLLED_STAGE_ISOLATED |
| NaturalEndToEnd | Each arm's own F1/G2A | NATURAL_END_TO_END |

No mixing of these metrics. Natural requests may have different issued universes.
The generation recipes use parsed stage decisions only; they never use analysis text,
Gold, review roles or geography to decide eligibility. Existing F1/G2A/H2-C responsibilities
and deterministic parsers stay unchanged. Context does not become selectable.

Capture/generation contracts are `CONDITIONALLY_FROZEN`: actual downstream bodies and
counts require real accepted upstream. Freeze each actual request before authorizing it.
No synthetic or Gold-driven requests are made to fill this dependency gap.

## Bounds, sizing and budget

All 643 issued atoms (including 430 unscored atoms) count toward conservative caps:

- F1 only: 14 fresh calls, already byte-frozen (7 Control + 7 B).
- Either complete mode: at most 14 F1 + 14 G2A + 1,286 H2-C = 1,314 calls.
- Both modes sharing only F1 roots: at most 14 + 28 + 2,572 = 2,614 calls.

These are loose source-only capacity bounds, not a requested/approved execution budget
and not an expectation that all atoms will be admitted. No automatic large experiment
is authorized. No implicit reuse or downstream deduplication is assumed.

F1 UTF-8 bytes are measured exactly; downstream payload sizes remain unknown until real
upstream. Bytes are not a token upper bound. Completion ceilings bound requested generation,
not verified billing. No monetary budget can be claimed before pinned-endpoint usage
calibration and a separately approved spend/call limit. Tokenizer mapping remains blocked.

Historical byte-compatible captures are only reuse candidates: source-review exposure,
timing, missing endpoint-observation pin and provenance must be disclosed. Adoption requires
an explicit reuse-policy amendment. The existing request manifest and historical receipts
are never rewritten. Do not silently subtract old calls from a fresh execution plan.

Provider execution and production promotion remain locked. D2.3 is not an inference run.
