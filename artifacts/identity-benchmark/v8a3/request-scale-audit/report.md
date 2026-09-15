# V8A3 — exact request scale audit

Status: **BLOCKED_ON_PRE_PROVIDER_SCALABILITY**.

This audit consumes only the frozen V8A2 request corpus at `579b3fd`. It does not mutate V8A2, read Gold/predictions, or call a provider.

- Requests: **12** (6 proposer + 6 falsifier)
- Candidate pairs: **102,915**; occurrences: **12,140**
- Total serialized request bytes: **55,218,832**
- Estimated input tokens: **13,804,712** (`ceil(UTF-8 bytes / 4)`)
- Maximum request: **11,061,000 bytes / 2,765,250 estimated tokens**
- Configured context: **1,000,000** tokens; configured max output: **16,000**
- Provider/model calls: **0/0**; Gold reads: **0**

## Per document

| Document | Occurrences | Candidates | Proposer bytes/tokens | Falsifier bytes/tokens | Repeated text bytes | Cross-stream occurrence payload |
|---|---:|---:|---:|---:|---:|---:|
| NEW-073DB817879B | 342 | 2,709 | 729,687/182,422 | 729,664/182,416 | 205 | 113,192 |
| NEW-26BE9B284520 | 1,302 | 10,413 | 2,821,459/705,365 | 2,821,436/705,359 | 752 | 456,606 |
| NEW-45314CB70E99 | 4,457 | 36,014 | 9,827,208/2,456,802 | 9,827,185/2,456,797 | 12,361 | 1,642,480 |
| NEW-6717CCA50787 | 751 | 9,859 | 2,518,441/629,611 | 2,518,418/629,605 | 8,371 | 254,165 |
| NEW-C07DA5643BE9 | 4,985 | 41,478 | 11,061,000/2,765,250 | 11,060,977/2,765,245 | 7,992 | 1,624,742 |
| NEW-FDF9C2552AF0 | 303 | 2,442 | 651,690/162,923 | 651,667/162,917 | 343 | 95,543 |

## Gate

- All requests below 90% input context threshold: **False**
- All requests fit with configured max output: **False**
- Total estimated input within 5,000,000-token budget: **False**
- Decision: **BLOCKED_ON_PRE_PROVIDER_SCALABILITY**

Request bodies and candidate semantics were not changed. If blocked, the next experiment is a new V8B source-only sparse-retrieval preflight; V8A2 remains immutable.
