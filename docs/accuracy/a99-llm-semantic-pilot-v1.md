# LLM Semantic Pilot V1

The first measurement of the **production LLM semantic pipeline** against occurrence Gold. Two documents, one model, one run.

**Headline:** the LLM does the claim assembly the deterministic engine could not do, and it over-proposes where the deterministic engine has explicit rules. On SRC-089 it scored **F1 0.757** where the deterministic V1.4 scored **0**. On SRC-095 it scored **F1 0.527** where V1.3 scored **0.913**.

Artifacts: `eval/a99-closed-loop/llm-semantic-pilot-v1/` (`preflight.v1.json`, `run.v1.json`, `score.v1.json`, `analysis.v1.json`, plus every raw provider response under each document's `telemetry/`).

## Setup

| | |
|---|---|
| Documents | SRC-089 (Decree 195/2013, translated) and SRC-095 (RFC 9114) only |
| Model | `qwen/qwen3.7-flash` via OpenRouter |
| Contract | V2_ATTENTION_FREE over PDF_SOURCE_FACTS_V3, `STRUCTURED_SOURCE_PARTS` |
| Packing | FIXED_OWNED_COUNT_120 (production default) |
| Binder | the production binder, exact claim identity only |
| Scorer | `GENERIC_EXACT_SCORER_V1`, the same scorer as every held-out |
| Hierarchy | not run |

Everything was frozen and hash-pinned in the preflight before the first request. The Golds were opened only after the run was committed.

**Budget** (authorized: 30 calls, 2.0M input, 250k output; retries counted): 26 calls, 725,449 input tokens, 43,054 output tokens. One call was spent on an attempt that failed at the transport layer and is kept as `attempt-1`. The 25 real requests needed no retries.

## Results

| | SRC-089 (36 Gold) | SRC-095 (103 Gold) |
|---|---|---|
| LLM F1 | **0.757** | **0.527** |
| LLM precision / recall | 0.737 / 0.778 | 0.370 / 0.913 |
| LLM TP / FP / FN | 28 / 10 / 8 | 94 / 160 / 9 |
| Deterministic engine | V1.4: **0** | V1.3: **0.913** |
| Deterministic TP / FP / FN | 0 / 13 / 36 | 89 / 3 / 14 |

Transport and contract held: 25 valid JSON replies out of 25, no claim declined, and 8 of 262 proposals unbindable (7 outside the owned segment, 1 out of source order).

## SRC-089: the assembly problem is solved

The deterministic engine failed this document because the decree bolds only the number of each article and sets the title in regular weight, so `Article 8.` became the whole label and its title became body text.

| Gold pattern | Gold | LLM exact | V1.4 exact |
|---|---:|---:|---:|
| `ARTICLE_HEADING` | 26 | **23** | 0 |
| `S089_Q2_CHAPTER_LABEL_OVER_TITLE` | 5 | **5** | 0 |
| `S089_Q1_DECREE_TITLE_BLOCK` | 1 | 0 (partial) | 0 (partial) |
| `S089_Q3_COLON_CLAUSE_LABEL` | 4 | 0 (unproposed) | 0 (unproposed) |

- **Article headings:** 23 of 26 exact multipart claims, joining the bold number with its regular-weight title across lines that begin in lower case. The 3 misses drop the final line of a three-line title (articles 22, 23 and 25).
- **Chapter headings:** 5 of 5 exact. The label and its centred title line are one claim, which is what the user decided in S089_Q2.
- **Title block:** the model claimed the right three lines but included the footnote mark `(*)`, so it misses by four characters.
- **Clause labels:** both routes miss all four. Nothing in the source sets them apart, and the model did not read them as structure either.

The 10 false positives split into two kinds: **4 are the same occurrences as Gold headings claimed with a different extent** (the 3 wrapped articles and the title block), and 6 are genuinely non-Gold — the signature block (3 lines), the footnote, the national header and one numbered clause.

## SRC-095: recall up, precision down

Recall rose above the deterministic engine's (0.913 against 0.864). Every numbered section matched exactly, including the 6 whose number is set apart in its own row and the 6 front and back matter sections. The index group letters (S095_Q2) stayed unproposed, as under V1.3.

The cost is precision, 0.370 against 0.967. The 160 false positives are dominated by navigation:

| Family | Claims |
|---|---:|
| Contents entries (the review named them non-headings) | 87 |
| Page furniture (running headers, page footers) | 29 |
| Index entries | 26 |
| Numbered captions | 7 |
| Table header or reference rows | 7 |
| Other (2 definition terms, 1 fragment, 1 wrong extent) | 4 |

The deterministic engine has explicit rules for all three of the leading families — the pointer test for contents lines, the furniture test for repeated page bands — and produced 3 false positives in total.

**The model was not confused about what these lines are.** It labelled them correctly in `semanticRole`: `running-header`, `page-footer`, `index-entry`, `footnote`, `signature-label`. It then returned them inside the `headings` array with `isHeading` true anyway. The loss is in what the contract asks of it and how the reply is interpreted, not in whether the model recognises page furniture.

## What this says about the architecture

1. **Semantic reasoning earns its place.** The failure that closed the deterministic study — assembling `Article N.` with its unemphasised title, and a chapter label with its title line — the LLM solves without a rule for it, on a genre it had never seen. That was the open question, and the answer is yes.
2. **It does not replace the deterministic engine's exclusions.** Contents lists, indexes and page furniture are exactly where the deterministic engine is strong and the LLM is weak. The two failure profiles are close to complementary.
3. **The identity decisions remain human.** Neither route reproduced the user's S095_Q1 distinction (`HTTP/3` is the title, `RFC 9114` the identifier) or the exclusion of the decree title's footnote mark. These are meaning decisions, not reading failures.
4. **Review has no counterpart here.** The production contract answers `isHeading` true or false and has no `NEEDS_REVIEW` state, so the deterministic engine's fail-safe — send an ambiguous label to a human — has nothing to map onto. A claim the model is unsure of is simply proposed or absent.

## Limitations

- Two documents, one model, one run, no repeats: no variance measured.
- The Golds were authored by the reviewer who designed the deterministic engine. The LLM route had no part in them.
- Placement was not run, so hierarchy is still `NOT_EVALUABLE`.
- 26 of 30 authorized calls are spent. No cohort run is authorized.

## Open questions for the next step

- Does an explicit exclusion clause (navigation, furniture, captions) recover precision without costing the assembly gain? That is one prompt clause, and the baseline to beat is this pilot.
- Does the deterministic engine's furniture and pointer evidence, given to the model as facts rather than as rules, do the same job?
- Which errors survive repeats, and which are sampling noise?
