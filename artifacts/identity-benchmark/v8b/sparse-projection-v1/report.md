# V8B — source-only sparse retrieval + scalable projection

Status: **BLOCKED_ON_V8B_SCALABILITY**.

V8A2 remains immutable (`579b3fd`); the full candidate universe is preserved as diagnostic authority. No Gold, historical predictions, or provider calls were used.

- Full candidates: **102,915**
- Shortlist: **15,185** (85.25% reduction)
- Proposer shards: **8**; falsifier shards: **8**
- Total estimated input: **5,768,346 tokens** (inherited budget: **5,000,000**)
- Maximum request: **699,952 tokens / 2,799,805 bytes**
- Per-request safety ceiling: **700,000 tokens**

Omitted candidates remain `NOT_RETRIEVED / NO_PROPOSAL_PATH`; V8B does not infer `DISTINCT` from retrieval omission.

## Per document

| Document | Full | Shortlist | Reduction |
|---|---:|---:|---:|
| NEW-073DB817879B | 2,709 | 412 | 84.79% |
| NEW-26BE9B284520 | 10,413 | 1,490 | 85.69% |
| NEW-45314CB70E99 | 36,014 | 5,802 | 83.89% |
| NEW-6717CCA50787 | 9,859 | 1,055 | 89.30% |
| NEW-C07DA5643BE9 | 41,478 | 5,997 | 85.54% |
| NEW-FDF9C2552AF0 | 2,442 | 429 | 82.43% |

## Gate

- Provider/model calls: **0/0**
- Gold/historical semantic reads: **0**
- Deterministic source-only retrieval: **v8b-source-only-multichannel-topk-v1**
- Deduplicated handle projection: **v8b-deduplicated-handle-projection-v1**
- Decision: **BLOCKED_ON_V8B_SCALABILITY**

If blocked, the next step is a new sparse-retrieval challenger; do not mutate V8A2 or V8A3 and do not call provider.
