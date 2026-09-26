# Generic Heading Audit: Final Verdict

**Verdict: `NOT_GENERALIZED`.** The generic heading audit engine is accurate on the financial statements it was developed on. It does not carry over to document genres it has not seen, and its hierarchy output was never measurable.

Test-generated artifacts back every number below: `eval/a99-closed-loop/final-verdict-v1/` (`cross-document-summary.v1.json`, `hierarchy-evaluation.v1.json`, `final-verdict.v1.json`, from `GenericAuditFinalVerdictTests`). They read the committed held-out scores and classifications and recompute nothing. The study made no model or provider calls.

## How it was measured

Each held-out document went through the same eight steps:

1. The engine version and the source-fact version are frozen.
2. The document is pre-registered.
3. The engine runs blind, and its proposals are committed unread.
4. The Gold is written from the original PDF alone. Any pattern without precedent goes to the user.
5. The Gold is frozen and every reveal input is pinned.
6. The proposals are scored once. The raw score is committed exactly as it came out.
7. Every residual gets one cause, in one bucket:
   - **A:** a meaning decision;
   - **B:** a generic engine gap, either known (B_KNOWN) or new (B_NEW);
   - **C:** an ontology gap;
   - **D:** a document-specific rule.
8. The next engine version is then developed on that document. Its held-out score is never recomputed; Gold corrections found later are diagnostics only.

Scorer: `GENERIC_EXACT_SCORER_V1`. A proposal counts only if its source parts match the Gold claim exactly.

## The held-out chain

| Held-out | Genre | Engine | Source facts | Gold | TP | FP | FN | F1 |
|---|---|---|---|---:|---:|---:|---:|---:|
| SRC-029 | procurement | V1 | V1 | 374 | 151 | 479 | 223 | 0.301 |
| SRC-041 | financial statements | V1.1 | V1 | 280 | 264 | 15 | 16 | 0.945 |
| SRC-042 | financial statements | V1.2 | V1 | 253 | 247 | 5 | 6 | 0.978 |
| SRC-044 | financial statements | V1.2 | V1 | 201 | 196 | 2 | 5 | 0.982 |
| SRC-053 | information statement | V1.2 | V1 | 271 | 0 | 0 | 271 | 0 * |
| SRC-054 | information statement | V1.2 | V2 | 296 | 243 | 69 | 53 | 0.799 |
| SRC-095 | technical standard (RFC) | V1.3 | V3 | 103 | 89 | 3 | 14 | 0.913 |
| SRC-089 | legal (decree, translated) | V1.4 | V3 | 36 | 0 | 13 | 36 | 0 |

\* The measurement was invalid. Under PDF_SOURCE_FACTS_V1 every atom read as size 1 with no bold. The raw 0 is kept, and V2 and V3 fixed the fact layer.

Micro totals mix five engine versions. They summarize the study, not any one version:
- **All eight held-outs:** precision 0.670, recall 0.656, F1 0.663.
- **Without SRC-053:** precision 0.670, recall 0.771, F1 0.717.

Residual buckets across the chain:

| Bucket | Residuals |
|---|---:|
| A | 97 |
| B_KNOWN | 80 |
| B_NEW | 1029 |
| C | 0 |
| D | 1 (SRC-041, V1.1) |
| Gold revision candidates | 3 (SRC-054) |

## Findings

1. **The engine is accurate within the family it was built on.** On financial statements whose headings are set apart by type, held-out F1 is 0.945 to 0.982.
2. **It does not generalize to new genres.** Each document of a new kind exposed generic gaps that development on the earlier documents could not have revealed:
   - SRC-054 sets its tables and sub-levels at body size.
   - SRC-095 sets section numbers apart from their titles, and has an index and an address block.
   - SRC-089 bolds only the number of each article and sets the title in regular weight.

   Each fix cycle raised the development scores, and the next held-out then found a new convention.
3. **The ontology held.** Since V1.1, no residual needed a new semantic category (C = 0). One document-specific residual (D) appeared, under V1.1, and none since.
4. **The source-fact layer can invalidate a measurement.** SRC-053 shows this. V3 reads SRC-089 correctly, so its F1 of 0 is a real engine failure, not a measurement failure.
5. **Failures are mostly about extent, not invention.** On every valid held-out since V1.2 except SRC-054 (0.78), precision stayed at or above 0.96. SRC-089 had no exact matches, but 32 of its 36 Gold headings were found partially: a number without its title, or a heading's lines split apart.
6. **Hierarchy is `NOT_EVALUABLE`.** The engine emits no level and no parent. Only one of the 23 Gold authorities (DOC-0202) declares a hierarchy, and it holds a count, not occurrences. Level, parent and full-path accuracy all have a denominator of 0.

## The last cycle (V1.4 → SRC-089)

V1.4 fixed only the five B_NEW families from SRC-095:
- **Split section numbers:** a label is judged whole, including in the region and furniture tests.
- **Cross-reference lists:** three families (index locators, index entries, cross-references) are handled by one lexical shape.
- **Contact blocks:** an author's name under an address heading is metadata.

On development documents, SRC-095 went from 0.913 to 0.960 and no other document regressed. It was the last development cycle, as the user decided.

Before SRC-089's Gold was frozen, the user decided the three patterns without precedent:
- **S089_Q1:** the decree's title is one three-line claim.
- **S089_Q2:** each chapter's label and title line are one claim.
- **S089_Q3:** the four colon-ended clause labels are headings.

SRC-089 scored F1 0, and classification traced it to real generalization gaps, not to the source facts:
- **Article headings (B_NEW):** the bold is only on the number, so V1.4 reads "Article 1." as a run-in lead and the title as body. This accounts for 26 misses.
- **The user's claim-extent decisions (A):** the chapter label and title as one claim, and the three-line decree title. The engine proposed the other reading each time.
- **The clause labels (A):** colon-ended labels are a meaning decision by design, but here the fail-safe did not hold: the engine never proposed them.
- **Two false positives (B_NEW):** a document-status notice read as the title, and the signatory's name read as a label.

Under the user's rule, a gate that fails on real gaps goes to the verdict with the limitation stated. V1.4 was not tuned on SRC-089.

## Limitations

- **Coverage:** eight held-outs, five of them financial. Each engine version met only one or two held-outs.
- **Independence:** the person who wrote the source-only Golds also designed the engine, so the Golds are not double-blind. Every Gold decision without precedent was the user's.
- **What was measured:** occurrence membership and semantic axes only. There is no hierarchy measurement and no visual binding.

## Not claimed

- 99% accuracy.
- Generalization across genres.
- Hierarchy accuracy.
- Readiness for production beyond a review-assisted audit of financial documents whose headings are set apart by type.

## What would be needed for more

- **Legal genre:** fixing the article-number lead would be development on SRC-089. It needs a new legal held-out.
- **Hierarchy:** three things are needed:
  - per-occurrence hierarchy Gold (level and parent), approved like membership;
  - an engine that outputs level and parent, frozen before a held-out;
  - a level, parent and path scorer.
