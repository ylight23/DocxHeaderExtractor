# RFC-1 RFC TOC First-Reject Diagnosis

## Status

RFC-1 is **CLOSED**. The exact C1 failure identity was joined and reproduced through the direct production API:

```text
RfcTocDictionaryOutlineTests.Dung_toc_dictionary_giu_so_muc_va_khop_nav_092
-> RfcTocDictionaryOutline.Analyze(SlimDocument)
```

The analyzer is normally production-reachable through `HeaderExtractionPipeline.cs:1272`. No PDF-first fallback or legacy header pipeline call path is involved.

## First Loss

The first loss is `TOC_CANDIDATE_GENERATION`, specifically `FindTocCluster`. The real 092 input contains 1,555 Slim paragraphs, but only 147 survive the analyzer's `!Corrupt`, `TableDepth == 0`, and non-empty-text filter. The eligible input has no dense early compact TOC marker cluster:

```text
TocParagraphs = 0
TocThreshold = 0
DictionaryEntries = 0
BodyAnchors = 0
Accepted = false
Reason = không có cụm TOC dày, sớm và gọn
```

Therefore `candidateGenerated = false`, `firstRejectingPredicate = NOT_APPLICABLE`, and dictionary/body-anchor/navigation operations were not executed. This is not a navigation mismatch inferred from the final assertion.

## Differential Control

RFC 091, 093, 094, and 095 also fail the same direct analyzer, so none is a valid passing negative control. The diagnostic test uses a controlled synthetic fixture through the same production analyzer. It has one dense paragraph with 20 numbered entries and 20 matching body anchors; it passes with 20 dictionary entries and a body-anchor ratio of 1.0. The first causal divergence is `FindTocCluster`.

## Invariants and Decision

`SECTION_NUMBER_PRESERVED` and `NAV_TARGET_MATCHED` are `NOT_OBSERVABLE` for 092 because no TOC candidate or dictionary is created. A passing control proves the gate contrast, but does not manufacture a 092 section/nav result.

Primary classification: `TOC_CANDIDATE_GENERATION`.

The evidence justifies opening RFC-2 for a narrowly scoped source/cluster handling investigation. RFC-1 itself makes no production change, does not change expected output, and makes no provider call.

Machine-readable evidence is in `eval/verification/rfc-toc-first-reject.v1.json`.
