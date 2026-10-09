# P7-D2 source pool and cohort-freeze gates

This offline command inventories **all** PDFs in the existing repository corpus
`todo10_8/heading_corpus_100`, plus the reference PDF. It pins binary identities
without reading Gold, predictions, or raw model captures. Output is a new file;
source text and private absolute paths are not published. Duplicate content is
reported, not silently counted as independent qualification documents.

```powershell
dotnet run --project scripts/P7CohortInventory -c Release -- <repo> <reference.pdf> <new-pool.json>
```

**A source pool is not a frozen cohort.** Category/file names cannot certify a
table, borderless table, two-column document, or a real heading in a table cell.
Coverage rows stay OPEN until source-only review identifies concrete cases. Prior
exposure must also be screened; do not call this pool held-out by default.

## Comparison design

- Control: existing production F1/G2A/H2-C bytes; no evidence/semantic patch.
- A: raw parser evidence, no references or interpretation requirement.
- B: byte-equivalent raw parser evidence and common stage decision rules, plus
  references, copied assertions, and short interpretation records.
- C: retrieval, deferred and not implemented.

`PdfEvidenceOnlyProtocol` implements A's qualification wrapper and delegates the
decision to the same stage parsers as B. The A/B source projection, stage input,
subjects, source hashes, visibility and semantic prompt prefix are tested equal.
Response contracts differ explicitly. The control remains unchanged.

Both A and B add an identity wrapper and change output instructions relative to
production Control. Thus Control→A is **not geometry-only causal isolation**.
A→B estimates the joint effect of reference/assertion/interpretation requirements,
not any one component in isolation; response volume and cost also differ.

## Evidence assessment

Physical evidence and interpretation are separate dimensions:

| Status | Physical assessment |
| --- | --- |
| `VERIFIED_FACTS` | Known in-scope references and copied values match the trusted source store |
| `UNVERIFIABLE_ASSERTION` | A known field lacks measurements; claims cannot be verified |
| `INVALID_REFERENCE` | Unknown/misbound field/source, wrong snapshot, or falsified copied coordinates/value |

Free-form interpretation is always `UNVERIFIABLE_ASSERTION` by this deterministic
verifier, even when physical facts are verified. Semantic decisions remain
`NOT_EVALUATED`. Malformed protocol is a separate contract failure, not automatically
evidence hallucination. Missing evidence still fails the strict factual contract;
the diagnostic status does not silently admit it.

Models may describe relationships freely **within the issued evidence scope**.
This is not global evidence exploration or retrieval. G2A retains the control's
neighbor scope; H2-C retains the control's tail. Broader visibility would be another
treatment. No predefined relationship enum is required. A regression explicitly
preserves H2-C membership despite a separate F1 OTHER judgment; disagreement is
not an implicit pruning rule.

## Freeze checklist

1. Select source-only shape-diverse documents from the pinned pool, record exact
   reviewed source cases, deduplicate documents, and identify prior exposure.
2. Freeze per-source parser snapshot/universe/store and production P05 packs;
   demonstrate deterministic reconstruction. Pool byte hashes alone do not pass this.
3. Freeze heading-membership and title/subtitle inclusion/grouping policy **before
   scoring predictions**. Mark ambiguous/unadjudicated units NOT_EVALUABLE rather
   than deriving a correct answer from a treatment output. Gold stays immutable.
4. Keep F1/G2A/H2-C separate. For isolated stage comparisons, use the same frozen
   control upstream cohorts in all arms; do not regenerate from treatment outputs.
5. Freeze Control/A/B prompts, schema versions, input/body hashes and constraints.
   Preserve existing frozen controls byte-for-byte.
6. Derive and approve an exact call budget. G2A primaries depend on F1 outputs and
   H2-C anchors on G2A outputs: without frozen upstream authority, later-stage
   request counts cannot honestly be called fully frozen. Freeze those stage gates
   after raw upstream capture, before Gold/scoring, or provide existing authority.
7. Score membership/FP/FN, exact boundary/under/over, overlap observations, factual
   reference failures, independently reviewed unsupported interpretations, and cost
   separately. False anchors and unresolved title boundaries get separate lanes.
8. Raw captures and hashes freeze before Gold scoring. No retries/repair/fallback,
   runtime change, source/Gold mutation, or implicit production authority.

No provider authorization or promotion is created by this inventory or checklist.
