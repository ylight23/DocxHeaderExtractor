# V8B0 — heading-boundary reuse audit

Status: **BLOCKED_ON_UPSTREAM_HEADING_PREDICTIONS**.

This audit is source/provenance-only. It did not read Gold, historical predictions/evaluations, or call a provider.

## Answers

1. The 12,140 V8A2 occurrences come from PDF word-coordinate line grouping plus the generic `LooksStructuralPdfLine` parser heuristic.
2. Frozen upstream LLM heading extraction + harness binding confirmations for these six documents: **0 found**.
3. A frozen authoritative bound-heading occurrence set for all six documents: **does not exist**.
4. The 102,915 identity pairs are generated from **generic/source-parser occurrences (B)**, not bound headings.

## Per document

| Document | Raw/source occurrences | Heading candidates before model | Frozen selected headings | Frozen bound headings | Candidate pairs |
|---|---:|---:|---:|---:|---:|
| NEW-073DB817879B | 342 | 342 | 0 | 0 | 2,709 |
| NEW-26BE9B284520 | 1,302 | 1,302 | 0 | 0 | 10,413 |
| NEW-45314CB70E99 | 4,457 | 4,457 | 0 | 0 | 36,014 |
| NEW-6717CCA50787 | 751 | 751 | 0 | 0 | 9,859 |
| NEW-C07DA5643BE9 | 4,985 | 4,985 | 0 | 0 | 41,478 |
| NEW-FDF9C2552AF0 | 303 | 303 | 0 | 0 | 2,442 |

## Boundary conclusion

V8A2 is a generic PDF source-occurrence lane. Its candidates are not restricted to an authoritative upstream heading set.
No V8H bound-heading challenger was materialized because no frozen upstream heading/binding prediction exists for the six sources. Raw paragraphs/lines were not substituted as headings.

- Provider/model calls: **0/0**
- Gold reads: **0**
- Matching heading artifact paths: **0**
- Matching binding artifact paths: **0**
- V8A2/V8A3/V8B mutated: **false**
