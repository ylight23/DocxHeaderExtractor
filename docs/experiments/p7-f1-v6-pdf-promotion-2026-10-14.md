# P7 F1 V6: promotion to the production PDF lane (2026-10-14)

Issue #9. The V6 direct agent with the method frozen in D4.12 ([p7-f1-v6-d4.12-owner-trials.md](p7-f1-v6-d4.12-owner-trials.md)) replaced the function-conditioned chain (F1 → G2A → H2-C V2) as the heading authority of the PDF lane. DOCX and DOC keep the canonical text pipeline.

The owner decided the promotion on 2026-10-14 without a held-out run. This page records what changed, what was measured, and what has **not** been measured.

## What runs now

```
PDF → production parser (PdfSourceAdapter) → PdfHeadingAgentSelector
        1. selection   one conversation per layout segment (the frozen D4.12 method)
        2. grouping    the model divides runs of adjacent selected lines into headings; C# validates
        3. placement   the model places each heading under a parent; C# validates and counts depth
      → SelectedLinesHeadingAuthority → shared assembler → outline (json, md, txt, xml, csv)
```

Code: `src/DocxHeaderExtractor.PdfHeadingAgent` (the agent), `IHeadingSelector` and `SelectedLinesHeadingAuthority` in `DocxHeaderExtractor.DocumentProcessing`. The production assemblies do not reference the qualification project (the architecture test enforces this); the agent is a port of the qualified V6 code, and `PdfHeadingAgentParityTests` holds the port to the qualified bytes on SRC-089.

### 1. Selection (the frozen method, with the structural-heading instruction below)

Arm TOOLFACTS_ANALYSIS_V3, instruction revised on 2026-10-14 (SHA `813e49c6…`; the frozen `mainrep2`, `2d057c62…`, is kept for parity), layout segments, the first reply must call `get_line_facts`, three transport retries, repair turns with no limit of their own, two prompt-cache marks. First request of every SRC-089 segment equals the SHA pinned in the D4.12 reference plan (`3dfeaf6e…`).

### 2. Grouping (new, not part of D4.12)

D4.12 selected lines and said grouping is a later step. Until this promotion the later step was a C# rule (consecutive lines of one page and one parser layout block), which split a heading whose lines sit in different blocks: "Chapter I" and its title were two headings, and the title then became a child of "Chapter I".

Gold claims with several parts always consist of lines adjacent in reading order. So C# builds *runs* (two or more selected lines with nothing between them) and asks the model how each run divides into headings; a run of one line needs no question. C# accepts only a division of every run into contiguous groups of one to eight lines, in order. A refused answer gets up to two repair turns whose message names every line left out of every run; a run that still cannot be accepted falls back to the layout rule. No selected line is ever dropped.

### 3. Placement (fixed)

The shared placement pass was always refused by the transport on the OpenRouter route (`TRANSPORT_INPUT_PARAMETER_INCOMPATIBLE`: `response_format` is `json_object` but the prompt never said "JSON"), so no PDF ever got a level: every heading was level 1, `unresolved`. This predates V6. The prompt now asks for a JSON object. The PDF lane also sends the style table of the document and each heading's style (size, weight, rank among the document's styles); they are measurements and the model still decides every relation. Without evidence (DOCX) the packet is byte-identical to before.

## Measured (SRC-089 only, a public corpus document)

| Check | Result |
|---|---|
| Placement alone, one request | accepted: 33 of 33 headings placed, 0 outside the tree, 0 unresolved |
| Grouping alone on the 46 lines of the first live run, 5 attempts | 28 of 28 groups equal to the Gold claims in the 4 attempts that were accepted; the first attempt was refused three times (the model left out the third line of two 3-line runs) |
| Grouping alone on the 44 lines of the third live run | with a generic repair message: 1 of 3 attempts refused three times (the same omission), 2 passed. With a message naming one run: 6 of 6 accepted, 3 of them only on the third request. With a message naming every missing line of every run (the shipped one): 8 of 8 accepted, 3 of them after one repair; all 8 equal to Gold, 27 of 27 groups |
| Whole CLI run, live, fourth run | 18/18 segments; 44 lines selected, all in Gold (precision 1.00, recall 0.772); 27 headings, equal to the 27 Gold claims on those lines; "Chapter I GENERAL PROVISIONS" is one heading at level 1 and the Articles are level 2; no heading unresolved; 328 s |
| Earlier whole-CLI live runs | run 1 (before the placement fix): precision 1.00, recall 0.807, every heading level 1 and unresolved. Runs 2 and 3 (placement fixed, before the grouping): precision 1.00, recall 0.719 and 0.772, levels present, but "Chapter I" and its title were two headings |

A grouping request cost about $0.0005 to $0.0011 (one to three requests). The placement request was not costed separately. D4.12 measured about $0.02 for this document; the CLI runs above did not report their cost.

## Not measured

- One document. Nothing here says how the three steps behave on other documents, other genres, or long documents (the grouping asks at most 40 runs per request).
- No held-out run against the layout arm B (0.950 / E-F1 0.838) and no multi-repetition stability: the selection recall moved between 0.719 and 0.807 across runs of the same request.
- The Gold has no hierarchy, so levels are checked by reading, not scored. The tree above is what the model placed.
- Every heading is still marked `RequiresReview`: that is the shared output policy, unchanged.
- The data policy of the trials applies: `zdr` false, data collection allowed, unredacted text sent to OpenRouter and the Alibaba route.

## Instruction revised (adopted 2026-10-14)

Why some Articles were left out: the frozen instruction says "main headings ... that carry its structure (its outline)" without saying what makes a heading main. The model's own reasoning showed it weighing "Chapters only" against "Chapters and Articles" and deciding per segment: where a bold Chapter heading was in view, the Articles in regular type below it were called sub-headings and left out; elsewhere every Article was selected.

The owner revised the instruction (SHA-256 `813e49c6…`; the frozen one, `2d057c62…`, is kept as `InstructionD412` for the parity tests):

```text
Extract the main headings of the document. A main heading is a heading that has meaning in the document's outline: its words name the part of the document it heads and can cover, as a summary, the content that part goes on to say. Decide this by what the heading means for the outline, not by how it looks: being bold, larger or set apart is not what makes a line a main heading, and a heading in the same type as the body text can still be one.
Apply this the same way everywhere in the document: a heading that has this role is selected wherever it appears, whatever other headings or lines are shown next to it.
The result is a list of the document's main headings in which each heading appears once: for each heading, select the lines where the heading itself stands (all lines of a heading that runs over several lines), at the start of the part it names, as its representative. A line that only lists, repeats or points to a heading that stands elsewhere, for example in a table of contents, is not that heading's representative and is not selected.
When you are unsure about how a line relates to other lines, use the tools to check that relationship.
Do not guess: if the evidence does not settle a line, say that it is unresolved instead of choosing.
```

Test through the production agent, two runs per document, scored like the D4.12 freeze (decisions of complete units, all-heading Gold):

| Document | D4.12 frozen runs (P / R) | Revised, run 1 | Revised, run 2 |
|---|---|---|---|
| SRC-089 (whole) | 1.00 / 0.754, 0.789, 0.772 | 1.00 / 0.807 | 1.00 / 0.825 |
| SRC-029 (first 12 segments) | 0.815 / 0.647; 1.00 / 0.312 | 1.00 / 0.667 | 1.00 / 0.438 |
| SRC-053 (first 12 segments) | 0.952 / 0.690 | 0.903 / 0.966 | 0.867 / 0.897 |

Recall rose on all three documents; precision stayed 1.00 on SRC-089 and SRC-029 and fell on SRC-053 (extra lines: "Balance Sheet (Section IV)", "Income Statement (Section IV)", "Non-GAAP Measures", and once the organisation name on the cover). On SRC-089 run 1 brought back Articles 8 and 13, but each run still missed something segment-dependent (run 1: Articles 1 and 2 and the line "Chapter I"; run 2: Article 13 and the line "Chapter IV"). SRC-029 still varies a lot between runs (0.667 against 0.438); many of its misses are cover-page lines and table-of-contents entries, which the instruction excludes. About $0.017 to $0.022 per run. Two runs per document: the direction is consistent, the size is not established.

## Tried and not adopted: a consistency pass after the selection

Why Articles are sometimes left out: the selection decides one segment at a time, and the instruction says "main headings" without saying which level counts. In the fourth live run the model called Articles sub-headings in some segments (Articles 1, 2, 8 and 13 were left out, with the model's own explanation "a sub-heading at a lower structural level" or "Articles are excluded") and main headings in others.

Experiment: a second model pass over the whole document, shown the outline of everything selected and, part by part, the lines with their style and whether they were selected, asked to add or remove lines so that the instruction is applied the same way throughout. Run on four different selections of SRC-089 (and once more on the fourth), about $0.006 to $0.008 per document:

| Selection | Selected lines before | After | Added | Removed (all Gold lines) |
|---|---|---|---|---|
| run 1 | 46 | 44 | 0 | 2 |
| run 2 | 41 | 36 | 0 | 5 |
| run 3 | 44 | 39 | 0 | 5 |
| run 4 (second attempt) | 44 | 41 | 2 | 5 |
| run 4 (first attempt) | 44 | 28 | 0 | 16 |

It never brought back a missing Article; it removed more correct lines every time, each time with the reading "an Article is a sub-section, not a main heading like a Chapter". Two or three of its four parts per document were also refused by the validator (`CHANGE_INVALID`, probably the 300-character limit on the reason; not checked). It was not wired into the pipeline and its code was removed.

Open: the instruction has to say which levels are "main", or the selection should take every level and leave "main" to the level assigned by the placement pass. Neither is measured.

## What is retired

`FunctionAnchorExtentHeadingAuthority` and its byte-parity and requalification tests are removed. On 2026-10-14 the owner cut the repository to the V6 experiment (commit e6a7d65): every other test, the non-V6 qualification code and the P7 experiment scripts are gone, and so are the two production classes nothing used any more (`PdfInferenceWireContract`, `PdfReadOnlyCorrespondenceBuilder`). The F1/G2A/H2-C protocol classes (`OccurrenceFunctionProtocolV1`, `HeadingAnchorProtocolV1`, `HeadingExtentProtocolV2`) stay because the kept V6 qualification code still refers to them.

## Instruction changed again: the structural-heading instruction (adopted 2026-10-14)

The owner replaced "main headings" with a functional definition: every genuine structural heading at every level, decided by what the occurrence does at its own location (it names, introduces or divides a unit of content), not by level, length, numbering or the ability to summarize; a standalone label and the title after it may form one heading over several lines (`InstructionStructuralV1`, SHA-256 `c5c5b693...`). Production sends it since this change; the pattern step stays off.

Measured on SRC-089 (whole), SRC-029 and SRC-053 (first 12 segments), 2 runs each, frozen in `artifacts/p7-v6-pattern-commitments/` (precision / recall of the selected lines against the all-heading Gold, after the pattern step where it ran):

| condition | SRC-089 | SRC-029 | SRC-053 |
|---|---|---|---|
| revised instruction (813e49c6) + pattern V2 | 1.00 / .84 | 1.00 / .31-.56 | .85-.96 / .79-.90 |
| structural instruction + pattern V3 | .93-.98 / .91 | .35-.84 / .73-.77 | .78-.81 / 1.00 |
| structural instruction + pattern V4 | .82-.98 / .91-.95 | .77-.79 / .75-.94 | .81-.91 / 1.00 |
| structural instruction + pattern V4 + consensus audit | .88-1.00 / .88-.91 | .97 / .75 | .78-.81 / 1.00 |

The structural instruction finds more headings on every document (SRC-053: 29/29 in every run) and selects more numbered body clauses, so precision drops. The pattern step (a document-wide decision per numbered opening) made 68 changes across the structural runs and none was right against Gold; under V3 it turned 66 table-of-contents lines of SRC-029 into headings. It was never on the production route and its code was removed from the agent project afterwards (it is in git history: commits bb4aa7a, ba08a03 and the ones before them; the runs stay frozen in `artifacts/p7-v6-pattern-commitments/`). The earlier "main headings by meaning" instruction (SHA-256 813e49c6) was removed from the code too; its text is in the same history. Once the model finds a heading it almost always selects all of its lines; the remaining alias errors are in the grouping step (SRC-029).

Provider failures now name an account without credit apart: HTTP 402 is `PAYMENT_REQUIRED`, not `CLIENT_ERROR`, and is not retried.

## Reproduce

`dhx <document.pdf> --openrouter -f md` with `OPENROUTER_API_KEY` set. `DHX_PDF_AGENT_PARALLELISM` (default 6) sets how many segments run at once; the provider rate-limited above about 6 concurrent requests.
