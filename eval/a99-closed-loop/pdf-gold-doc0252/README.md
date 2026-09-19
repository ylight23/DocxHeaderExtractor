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
  "sourceAlias": "S0001",
  "page": 1,
  "sourceOrdinal": 0,
  "verbatimText": "...",
  "humanDecision": null,
  "semanticRole": null,
  "parentSourceAlias": null,
  "reviewNote": null
}
```

- `humanDecision` — `HEADING` · `NOT_HEADING` · `NEEDS_REVIEW`. Null means undecided, never "no".
- `semanticRole` — a separate field on purpose. Role is not encoded into the decision, so heading
  membership and role stay separately measurable: a heading found with the wrong role is a role
  error, not a missed heading.
- `parentSourceAlias` — only where the relation was actually adjudicated. Null means *not
  adjudicated*; it does not mean "root".
- `reviewNote` — why, when the answer was not obvious.

## Suggested order

1. **`DUPLICATE_TEXT_INDEX`** — review repeated wording side by side.
2. **`BY_PAGE`** — check reading order and page boundaries; catch headings split across a break,
   running headers, and page artefacts.
3. **`BY_PARSER_CONTEXT`** — use `structuralScope` to organise reading. It is context, not the
   answer: the scope is a parser observation, and treating it as the semantic verdict would make
   the review agree with the harness by construction.
4. **Consistency pass** — every alias carries exactly one decision, and any remaining
   `NEEDS_REVIEW` is deliberate rather than skipped.

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

1. Validate every row against the current catalog (`PdfGoldValidator`): aliases inside the source
   universe, text exact, repeated text disambiguated. A row that does not bind is a Gold defect,
   never a model miss.
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
