# Required PDF layout evidence — 2026-10-25

The user asked why selection was not directed to use evidence tools before classifying a target. V8 makes that workflow explicit and testable. It is an opt-in candidate; the production PDF default remains V6.

## Change

`PdfMembershipInstructionV8` retains the general structural criterion from V7 and requires `get_page_layout` to show every target alias before the model returns a final answer. The instruction asks the model to read the returned text and layout with neighboring lines, and to continue with `nextOffset` when a page result does not include a target.

The candidate PDF format replaces the old optional-tools wording with a mandatory page-layout instruction. `SelectionTask` checks the successful tool-result ledger and refuses a final answer while any target alias is absent from successful `get_page_layout` results. Its repair message names the missing aliases. This makes tool use a runtime condition rather than relying only on the model following the prompt.

V8 is selectable with `PdfMembershipInstructionVersion = 8`. The default remains 6. DOCX and scan route prompts are unchanged. No parser or Gold change is included.

The resolved prompt text used by the live trial is saved as `artifacts/qualification/pdf-tool-evidence-v8-20261025/v8-candidate-system-prompt.txt` and `v8-candidate-selection-instruction.txt`; the exact strings are also present in every captured first request. Their UTF-8 SHA-256 values are `50022f210c72028c2ca7901f30ea573582e625fbc1fe3336fe367be7837e609a` and `0d897eae5fdab50e3c716da09d4b16ca164592b822753ddffab706de363530d7`.

## Offline verification

Three focused tests passed. They verify that (1) the runtime rejects a direct answer that skipped the tool and accepts it only after all target aliases appear in page-layout results, (2) V8 keeps the same scopes as V7, and (3) V6 remains the default while V8 has a distinct opt-in selector identity. The qualification trial project builds with zero warnings and zero errors.

The initial live harness attempt used its old two-POST-per-scope cap and ended three PDF072 scopes as `REVIEW_POST_LIMIT` after multiple layout pages were needed. Those artifacts are retained under `live/initial/` and excluded from scoring. The bounded candidate trial was rerun at four POSTs per scope, matching the configured maximum of three tool rounds plus an answer; this changes only the qualification harness cap, not production.

## Controlled initial-selection results

The same five frozen PDF072 scopes and the same PDF076 scope were run twice each. Every completed scope had successful page-layout evidence for all target aliases. V8 still failed the declared accuracy/stability gate against corrected Gold and the frozen V6 decisions:

| Document | Repeat | Seconds | V6 TP/FP/FN | V8 TP/FP/FN | All targets had page evidence |
|---|---:|---:|---:|---:|---|
| PDF072 | 1 | 247.289 | 25/2/0 | 23/16/2 | Yes |
| PDF072 | 2 | 102.361 | 25/2/0 | 25/3/0 | Yes |
| PDF076 | 1 | 70.520 | 2/0/8 | 1/0/9 | Yes |
| PDF076 | 2 | 89.138 | 2/0/8 | 10/0/0 | Yes |

The candidate used 7 successful `get_page_layout` calls per PDF072 repetition and 2 per PDF076 repetition. PDF072 varied from missing two Gold headings and adding 16 extra aliases in the first repeat to no omissions and 3 extras in the second. PDF076 varied from 9 omissions to an exact 10/10 selection. The prompts provided mandatory source evidence, but the evidence did not make the model's classifications stable.

## Raw-response diagnosis

The first-request SHA-256 is identical across repetitions for the inspected units: PDF072 SEG-000 `fd3ec8d70a49`, PDF072 SEG-013 `38c4ddc66155`, and PDF076 SEG-012 `3c12e72a626f`. The successful page-layout line arrays also match exactly by page and offset in both repetitions. Thus the inspected disagreements did not come from different target packets or different parser/tool evidence.

- **PDF072 SEG-000:** On repeat 1, the visible model analysis says L0000–L0003 are “title/metadata elements” that precede the body and do not divide sections, so it rejects L0000 and L0001, which the corrected Gold treats together as the document heading. On repeat 2, it calls L0000–L0003 a “document title block” and selects all four, incorrectly including the date and location metadata. The same layout result shows all four lines centered and bold at 11 pt, with L0000 and L0001 supplying the title and group name. The model changed its semantic boundary around the block.
- **PDF072 SEG-013:** On repeat 1, it says presentation titles “name the content unit” and selects 14 Gold-negative agenda-item aliases. On repeat 2, it says talk titles are agenda items inside the larger units and excludes them. This is an inconsistent reading of the broad phrase “names, introduces, or divides a unit of document content.”
- **PDF076 SEG-012:** On repeat 1, the model calls organization names “list entries” and selects only L0421, the parent heading. On repeat 2, it calls the same nine organizations “sub-headings” introducing delegate groups and selects all nine along with L0421. The page-layout data is identical. For example, the parent L0421 is bold while L0422 and L0427 are regular font, even though the organizations begin separate delegate groups; the model did not apply one stable rule to that structure.

There were also protocol errors, separate from these membership errors. PDF072 repeat 1 first returned a mismatched `sourceText` for L0540; the runtime rejected and repaired the response, but the final selection still contained the Gold-negative agenda items. PDF076 repeat 2 initially cited evidence IDs such as `E1.01`; the runtime rejected those references and asked for source aliases, and the corrected final response retained the same heading decisions. These repairs validate output shape and source binding; they cannot decide whether an organization name or agenda item is structurally a heading.

The trace therefore points to **LLM semantic classification and repeatability**, not missing tool calls or different source data. The tools return measurements and page text, not heading labels. V8's gate proved the model read the source but cannot make the interpretation accurate; it remains unqualified.

The follow-up source-preflight implementation and bounded name review are documented in `pdf-source-preflight-v9-20261025.md`. They complete source reads before the first classification request, but the live accuracy gate still fails. V6 remains the production default.

The PDF072 initial-selection times were below five minutes, but the first was 4m 7s. These are five-scope selection trials only, not full-document extraction times; they do not qualify the candidate for a five-minute end-to-end target. No full extraction was run.

**Gate: fail.** V8 remains opt-in research code. Do not promote it or use it to replace the PDF V6 default. The score and complete tool traces are in `artifacts/qualification/pdf-tool-evidence-v8-20261025/live/initial-postcap4/score.json` and its sibling request/reply files. The source/build identity and preserved two-POST harness attempt are in the same artifact root.
