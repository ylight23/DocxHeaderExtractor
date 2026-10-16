# Scan evidence: source integrity, OCR preflight, numbered sequences

Status 2026-10-16. The plan agreed with the owner: immutable source, versioned derived
views, read-only evidence tools; the model decides. Nothing here corrects a reading or names a heading.

## Production integration (2026-10-16)

`dhx extract <scan.docx> --openrouter -f json` and the Web upload now detect `ScannedImageOnly`
before the native DOCX adapter. The production composition root supplies `IScanHeadingSelector`;
`ScanExtractionPipeline` runs its OCR/VLM preparation and selection under the shared lane deadline,
then uses the existing selected-lines authority, binding, placement, hierarchy and materialization.
Native/Mixed DOCX and PDF keep their own selectors. The shared placement packet carries measured
scan layout and typography; the model decides parent relations, without a heading-keyword rule.

The source catalog contains every OCR line in its declared reading (accepted blind transcription,
else OCR, or the selected alternate reading). A heading extent addresses that exact reading.
`SourceAnchor.Scan` and the outline's `scanSources` keep the page, original-image pixel box, reading
view, original OCR, other readings and transcription status/text. Scan pixels are not PDF points
or editable OOXML paragraphs. Sections and chunks use the same catalog the binder used.

`routeAudit.scan` separates preflight and conversation calls/costs and records shared placement
attempts; placement billing is not exposed by the shared transport and is not included in those
two costs. Unit failures, missing OCR views and failed preflight calls make the result partial;
CLI returns exit code 1 while retaining the usable JSON. Web reports `Partial` and its diagnostics.
`scan-extract` remains the standalone diagnostic format and also returns 1 for incomplete results.

Cancellation reaches page workers, the exact Tesseract/ImageMagick child process, transcription
and conversation HTTP calls, and shared placement. A cancelled/deadline-expired run cannot publish
a completed result. `--no-llm` builds a local OCR catalog without transcription/selection calls.
Scan writeback to Word paragraphs is explicitly unsupported: Web skips it, and the writeback API
refuses it. This integration changes routing/output, not the selector's heading policy or its
measured accuracy; the 011/022 references still need human review.

### Additional text observations (`SCAN_AGENT_SOURCE_V4`, `SCAN_LINE_FACTS_V4`)

Each line's `surface` facts are measured from its declared reading, identified by `readingViewId`:

- `leadingToken`: the literal leading digits/letters and punctuation, with exact UTF-16 offsets;
  `numberedPrefix`: the existing language-neutral shape key, form and numeric value, when readable;
- `endingPunctuation`: the punctuation at the end of the line, including closing quotes. Long form
  leaders are bounded to four displayed characters and disclose their full count;
- `previousLine`: its alias, page, reading and terminal punctuation; `nextLine`: its alias, page,
  reading, literal leading token when present, and its first Unicode letter and case;
- `nearestPrecedingNamedNumber`: the nearest earlier line whose shape key has words before a number,
  with that key/value/form and the number of intervening lines. Its words may be arbitrary, an OCR
  error or an inline cross-reference. It is **not** an enclosing article or a parent claim.

No `isHeading`, `isBody`, sentence-completion or article-membership judgement is added. In particular,
a comma and a following lower-case letter are exposed as two observations, not labelled a continuing
sentence. No keyword list decides heading membership. Both the scan system prompt and user instruction
remain byte-for-byte unchanged. Facts are in the first request and in `get_line_facts`; referenced
aliases become citable observations without counting as reading those neighbours' core facts.
OCR and VLM transcription cache keys do not change. Larger facts can make the existing size-based
partitioner create more conversation units; no new provider call or automatic decision is introduced
by the measurements themselves. `eval/scan-text/022_surface_facts.v1.json` shows four real 022 lines,
measured offline from cached primary OCR only; it is a diagnostic example, not a gold reference.

```text
source (DOCX) ──► DocxSourceIntegrity ──► NativeText / Mixed ──► native DOCX pipeline
                                     └──► ScannedImageOnly ──► OCR/VLM source and selection
                                                              └──► shared binding, placement, hierarchy
                                                                   └──► structure, outline, sections, chunks
```

## 1. Source integrity (`DOCX_SOURCE_INTEGRITY_V1`)

`DocxSourceIntegrity.Read` measures the package: OOXML text characters, paragraphs with text, body images, and
how many of them cover a page (extent at least 60% of the page's width and height). Kinds:

| Kind | Rule | Corpus (74 files of `todo10_8/generated-docx`) |
|---|---|---|
| `NativeText` | no page-covering image | 57 |
| `ScannedImageOnly` | page images, fewer than 200 OOXML characters per page image | 6 (`002 011 014 016 022 023`: 0 to 4 characters per page image, a signature block) |
| `Mixed` | page images and text | 11 (`041 042 043 044 046 047 048 049 050 053 056`: covers and charts; 60,000+ characters per page image) |

`DocxAgentSource.Load` refuses `ScannedImageOnly` with `SCANNED_IMAGE_ONLY`; before, four of these files were read
as their one signature paragraph and two as `EMPTY_UNIVERSE`. All 74 files are real OOXML packages (ZIP); none is a
binary `.doc` under a `.docx` name.

## 2. OCR preflight and evidence store (`SCAN_OCR_EVIDENCE_V1`)

`ScanOcrPreflight` runs before any agent conversation, once per source and parameter set:

- page images in body order, kept as their original bytes with SHA-256;
- two views: `ocr-original` (the image as it is) and `ocr-upscaled` (grey, 1.5x, ImageMagick); each records the
  engine (`tesseract 5.3.4`), languages (`vie+eng`), page segmentation mode, the raw TSV of every page and its
  hash; boxes of every view are in the pixels of the original image;
- per line: text exactly as the engine wrote it (`textNfc` only when NFC changes it), box, mean and lowest word
  confidence, words with boxes and confidences, and `overlapsOtherLine` (another line shares at least 30% of its
  height and its horizontal range: the measurement that flags a stamp or speck mixed into a line);
- views that could not be made are recorded with the reason (`IMAGEMAGICK_NOT_FOUND`, `NO_PAGE_IMAGES`).

The store entry is `<root>/<sourceSha[..16]>/<parametersSha[..12]>/` (evidence.json, pages/, views/<id>/*.tsv),
written to a temporary directory and moved into place; an existing entry is read back, never rewritten. `dhx scan-ocr` and
`dhx scan-extract` share the root (`.dhx-evidence/ocr` by default, `<-o>/ocr` otherwise), so OCR done ahead by
`scan-ocr` is not done again by `scan-extract`.

`OcrViewComparer` pairs the lines of two views by box overlap (IoU at least 0.5) and lists, after NFC, every
stretch where their words differ, as `UNVERIFIED`. It never chooses.

Why two views: on page 3 of `023`, upscaling fixes `a)` → `đ)` but breaks `quyền` → `quyên`, `bản` → `ban`,
`số` → `so`; grey and normalised alone changes nothing; no view fixes `Tham` → `Thẩm`. Each view is right where
the other is wrong, so both are kept and compared.

Measured (`dhx scan-ocr`, 4 workers):

| File | Pages | Seconds | Words | Mean conf. | Lines read differently by the two views |
|---|---:|---:|---:|---:|---:|
| `002` Bộ luật Lao động | 83 | 232 | 41,937 | 95.6 | 1,398 of 3,241 |
| `011` Luật ATTT mạng | 28 | 56 | 13,913 | 95.3 | 446 of 1,031 |
| `014` Luật Chứng khoán | 93 | 308 | 46,380 | 95.3 | 1,402 of 3,485 |
| `016` NĐ 13/2023 | 37 | 77 | 14,246 | 94.6 | 533 of 1,119 |
| `022` NĐ 01/2021 | 85 | 247 | 43,297 | 95.9 | 1,293 of 3,189 |
| `023` NĐ 23/2015 | 34 | 66 | 12,720 | 94.2 | 460 of 1,084 |

A `Mixed` control (`046`, one cover page) took 4 seconds; the preflight reads only page-covering images.

The OCR text layer of a PDF made from these images is not used: its font is `GlyphLessFont`, sizes are line-box
heights, bold is always 0, and PdfPig mixes a stamp line into the heading below it. Typography from scans needs
its own measurement (or a VLM observation, see section 5).

## 3. Numbered sequences (`get_shape_sequence`, `SHAPE_SEQUENCE_V2`, `DOCX_AGENT_TOOLS_V2`)

A line's sequence key is the words before its first number (at most two, symbols such as `-` or `•` skipped), `#`,
and the punctuation right after the number: `Điều 4.` and `Điều 17.` share `Điều #.`. The tool returns the target's
key and number, how many lines share the key, its place, its neighbours in the sequence, and where the numbers skip,
repeat or go down. For every skip it lists the lines between the two neighbours that carry exactly a missing number
under another key. Numbers are compared exactly and keys not at all: nothing is matched approximately.

Native corpus (68 readable files): 213 sequences of 20 lines or more, 119 with a skip, mostly `#` and `#.` (clause
numbers that restart in every article, numbers in tables): legitimate structure, which is why the tool reports and
never concludes. `Điều #.` / `Article #.` sequences are mostly complete. In Bộ luật Dân sự (native DOCX) the article
sequence skips 8 → 10: `Điều 9. Thực hiện quyền dân sự` stands in the middle of paragraph `S0046`, after clause 9 of
article 8, and `S0059` holds `Chương III`, `Mục 1.` and `Điều 16.` in one paragraph.

On the OCR evidence (view `ocr-original`):

| File | Articles under `Điều #.` | Both views together | Missing in both | Of those, found as a missing number under another key |
|---|---:|---:|---|---|
| `011` | 54 / 54 | 54 / 54 | none | |
| `016` | 44 / 44 | 44 / 44 | none | |
| `014` | 130 / 135 | 134 / 135 | `24` | 3 of the original view's 4 skips have such a line |
| `002` | 216 / 220 | 216 / 220 | `9 40 42 183` | all four: `Điều 9,` `Diéu 40.` `Điền 42.` `Diéu 183.` |
| `022` | 98 / 101 | 99 / 101 | `18 19` | both skips of the original view (13 → 15, 17 → 20) have such a line |
| `023` | 48 / 49 | 49 / 49 | none | the skip 6 → 8 of the original view has such a line |

So, for every article that neither view reads under the article key, the original view has a line that carries
its exact number between the right neighbours in all cases measured but one (`014` article 24). That is where the
model should look; whether the line is the article is still the model's call.

## 4. Vision observations (`SCAN_VLM_OBSERVATION_V1`, prompt `SCAN_VLM_PROMPT_V1`)

Owner's decisions (2026-10-16): page images may be sent to the provider; the vision model is the agent's own
`qwen/qwen3.7-flash`; the agent decides what a heading is, with OCR, vision and the program supplying facts.

`ScanVisionPreflight` makes one request per page, through the agent's audited transport (Alibaba only, pinned model,
usage and cost audit, 128 KB request cap):

- the page in black and white at 1000 px wide, with every OCR line of the page (id, box scaled to that image, first
  words): closed answers per line, `appearsBold` (yes/no/uncertain), `relativeSize` (larger/same/smaller/uncertain),
  `alignment` (left/center/right/justified/uncertain);
- for the weakest lines (the views read them differently, a word below 60, another line overlaps them; lowest
  confidence first, at most 10, and only while the request stays under the cap) a full-resolution crop and every
  view's reading: the model answers one viewId, `NONE` or `UNREADABLE`, and with `NONE` may propose a reading.

Black-and-white PNG keeps letters, weight and diacritics (the crop of `Điều 4. Thẩm quyền` reads `Thẩm`, which
Tesseract misread as `Tham`) at a fraction of the size: a page is about 28 KB against 82 KB for grey JPEG at 700 px.
Measured request sizes: `023` median 78 KB, max 96 KB; `002` median 110 KB, max 130 KB (under the cap, close to it).
Ten crops per page is the cap almost everywhere: about 40% of lines are read differently by the two views.

The reply is validated: line ids must be the ones asked, answers from the closed sets, a choice one of the views shown,
a proposed reading only with `NONE`; everything else is rejected and listed in `rejected`. A valid observation is
stored once per image, model, prompt version and set of asked lines (`<root>/vlm/`), and read back; a failed call is
returned with its code (`CALL:TRANSPORT:CLIENT_ERROR` when the provider refuses an image) and not stored, and the
route goes on without it.

Not verified: whether `qwen/qwen3.7-flash` accepts image input. `openrouter.ai` is not reachable from the environment
this was built in, so every test runs against a fake provider. The first live run will tell; a refusal is recorded
per page and the route continues on OCR alone.

## 5. The scan route (`SCAN_AGENT_SOURCE_V2`, `SCAN_LINE_FACTS_V2`, `SCAN_AGENT_TOOLS_V1`, `P7_F1_V6_SELECT_OBJECTS_SCAN_V1`)

`ScanHeadingAgentSelector` (CLI `dhx scan-extract`, key from `OPENROUTER_API_KEY`): OCR preflight, vision preflight,
transcription preflight, then the V6 conversation over `ScanAgentSource`, with the same runner, budget, repairs and
core-facts gate as DOCX.

- Transcription preflight (`SCAN_VLM_TRANSCRIPTION_V1`, prompt `SCAN_VLM_TRANSCRIBE_V1`; owner's decision after §5a):
  every line of the primary view is cropped from its page image (8 px margin), the other lines' boxes that the margin
  reaches painted white (the line's own box kept whole), enlarged 2x in black and white (pixels repeated), and
  transcribed by the vision model, which sees only the crops, never an OCR reading. Batches of at most 10 crops under the byte cap, stored
  in `<root>/vlm-transcribe/` by what the crops are made of, read back on the next run. A transcription is a reading
  (`vlm-transcribed`, origin `VLM_TRANSCRIBED`) only when it passes two checks against the primary OCR reading of the
  same box and of the lines whose boxes overlap it (what the mask cannot paint out), compared as
  letters and digits without diacritics, so that dots a form's leaders lost in OCR do not count: no line break
  (`REJECTED_MULTILINE`), not closer to a neighbour, or to a neighbour joined to the line, than to the line itself
  (`REJECTED_NEIGHBOUR_TEXT`: the line above caught by the margin, or a line of specks OCR made over a heading, whose
  crop shows the heading again), and not 10 letters and 2 times longer (`REJECTED_LONGER_THAN_OCR`). A rejected one is
  kept as evidence. A reply that is not the expected object or not text is asked once more. In the conversation, a call
  whose reply ended with the model's reasoning using every output token (`AUDIT:FINISH_LENGTH,REASONING_EXHAUSTED`,
  one unit lost in each of the third and fourth live runs) or that timed out (`TRANSPORT:TIMEOUT_OR_CANCELLED`, one
  unit in the fifth) is sent once more, the same bytes: scan route only (owner's decision), PDF and DOCX unchanged. With the transcription,
  the vision preflight asks no reading choices, and choices are not shown.
- A target is one line of the primary view (`ocr-original`), alias `P003-L012`. Its text is the accepted transcription,
  else that view's reading; the facts say which (`text.shownReading`, `text.transcription`, and the OCR views that read
  the same), and `get_text_views` lists every reading, the transcription and a rejected one with its reason.
- Facts per line, each with its origin: layout measured on the page image (box, page size, left edge against the page's
  common edge, margins and centring, gaps above and below, height against the median line), OCR (mean and lowest
  confidence, low-confidence words, overlap, views covering it and whether they differ), typography only as a
  `VLM_OBSERVATION` or explicitly "not measured", the vision reading choice, a repetition summary. About 700 bytes a
  line, so units are cut by size to 39-40 lines; core facts of a unit at most 28.8 KB in two calls on all six scans.
- Tools: `get_line_facts`, `get_source_context`, `search_document_text` (every view, says which view holds the text),
  `get_text_views` (every reading, the differences as `UNVERIFIED`, the vision choice and proposal), `get_shape_sequence`,
  `get_source_integrity`.
- Answer: a SELECT may carry `readingViewId`, one of the readings listed for its line (an OCR view, or `vlm-proposed`);
  `headingExtent` is measured on that reading. A reading not listed is refused (`READING_VIEW_UNKNOWN`) and goes back to
  the agent; the agent never writes a reading of its own.
- Result (`ScanHeadingResult`): each heading's parts carry page, OCR line id, box, the view taken and its origin, the
  text, the extent, the primary reading kept beside it, the readings that agree, and a verification: with the
  transcription, `CORROBORATED` when an OCR view reads the same (two independent readers), else `UNVERIFIED`; with an
  OCR view, `CORROBORATED` when the vision model chose it, `CONFLICT` when it chose another reading, `UNVERIFIED`
  otherwise. When the agent names no reading, the part takes the text shown. `TextStatus` is `VLM_TRANSCRIBED`,
  `OCR_PRIMARY_VIEW`, `OCR_OTHER_VIEW` or `CORRECTED_UNVERIFIED`. The result also counts the transcribed and accepted
  lines and the preflight calls and cost (vision and transcription; `ProviderCalls` and `ProviderCostUsd` are the
  conversation's).
  `CORROBORATED` means two readers agree, not that the text is right: on `023` the line `Điều 4. Tham quyền và trách
  nhiệm cap bản sao từ sô gốc` is `CORROBORATED` (the vision model chose the same OCR view).
- Lines of one heading are joined by the shared grouping step (`PDF_HEADING_GROUPS_V1`): the agent divides each run of
  adjacent selected lines into headings. A run it divided correctly is kept even when another run of the same answer is
  refused; a repair turn asks only for the runs still open; a run still open after the repairs falls back to one heading
  per line, and the failure names it (`GROUP-001:PROTOCOL:RUN_MISSING:R7`).

## 5a. Reading accuracy (`dhx scan-text-eval`, `SCAN_TEXT_EVAL_V1`, prompt `SCAN_VLM_TRANSCRIBE_V1`)

Gold: `eval/scan-text/023_heading_lines.v1.json` (`SCAN_LINE_TEXT_GOLD_V1`), the 120 lines of the headings of the second live
run of `023`, each with page and box. Transcribed by Claude from the page images, line by line; not reviewed by a person,
so not human gold under the a99 gold policy. Lines are compared after NFC and white space only; a line is exact or not, and
the character error rate (CER) is edit distance over gold letters.

Measured before the vision transcription, from `ket-qua-023-v2.json` and the local OCR store (Tesseract 5.3.4):

| Reader | Exact lines | CER |
|---|---:|---:|
| `ocr-original` (Windows run, Tesseract 5.4.0) | 73/120 (61%) | 2.33% |
| `ocr-original` (Linux, 5.3.4) | 75/120 (62%) | 2.33% |
| `ocr-upscaled` (Linux, 5.3.4) | 73/120 (61%) | 3.13% |
| better of the two views, chosen by the gold (no reader can do this) | 85/120 (71%) | 1.63% |
| result text (agent's choice among views and vision proposals) | 77/120 (64%) | 1.95% |

Measured with the vision transcription (Windows, commit `004bbdae`, report in `.dhx-evidence/eval/`): 28 POST, 0.0033 USD.

| Reader | Exact lines | CER |
|---|---:|---:|
| `ocr-original` | 73/120 (61%) | 2.33% |
| `ocr-upscaled` | 74/120 (62%) | 3.04% |
| better of the two OCR views (oracle) | 84/120 (70%) | 1.70% |
| vision model, blind, crop as sent by the vision preflight | 118/120 (98%) | 0.27% |
| vision model, blind, crop enlarged 2x | 120/120 (100%) | 0.00% |
| vision model, blind, crop enlarged 2x in black and white (6.6x fewer bytes) | 118/120 (98%) | 0.27% |

The two black-and-white misses read every letter right and added the line above (`Mục 3`/`Mục 4`), whose bottom the
8 px margin caught. `scan-text-eval` now also measures crops masked: the other lines' boxes that reach into the crop are
painted white (except where they overlap the line's own box) before the crop is cut (`-2x-masked`, `-2x-bilevel-masked`).

The two misses at 1x read every letter right but added the line above (`Mục 3⏎CHỨNG THỰC CHỮ KÝ`): the crop's margin
caught part of it. Hence the transcription preflight's checks and the 2x scale. The eval also scores what the route
shows (`shown-by-scan-route`: the 2x transcription where it passes the checks, else `ocr-original`). One document, its
heading lines only, against a gold no person has reviewed: not yet a measurement of body text or of other scans.

Before the vision transcription, choosing among the OCR views could not pass 71% on these lines: the readings that are wrong are wrong in both views
(`sổ` → `số`, `Thẩm` → `Tham`, `ĐIỀU` → `DIEU`, `III` → `II`). Of the 11 heading lines where the vision model's choice
can be checked, 4 were right; the 7 wrong ones were all an OCR reading it accepted. `dhx scan-text-eval` measures what
the vision model reads when it is shown only the crop: the crop the vision preflight sends, and the same crop enlarged
2x (the scans are black and white already, so a grey crop would be the same image). Each batch is stored once
(`<root>/vlm-transcribe/`); the report is written to `<root>/eval/`.

## 5c. Measured type and in-memory crops (`SCAN_TYPOGRAPHY_V1`, `SCAN_AGENT_SOURCE_V3`, `SCAN_LINE_FACTS_V3`)

- `PngRaster` decodes a page image in .NET (PNG without interlacing, any colour type, 1 to 16 bits; anything else is
  first turned into an 8-bit grey PNG by ImageMagick). The transcription's black-and-white 2x masked crops are cut from
  it in memory: pixel for pixel the image tool's crop (tested on 023's pages, at the page's edges and with masks), so
  the stored transcriptions stay valid; 1,084 crops in about 14 s on one core against about 0.3 s each before.
- `ScanTypography` measures each OCR line on the image: the mean length of its horizontal runs of dark pixels (the
  width of its vertical strokes), against the page's median line, per unit of height, and its share of ink. On 023,
  against the gold heading lines: bold lines at 1.29 times the page's median stroke or more (10th percentile, median
  1.35), regular heading lines and 98% of the body at 1.06 or less; the body lines above 1.15 were bold on the image
  too (title block, motto, signature). The facts give it as `typography` with origin `MEASURED_ON_IMAGE`.
- So the vision preflight's page-by-page typography questions (one call a page, 90 to 371 s a document) are off by
  default; `dhx scan-extract --vision` asks them too, shown beside the measurement (`typography.vision`). Line height
  and alignment were measured already (layout facts).

## 5d. Facts in the request (scan route only)

The V6 method asks the model to read the targets' facts through `get_line_facts` before it answers: one round trip
of every unit. On `011` the conversation took about as long as its slowest unit (`SEG-000`: 6 calls one after
another, 250 s of 309 s; median call 20.7 s). On the scan route (owner's decision), the first request now carries
`targetFacts`: the targets' facts, the same objects `get_line_facts` returns, entered into the coverage ledger as read.
The model may answer on its first turn; `get_line_facts` still gives the facts of any other line. PDF and DOCX keep
the forced facts call (`IAgentFlavor.PayloadFacts` is null for them). The selector id says `facts-in-request`.

## 5e. Which headings (`dhx scan-heading-eval`, `SCAN_HEADING_GOLD_V1`, `SCAN_HEADING_EVAL_V1`)

`dhx scan-heading-eval <ket-qua.json> --gold <gold.json> [-o report.json]` scores the headings a scan run selected
against a gold of the document's headings; it changes nothing. A heading with a label (Chương, Mục, Điều, Phần) is
matched by the label's kind and number within a page; one without (the title block, a titled clause) by its letters
(similarity 0.8), or by the start of them when only the first line of a longer title was selected (found, its text not
exact). A title selected apart from its label is a split part, not a false positive. Recall counts the required
headings found; precision counts the selections that are a gold heading or a part of one; "acceptable" headings count
for neither side when missed and are not false positives when selected.

The golds (`eval/scan-text/011_headings.v1.json`, `022_headings.v1.json`) follow the boundary of the user-approved a99
legal gold: 022 is DOC-0202 itself (same source; `canonical-semantic-gold-vnext/semantic/DOC-0202.semantic-freeze.v1.json`,
111 headings = the decree title, Chương I–IX, Điều 1–101; khoản, điểm, Nơi nhận, signatures and page furniture are
not headings), and 011 is read the same way (the law's title, every Chương, Mục and Điều: 69). DOC-0202 freezes the
count only; the lines, pages and texts in these files were read by Claude from the page images and are not reviewed by
a person. For 022 every Chương and Điều part of the v3 run was checked against the images (one fix: `Chương VIII,` is
`Chương VIII`), and the texts were taken from v3 after that check, so "text exact" favours v3 only where v3 was right.

| run | recall | precision | false positives | text exact |
|---|---|---|---|---|
| 011 v2 (`SCAN_AGENT_SOURCE_V3`) | 68/69 (title missed) | 68/70 (97.1%) | 2 (khoản 4., 5.) | 67/68 |
| 022 old (`SCAN_AGENT_SOURCE_V2`, vision on) | 111/111 | 111/150 (74.0%) | 39 | 106/111 |
| 022 (`SCAN_AGENT_SOURCE_V3`) | 110/111 (Điều 19 missed) | 110/139 (79.1%) | 29 (four bare 3.–6.) | 105/110 |
| 022 v3 (facts in request) | 111/111 | 111/130 (85.4%) | 19 (18 khoản, Nơi nhận) | 110/111 |

Every false positive on 022 is a khoản or "Nơi nhận:". The instruction asks for headings "at all structural levels,
including … lower-level subdivisions" and also not to select "numbered body paragraphs"; a khoản reads as both, and
the runs disagree on which (55 different lines across the three).

Reports: `eval/scan-text/reports/heading-eval-*.json`.

## 5b. Speed

- OCR: every page of both views is one queue of engine runs (one thread each), the upscaled pages first since they take
  about twice as long, with as many runs at once as the machine has processors (`Options.Parallelism`, not part of the
  evidence's parameters, so stored evidence stays valid).
- Vision observation and transcription run side by side once the OCR evidence exists (both read only it and the page
  images, both wait on the provider): vision, transcription and the conversation each 32 calls at once
  (`dhx scan-extract --jobs N` sets all three, for a provider that limits the rate).
- Transcription reasons with a budget of 1,024 tokens (measured on 023: 120/120 like reasoning on, about 14 s a call
  against about 50 s). Fresh run of `016` before these two changes (37 pages, Windows): OCR 80 s, transcription 240 s,
  vision 371 s (37 calls at 8 at once, about 75 s a call), preflight 451 s, conversation 496 s; 15 min 47 s in all,
  0.123 USD; no unit failure.
- Measured on 023 (v6, Windows, OCR read back): vision 22 s; transcription 481 s for about 76 calls at 8 at once,
  about 50 s a call with the model's reasoning on, against about 7 s with it off (eval: 12 calls, 20 s); the
  conversation 545 s. Reasoning off read 118/120 (`sổ` → `sô`, `tổ` → `tô`), so it stays on; the eval now also measures
  a reasoning budget of 1,024 tokens (`-reasoning-1k`).
- Fresh run of `011` at these settings (28 pages, Windows, no rate limit): OCR 70 s, vision 90 s, transcription 229 s
  (side by side with vision), preflight 299 s, conversation 295 s; 9 min 56 s in all, 0.102 USD. The transcription
  was slower than its calls measured alone (about 104 calls at 32 at once is four rounds, about 57 s a round, against
  about 14 s a call in the eval at 4 at once). To tell where the time goes, the result now gives the crop seconds
  (`TranscriptionCropSeconds`; about 0.3 s a crop measured on Linux, each run decoding the whole page), each stage's
  call durations (`CallSeconds`: count, median, 90th percentile, slowest, sum) and the slowest conversation units.
- A batch whose reply stays unreadable after the second ask is split in two, down to single lines, so that one crop
  that makes the model answer with no text (`CONTENT_NOT_TEXT`) leaves only itself unread.
- The result gives the seconds of each stage (`StageSeconds`: ocr, vision, transcription, preflight, conversation); the
  CLI prints them.
- `scan-text-eval` also measures transcription with the model's reasoning off (`-no-reasoning`) and gives each
  method's seconds, calls and cost, to decide whether the route can send it.
- A second run on the same document reads every preflight stage back from the store.

## 6. Windows

The scan tools are the same on Windows and Linux. Requirements: .NET 9, Tesseract with the Vietnamese data (the
UB Mannheim installer; tick Vietnamese, or the preflight stops with `TESSERACT_LANGUAGE_MISSING:vie`), ImageMagick on
`PATH` (without it there is no upscaled view and no vision step, and the evidence says why), `OPENROUTER_API_KEY`.

- Tools are found on `PATH` (`tesseract.exe`, `magick.exe`; `C:\Program Files\Tesseract-OCR\` too). `convert` is not
  looked for under Windows names: `convert.exe` there is the system's file-system tool.
- No path reaches an external tool. Images go to Tesseract and ImageMagick on standard input and come back on standard
  output; .NET reads and writes the files. Tesseract on Windows cannot open a path outside the system code page (a user
  folder such as `C:\Users\Tuấn`); .NET can. Checked on Linux with a store under `Thư mục của Tuấn/kho evidence` and a
  source named `Nghị định 23 – bản scan.docx`: the raw TSV of all 34 pages of both views is identical, byte for byte,
  to the run that passed paths.
- Arguments are passed one by one, not through a shell, so `%` in `-threshold 55%` is not touched by `cmd`.
- Files are UTF-8 without a byte order mark; TSV lines may end in `\r\n`.

First Windows run (Tesseract 5.4.0, ImageMagick 7.1.2-33): `dhx scan-ocr` on `023`, all 34 pages in both views, no
gap, about 135 s; `dhx scan-extract` then ran end to end. The tests that need Tesseract or ImageMagick skip themselves
where the tools are missing, so a green suite on a machine without them checks nothing of this part.

## 7. Not done yet, and known limits

- Third live run (`ket-qua-023-v5.json`, commit `d265e2ee`, grey 2x crops): all 1,084 lines transcribed, 1,059
  accepted (19 refused as a neighbour's text, 6 with a line break). Against the gold: 106/106 heading lines shown from
  the transcription exact; 113 of 120 gold lines selected; one wrong text (`bỗ sung`, an OCR reading the agent named).
  `SEG-027` failed with `TIMEOUT_OR_CANCELLED`. Masked crops measured the same day: black and white 120/120, grey
  119/120; the route now sends black-and-white masked crops (6.6x fewer bytes), and the neighbour check compares only
  lines whose boxes overlap, since the mask paints out the rest (it had refused form lines like `Bên A`/`Bên B`).
- Second live run (`ket-qua-023-v4.json`, commit `37417097`): no budget refusal; 1,019 of 1,069 transcriptions passed
  the first checks. Against the gold: 83/83 heading lines shown from the transcription exact; the 8 wrong ones were
  shown from OCR, chosen by the agent where the vision model's choice among OCR readings had confirmed a wrong one
  (`DIEU KHOẢN THI HANH`), or untranscribed (two `PROTOCOL_VERSION_INVALID` batches and one `CONTENT_NOT_TEXT`). Of the
  47 refused as longer, 40 were right transcriptions of form lines whose leaders OCR had shortened, and the rest the
  heading read again from a line of specks: hence the letter-based length, the neighbour check, the second ask, and
  no reading choices. `SEG-018` failed with `REASONING_EXHAUSTED` (`SEG-001` on the run before).
- First live run with the transcription (`ket-qua-023-v3.json`, commit `876634af`): grey 2x crops of body lines fit
  about five to a request, so 218 batches were cut where the ceiling was planned for 144 from the line count; 65 were
  refused (`BUDGET:RESERVE_OVER_CEILING`) and 332 of 1,084 lines went untranscribed. Fixed: the budget is made from the
  batches actually cut, and a retriable transport failure is sent once more. Of the 752 transcribed, 715 passed the
  checks (34 longer than the OCR, 3 with a line break; the result now lists each refused one with its OCR reading).
  Against the gold: every heading line shown from the transcription was exact (72/72); the 19 wrong heading texts were
  all lines shown from OCR (untranscribed or refused). 111 of the 120 gold lines were selected; `SEG-001` failed
  (`AUDIT:FINISH_LENGTH,REASONING_EXHAUSTED`: the model's reasoning used every output token), which lost `Điều 3`.
  Preflight 153 POST, 0.0167 USD; conversation 132 POST, 0.0790 USD.
- One live run so far, `023` on Windows (`ket-qua-023.json`, built from `0288e073`): the model accepts the page images
  (34 pages observed, no vision failure); 29/29 units complete, 113 POSTs, 0.0718 USD for the conversation, the vision
  calls not counted in that total. 117 selected lines; all 49 `Điều` and the five `Chương` found (`Chương III` read as
  `Chương II` by OCR); `Mục 1` not selected; some numbered clauses (`2. Ủy ban nhân dân xã…`), the motto and `Chủ
  thích:` selected. The grouping request (26 runs) was refused three times for a missing run, so every heading was one
  line; fixed since (runs answered correctly are kept). No gold yet, so these are observations, not scores.
- Hierarchy (levels, parents) is not part of the scan result; writing a scan's outline back into a DOCX needs a new
  document, since the source has no paragraphs to mark.
- `Mixed` documents run on their native text; their page images are not OCR'd in that route.
- `overlapsOtherLine` fires on 885 of 3,485 lines of `014` and 10 of 1,119 of `016`: it depends on the scan. It is a
  measurement, not a flag of error; its threshold is not tuned.
- `headingExtent` carries one extent per SELECT; a paragraph that holds several headings (`S0059` above) cannot be
  expressed fully. A separate contract change.
- The DOCX tool definitions and prompt changed (`DOCX_AGENT_TOOLS_V2`): request hashes differ; live DOCX stays on
  hold until A/B. PDF and `P7F1V6Runner` are unchanged.
