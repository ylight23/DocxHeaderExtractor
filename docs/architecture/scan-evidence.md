# Scan evidence: source integrity, OCR preflight, numbered sequences

Status 2026-10-16. The plan agreed with the owner: immutable source, versioned derived
views, read-only evidence tools; the model decides. Nothing here corrects a reading or names a heading.

```text
source (DOCX) ──► DocxSourceIntegrity ──► NativeText / Mixed ──► DocxAgentSource (as before)
                                     └──► ScannedImageOnly ──► refused as SCANNED_IMAGE_ONLY
                                                              └──► ScanOcrPreflight (offline, `dhx scan-ocr`)
                                                                   └──► evidence store (immutable, two views)
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
written to a temporary directory and moved into place; an existing entry is read back, never rewritten.

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

## 5. The scan route (`SCAN_AGENT_SOURCE_V1`, `SCAN_LINE_FACTS_V1`, `SCAN_AGENT_TOOLS_V1`, `P7_F1_V6_SELECT_OBJECTS_SCAN_V1`)

`ScanHeadingAgentSelector` (CLI `dhx scan-extract`, key from `OPENROUTER_API_KEY`): OCR preflight, vision preflight,
then the V6 conversation over `ScanAgentSource`, with the same runner, budget, repairs and core-facts gate as DOCX.

- A target is one line of the primary view (`ocr-original`), alias `P003-L012`; its text is that view's reading.
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
  text, the extent, the primary reading kept beside it, the readings that agree, and a verification:
  `CORROBORATED` (an OCR view and the vision model read the same), `CONFLICT` (the vision model chose another reading),
  `UNVERIFIED` otherwise. `TextStatus` is `OCR_PRIMARY_VIEW`, `OCR_OTHER_VIEW` or `CORRECTED_UNVERIFIED`.

## 6. Not done yet, and known limits

- No live run yet (provider not reachable here); quality of headings, cost per document and whether the model accepts
  images are unmeasured. Gold for the six scans does not exist yet.
- Hierarchy (levels, parents) is not part of the scan result; writing a scan's outline back into a DOCX needs a new
  document, since the source has no paragraphs to mark.
- `Mixed` documents run on their native text; their page images are not OCR'd in that route.
- `overlapsOtherLine` fires on 885 of 3,485 lines of `014` and 10 of 1,119 of `016`: it depends on the scan. It is a
  measurement, not a flag of error; its threshold is not tuned.
- `headingExtent` carries one extent per SELECT; a paragraph that holds several headings (`S0059` above) cannot be
  expressed fully. A separate contract change.
- The DOCX tool definitions and prompt changed (`DOCX_AGENT_TOOLS_V2`): request hashes differ; live DOCX stays on
  hold until A/B. PDF and `P7F1V6Runner` are unchanged.
