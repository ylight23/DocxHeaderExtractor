# PDF072 native production pipeline run with Luna

Run date: 2026-10-25. This fresh Luna-authored run called the public `PdfHandler.ExtractAsync` entry point for the full PDF072 source. The request/reply decisions were frozen before the registered Gold was opened for scoring.

## Pipeline and frozen run

- Source: `todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf`
- Source SHA-256: `a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61`
- Gold authority: `DOC-0252`; Gold SHA-256: `e966d4e57ba6b312a2cebe93446c4350d4ef7173458f7c528306775f9c743b03`
- Execution: `PdfHandler.ExtractAsync -> PdfPipeline -> PdfLane -> LaneHarness -> SelectionAuthority -> StructureAssembler -> ParentResolver`.
- Selection used the production `PdfSelector` with V12, source-bound selection, and raster geometry. Native pairwise GROUP and production hierarchy placement ran through the lane. No `GroupVerifier` or manually invoked selection/grouping/parent-resolution stage was used.
- The 15 production selection scopes covered all 650 parser aliases. The lane returned 42 headings from 44 selected aliases.
- Decisions were frozen in [frozen-semantic-decisions.json](../../artifacts/qualification/pdf072-native-production-luna-20261025/01/frozen-semantic-decisions.json) before scoring. Its SHA-256 is `4ad587ab34c725c53e94730b2a861718c15fc29a4a82b359f50ee08a12867f93`. [run-manifest.json](../../artifacts/qualification/pdf072-native-production-luna-20261025/01/run-manifest.json) records the request and response hashes for all 20 Luna turns.
- One earlier attempt was superseded after a malformed `evidenceRefs` array triggered a production protocol repair, which received an incomplete response. It is preserved in [attempt-1-protocol-repair-error](../../artifacts/qualification/pdf072-native-production-luna-20261025/attempt-1-protocol-repair-error/) and is excluded from this run and its score.

## Gold score

| Measure | TP | FP | FN | Precision | Recall |
|---|---:|---:|---:|---:|---:|
| Heading alias membership | 37 | 7 | 8 | 84.1% | 82.2% |
| Exact heading groups | 35 | 7 | 7 | 83.3% | 83.3% |

Gold contains 45 heading aliases in 42 groups. The native output contains 44 aliases in 42 groups. The eight missing aliases were in the model's source scope and were marked `NOT_SELECT`; they are selection misses rather than coverage gaps. The complete score, including predicted and missing groups, is in [gold-score.json](../../artifacts/qualification/pdf072-native-production-luna-20261025/01/gold-score.json).

## False-positive evidence audit

All seven false positives are agenda entries on pages 13–14 that the model treated as document headings. Each request carried formatting/layout facts and an available OpenCV raster occurrence, so these are interpretation errors rather than missing raster evidence. The complete audit preserves the raw alias and text, page and PDF-point location, raster values, formatter facts, and the model's classification rationale in [fp-evidence-audit.json](../../artifacts/qualification/pdf072-native-production-luna-20261025/01/fp-evidence-audit.json).

| Alias | Raw text | Evidence present | Model rationale / audit finding |
|---|---|---|---|
| `L0522:S1` | Welcome and Opening Remarks [TAG Co-Chairs, World Bank] | Page 13; bold/italic styling; raster occurrence overlaps the time segment | The model called it the first scheduled item. It is an agenda row, not a body heading. |
| `L0524:S1` | Update on the ICP 2021 and 2024 Cycles [World Bank and ICP Regional | Page 13; bold/italic styling; raster occurrence overlaps the time segment | The model selected the scheduled cycle-update row; its presenter text and agenda position identify it as schedule content. |
| `L0527:S1` | Research Topics Emerging from the Previous ICP Cycles [World Bank] | Page 13; bold/italic styling; raster occurrence overlaps the time segment | The model promoted a scheduled research item to a document heading. |
| `L0528:S1` | Forthcoming Research Topics [Prasada Rao] | Page 13; bold/italic styling; raster occurrence overlaps the time segment | The model promoted a scheduled research item to a document heading. |
| `L0529:S1` | Setting Up a Short- and Long-Term Research and Development Agenda [Co- | Page 13; bold/italic styling; raster occurrence overlaps the time segment | The model promoted an agenda row with a presenter label to a document heading. |
| `L0532:S0` | 14:00 – 14:15 TAG Composition and Terms of Reference [TAG Co-Chairs] | Page 13; raster reports two separated horizontal ink runs | The model treated the row as a heading while overlooking the combined time-and-topic schedule layout. |
| `L0546:S1` | Fine Tuning Global Linking Procedures | Page 14; bold styling; second visual segment has a measured 48.5-point gap; raster occurrence is present | The model treated an agenda continuation topic as a document heading. |

## Calls, timing, and placement

The run took 385.0 seconds end to end. This includes the time while the bridge waited for Luna-authored response files, so it is not model-generation latency.

| Phase | Logical calls | Phase wall time | Scheduling |
|---|---:|---:|---|
| Selection | 15 | 191.52 s | Production selector used up to 6 concurrent scope calls. |
| Pairwise GROUP | 2 | 52.30 s | Two chunks overlapped; peak concurrency was 2. |
| Hierarchy placement | 3 | 131.79 s | Three 16-heading batches ran sequentially. No chain-repair request was needed. |

There were 20 logical calls in total. The production transport retry limit was 3; the local bridge returned valid SSE replies on the first attempt, so no transport retries occurred. The production route key was supplied to the lane, while Luna authored every response body locally. The bridge only wrapped request/reply envelopes and recorded hashes/timing; it did not alter phase choice, scheduling, retry behavior, or decision content. No provider-generation, billing, or Qwen-quality claim is made.

The production placer returned 7 root relations, 34 parent relations, and 1 heading outside the hierarchy. Registered Gold does not evaluate hierarchy quality, so these are pipeline observations, not accuracy claims.

