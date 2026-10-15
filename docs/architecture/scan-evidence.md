# Scan evidence: source integrity, OCR preflight, numbered sequences

Status 2026-10-15. Provider-free part of the plan agreed with the owner: immutable source, versioned derived
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

## 4. Not done yet, and known limits

- No agent flavor reads the scan evidence yet: the scan source adapter, `get_source_integrity`, `get_text_views`
  (with the view differences inside) and `get_layout_views`, and the binding of a heading to `viewId` + `regionId`
  + box, are the next steps. A heading of a scan is anchored to a page image, a box and an OCR view, never to an
  OOXML paragraph that does not exist; OCR offsets never stand in for native offsets.
- `Mixed` documents run on their native text; their page images are not OCR'd yet.
- `overlapsOtherLine` fires on 885 of 3,485 lines of `014` and 10 of 1,119 of `016`: it depends on the scan. It is a
  measurement, not a flag of error; its threshold is not tuned.
- `headingExtent` carries one extent per SELECT; a paragraph that holds several headings (`S0059` above) cannot be
  expressed fully. A separate contract change.
- The DOCX tool definitions and prompt changed (`DOCX_AGENT_TOOLS_V2`): request hashes differ; live DOCX stays on
  hold until A/B. PDF and `P7F1V6Runner` are unchanged.

## 5. VLM (agreed design, waiting on three decisions)

Roles A (visual typography per line, closed answers), B (arbitration between OCR views, `NONE`/`UNREADABLE`
allowed), C (reading order where geometry is ambiguous), all as `VLM_OBSERVATION`, run in preflight and cached by
image hash, model and prompt version; regions chosen by evidence quality (low confidence, view disagreement,
overlap, sequence skip), never by "looks like a heading". Waiting on: sending page images to a provider (ZDR off),
the VLM model (OpenRouter is not reachable from this environment), and the output policy for a corrected reading.
