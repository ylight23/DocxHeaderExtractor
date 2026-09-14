# A99 V4F-A — frozen identity provider-scale audit

Status: **OFFLINE_PROVIDER_SCALE_AUDIT_COMPLETE**

No model/provider calls were made. Gold and V4E-B evaluation artifacts were not read. Raw request bodies were reconstructed and hashed only; no raw request archive was written.

## Frozen inputs

- V4E shortlist/request universe: **7,702** requests; exact builder reconstruction: **PASS**; request SHA/byte mismatches: **0**.
- Source catalog and V4E manifest/shortlist/request hashes matched their frozen manifest.
- Provider/model configuration: OpenRouter / `qwen/qwen3.7-flash`; configured context limit `1,000,000` tokens; provider-verified limit: **UNKNOWN**.

## Request size

- Total logical request bytes: **7,070,469,069**. Estimated input tokens use `ceil(UTF-8 bytes / 4)` and are explicitly estimated.

| Document | Calls | Min bytes | P50 bytes | P95 bytes | Max bytes |
|---|---:|---:|---:|---:|---:|
| DOC-0123 | 3,445 | 1,288,362 | 1,288,406 | 1,288,408 | 1,288,412 |
| DOC-0133 | 2,977 | 753,434 | 753,438 | 753,438 | 753,511 |
| DOC-0252 | 1,280 | 303,882 | 303,884 | 303,884 | 303,888 |

## Payload and context

The exact decomposition is recorded in `payload-decomposition.json`; pair-specific target bytes, document-shared node context bytes, and residual serialization envelope sum exactly to total request bytes. Static prompt/provider envelope are not present in the canonical builder body and are recorded as zero for this audit.

Context classification is recorded in `context-validation` fields inside `manifest`/report artifacts: configured-limit based only, not provider verified.

## Call scale

- Calls: **7,702**; one call per frozen candidate. Candidate endpoint degree percentiles and per-document density are in `call-count-distribution.json`.
- Per-document counts: DOC-0123=3,445, DOC-0133=2,977, DOC-0252=1,280.

## Workload/time/cost

- Minimal/typical/configured-maximum output scenarios are estimates only; no previous outputs were inspected.
- Wall-time scenarios at 1/2/5/10/25/50 requests/sec make no concurrency assumption and are in `execution-scenarios.json`.
- Cost: **COST_UNKNOWN**; no authoritative local price snapshot and no provider query.

## Decision

Primary diagnosis: **REQUEST_CONTEXT_DOMINANT**. The recommended single next experiment is **READY_FOR_V4F_CONTEXT_PROJECTION_EXPERIMENT**. This audit authorizes no provider execution and implements no follow-on experiment.

## Firewall

`GoldReadCount=0`; `V4EBEvaluationReadCount=0`; `MODEL_CALLS=0`; `PROVIDER_CALLS=0`; `V4E-B consumed=false`; request mutation=false; raw request archive=false.

## Artifacts

- `manifest.json`
- `request-size-distribution.json`
- `payload-decomposition.json`
- `call-count-distribution.json`
- `token-workload.json`
- `cost-estimate.json`
- `execution-scenarios.json`
- `artifact-volume-estimate.json`
- `firewall.json`

Input verification failures: 0; diagnosis is source/request-scale evidence only, not an accuracy claim.
