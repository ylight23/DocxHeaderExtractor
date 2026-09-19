# DOC-0252 — PDF occurrence Gold review pack

`072_ICP_TAG_Minutes_Mar_2025.pdf` · `sourceSha256 a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61`

DOC-0252 already has an approved semantic Gold, and it is a single number. A count can say a run
found too many or too few. It cannot say *which* heading was missed, so it cannot drive recall,
precision, or the diagnosis of any specific failure. This pack exists so the occurrences behind
that number can be identified by a person.

Everything here is parser output. No model proposed any of it, nothing was pre-filtered, and no
provider call was made.

## Files

| File | View | Shape |
| --- | --- | --- |
| `source-universe.v1.json` | — | all 1,013 occurrences in document order |
| `review-duplicate-text-index.v1.json` | `DUPLICATE_TEXT_INDEX` | 573 text groups, 19 of them repeated |
| `review-by-page.v1.json` | `BY_PAGE` | 15 pages |
| `review-by-parser-context.v1.json` | `BY_PARSER_CONTEXT` | 15 pages × structural scope, with parser evidence |

Each view is an **exhaustive partition** of the same 1,013 occurrences — same aliases, no
additions, no omissions, verbatim text unmodified. None of them is a shortlist: a candidate filter
would decide the question this review exists to answer, and the recall ceiling would quietly become
the filter instead of the source universe.

## What a reviewer fills in

```json
{
  "sourceAlias": "S0573",
  "page": 8,
  "sourceOrdinal": 605,
  "sourceText": "Session V: Current Research 1 The Treatment of Import and Export Prices ...",
  "humanDecision": null,
  "headingClaims": [
    {
      "selectionMode": null,
      "verbatimText": null,
      "occurrence": null,
      "leftExactContext": null,
      "rightExactContext": null,
      "semanticRole": null,
      "parentSourceAlias": null,
      "reviewNote": null
    }
  ]
}
```

- `humanDecision` — `HEADING` · `NOT_HEADING` · `NEEDS_REVIEW`. Null means undecided, never "no".
- `headingClaims` — the headings found **inside** this occurrence.
  - `NOT_HEADING` → empty.
  - `HEADING` → at least one claim; **several are allowed**.
  - `NEEDS_REVIEW` → leave as is; a second pass settles it.
- `selectionMode` — `WHOLE_ALIAS` when the heading is the entire occurrence, `VERBATIM_TEXT` when
  it is part of one.
- `verbatimText` — for `VERBATIM_TEXT`, the exact heading text copied from `sourceText`.
- `occurrence` / `leftExactContext` / `rightExactContext` — only when that text appears more than
  once inside this same occurrence.
- `semanticRole` — a separate field on purpose. Role is not encoded into the decision, so heading
  membership and role stay separately measurable: a heading found with the wrong role is a role
  error, not a missed heading.
- `parentSourceAlias` — only where the relation was actually adjudicated. Null means *not
  adjudicated*; it does not mean "root".
- `reviewNote` — why, when the answer was not obvious.

Each claim becomes exactly one `PdfGoldHeading`. The conversion is mechanical and checks itself:
anything a row does not say is reported back rather than defaulted, because a default there is code
deciding what a person meant. `PdfGoldReview.TryToGoldHeadings` returns nothing at all when the
review is unfinished, so correctness does not depend on remembering to validate first.

`reviewNote` is the one field that does **not** cross over. It is review metadata — why a reviewer
decided as they did — and it stays with the review lineage. Semantic Gold is what the document is
held to contain; growing it to carry a rationale would grow the thing every evaluation compares
against.

### Why claims are a list

A source occurrence is a parser artefact, not a semantic unit. Line grouping fuses neighbouring
lines that share geometry and font, so one occurrence can hold more than one heading. In this
document:

| Alias | Page | Fused text |
| --- | --- | --- |
| `S0043` | 1 | `Session II: Update on the ICP 2021 Cycle` + `1 Global office update` |
| `S0460` | 7 | `Session IV: TAG Functioning and Terms of Reference for Task Forces` + `1 TAG Composition ...` |
| `S0573` | 8 | `Session V: Current Research` + `1 The Treatment of Import and Export Prices ...` |

One answer per occurrence could not say *which* heading, with *which* boundary, in *which* role —
and those are exactly the partial-span cases I8 exists to address. A Gold that cannot express them
cannot measure I8 either.

## Suggested order

1. **`DUPLICATE_TEXT_INDEX`** — review repeated wording side by side.
2. **`BY_PAGE`** — check reading order and page boundaries; catch headings split across a break,
   running headers, and page artefacts.
3. **`BY_PARSER_CONTEXT`** — use `structuralScope` to organise reading. It is context, not the
   answer: the scope is a parser observation, and treating it as the semantic verdict would make
   the review agree with the harness by construction.
4. **Consistency pass** — every alias carries exactly one decision, and any remaining
   `NEEDS_REVIEW` is deliberate rather than skipped.

## Provenance of this review

The pack itself does not show the approved total, but the reviewer was told it in conversation
before starting. This review is therefore **not a blind first pass**, and the freeze must not
describe it as one. It can still be conducted occurrence by occurrence without using the total as a
target — that is the intent — but the exposure is a fact about how the Gold was produced, and
recording it is cheaper than defending the claim later.

## Rules this pack enforces

**Decisions are per occurrence.** `DUPLICATE_TEXT_INDEX` groups identical wording so an
inconsistency is easy to see — not so one answer can cover the group. The same string can be a
table-of-contents entry, a true section heading in the body, a running header artefact and an
ordinary mention in prose. Same text is neither the same occurrence nor the same node. Applying one
answer across several occurrences is a reviewer's explicit act after confirming they really are
treated alike, never a default.

**The approved total is not shown here.** A first pass that knows the answer becomes a search for
that answer: one over, and a borderline row gets dropped to make it fit instead of being recorded
as uncertain. Reconciliation happens after the pass is frozen, and a disagreement is a finding to
re-examine — not an acceptance criterion for the review.

**No judgement is shown.** No likelihood, score, recommendation or predicted role. `domainRole` and
`CandidateAttention` are excluded even though they are genuine parser-owned routing evidence,
because `CandidateAttention` is precisely the heuristic this evaluation exists to test. A reviewer
who sees it is anchored by it, and the Gold stops being independent of the thing it measures.

**The text is the binding text.** Every `verbatimText` is the declared glyph projection
(`pdf-glyph-gap-v1`) the model is shown and the binder binds against — not the readable rendering,
and not punctuation-normalised. Those two disagree exactly where a PDF's reconstruction is hard, and
Gold written against the wrong one fails to bind for reasons that have nothing to do with the model.

## After the review

1. Convert with `PdfGoldReview.TryToGoldHeadings`, which refuses an unfinished review, then
   validate every row against the current catalog (`PdfGoldValidator`): aliases inside the source
   universe, role and selection mode present, text exact, repeated text disambiguated. A row that
   does not bind is a Gold defect, never a model miss.
2. Reconcile the `HEADING` count with the approved total. If they differ, that is a conflict between
   two lineages — the total approved by USER on 2026-09-12, and this occurrence review — and it is
   reported for adjudication. Neither is rewritten.
3. Freeze with `occurrenceAuthority` naming the reviewer, the date and the source-universe hash.
   Only then may `occurrenceEvaluable` become true, and only then is there Gold enough to measure a
   provider run.

## Regenerating

These artifacts are compared, not rewritten, by an ordinary test run:

```
A99_FREEZE_UPDATE=1 dotnet test -c Release --filter FullyQualifiedName~PdfGoldReviewPackTests
```
