# A99 DOC-0202 Span Representation Reconciliation

Base revision: `04a7e9b4be60f69327c85bb1af747af5538fb059`

Execution revision: `c43ad1f` (`accuracy99: reconcile DOC-0202 image-only representation`)

## Decision

DOC-0202 is user-finalized semantic Strict Gold at 111 headings:

- 1 document title
- 9 chapters
- 101 articles

The DOCX package contains 85 page-image media entries and 85 drawing paragraphs, but only 4 `w:t` nodes, all signing metadata. The PDF contains 85 pages with one image per page and no extracted text or letters. The review packet contains 170 source occurrences: 1 metadata-text occurrence and 169 empty occurrences.

No recoverable character text for the 111 page contents was found. The packet builder did not discard recoverable heading text. OCR, model inference, provider calls, and image/bounding-box identity were not used.

Therefore:

```text
SPAN_REPAIR_RESULT = IMAGE_ONLY_NOT_CHARACTER_SPAN_REPRESENTABLE
REPRESENTATION_CAUSE = SOURCE_CONTAINS_PAGE_IMAGES_WITHOUT_CHARACTER_TEXT
CURRENT_B3_CONTRACT_COMPATIBLE = false
```

The semantic adjudication is user-finalized Strict Gold with truthful `HUMAN_WITH_MODEL_ASSISTANCE` provenance. It remains semantic-only because the source is not character-span representable; it is not converted into a fabricated span Gold packet.

## Cohort Action

DOC-0202 remains outside the frozen 15-document active cohort because its source is image-only. It is semantically evaluable but not character-span evaluable; no source spans are fabricated. The active cohort replacement for the later assisted `DOC-0123` exclusion is `DOC-0001`, selected from DEV metadata only.

The active queue remains source-only human review of `DOC-0205` first. Holdout remains sealed and provider calls remain zero.

## Verification

```text
Focused A99 tests = 26/26 PASS
Release build = PASS
Full suite = 1122 total, 1121 passed, 1 failed, 0 skipped
Known failure = N15
New failures = 0
Baseline = NOT_RUN
A99 claim = A99_NOT_MEASURED
```

The full-suite TRX remains untracked under `tests/DocxHeaderExtractor.Tests/TestResults/`.
