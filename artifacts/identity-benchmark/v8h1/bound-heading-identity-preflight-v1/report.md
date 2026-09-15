# V8H1 — Bound-Heading Identity Preflight

Status: **READY_FOR_IDENTITY_MECHANICS**.

Generalization status: **BLOCKED_ON_MINIMUM_HOLDOUT_DOCUMENT_COUNT**. The preregistered minimum is 6 documents; the frozen bound-heading authority currently contains 4 eligible documents and 36 headings.

No provider calls, Gold reads, identity labels, historical predictions, or evaluation artifacts were used. The superseded 38-heading snapshot and raw V8A2 source occurrences were not used.

## Frozen mechanics

- Bound headings: **36**
- Full within-document pairs: **212**
- Proposer requests: **4**
- Independent falsifier requests: **4**
- Sparse pruning: **false**
- Falsifier receives proposer output/rationale: **false**

## Per document

| Document | Bound headings | Full pairs |
|---|---:|---:|
| NEW-26BE9B284520 | 7 | 21 |
| NEW-6717CCA50787 | 19 | 171 |
| NEW-C07DA5643BE9 | 5 | 10 |
| NEW-FDF9C2552AF0 | 5 | 10 |

## Upstream unavailable

The following documents remain `UPSTREAM_HEADING_UNAVAILABLE` and are not converted into semantic predictions: **NEW-073DB817879B, NEW-45314CB70E99**.

The next allowed phase is provider authorization for V8H1 mechanics only. A first generalization evaluation remains blocked until at least two additional eligible source documents are added and their bound-heading authority is frozen.
