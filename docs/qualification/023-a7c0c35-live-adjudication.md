# 023 live audit adjudication: a7c0c35

Review date: 2026-10-19. This note joins the safe audit from execution
`0cd9b814-759e-45fd-955c-6adcc098aef7` with visual review of selected pages from
the source PDF. It records local source findings only; it does not establish
user-finalized Strict Gold. No production behavior or Gold registry was changed.

Source: [023_ND_23-2015_Chung_thuc.pdf](../../todo10_8/heading_corpus_100/01_phap_quy/023_ND_23-2015_Chung_thuc.pdf),
SHA256 `762b29b5c61411349d08858bba9eb3be3f417f00dc02a4c4179ca8400ec006a8`.
The safe pipeline JSON is retained locally at
`%TEMP%\dhx-a7c0c35-023-live-retry.ndjson.pipeline.json`, SHA256
`7dc7c53703e8e5a8d5e49ca1466f043f6dec1f4c8e8bd345c54ee8a32609c771`.
The reviewed page rasters match the OCR cache hashes:

| Page | Raster SHA256 |
|---|---|
| 8 | `377525861198aaba01515b253dcac40e63e2032c33ea634b523d6ec01755ddaa` |
| 23 | `2d34c42d6ae977a0f222c609e219018a3ad876f05b0754d050f03deeec134beb` |
| 26 | `bb762dd0a0fabee40c1460a46428ee5939d5189667c339daa295fed2f43c3bc8` |
| 27 | `3bf80e2b011bdff930d9c18a3d7fd85d1021292f68f48a4f579be01cbba60116` |
| 28 | `9d4a0d782c1ff991d0517adaacf303c9262979020548e10a07d97fe769801c0f` |
| 29 | `c8b58fc924e5c16be75912d09c60267b149d425256ee8e4f442dac3ca3f5fde2` |

## Run outcome

The Web and HeadingAgent assemblies both reported build
`a7c0c35f7748b1b882dabd37b65450cf99d2bf04`. The scan returned 79 headings, with
74 placed and 5 unresolved. The unresolved summary was 5 direct model rejects,
0 protocol no-verdicts, 0 ancestor-chain blocks, and 0 graph conflicts.

The previous V41 coverage failure did not recur. The same four aliases
`P017-L031` through `P017-L034` appeared under candidate `V40`; expected and
observed membership sequences matched completely, with no missing, extra, or
duplicate aliases. The candidate received `ACCEPT` in `VERIFY-005`, round 1.
Candidate IDs changed, so the alias scope is the stable comparison key here.

Hierarchy repair resolved 28 of 33 repaired edges after fresh re-verification.
The remaining 5 terminal rejects were `P008-L024`, `P027-L001`, `P028-L001`,
`P028-L013`, and `P029-L010`. No graph-reverification phase ran.

## Source-adjudicated boundary errors

| Scope | Audit trajectory | Source review | Finding |
|---|---|---|---|
| `P026-L003..L004` | Cycle 0 merged both lines. Cycle 1 split them. Cycle 2 merged them again and the verifier accepted. | Page 26 prints “4. Lời chứng chứng thực hợp đồng, giao dịch” and, as a separate line, “a) Lời chứng chứng thực hợp đồng, giao dịch”. | The final accepted merge is wrong. This is a concrete A→B→A oscillation ending in acceptance of A, although the source shows two structural units. |
| `P023-L009..L010` | `REVIEW-VERIFY-001` accepted the two aliases as one current group. | Page 23 prints “2. Lời chứng chứng thực chữ ký” followed by a separate “a) Lời chứng…” form heading. | A second false merge, this time without a boundary-repair cycle. |

The four unresolved appendix headings `P027-L001`, `P028-L001`, `P028-L013`,
and `P029-L010` were proposed under `P023-L002` (`I. LỜI CHỨNG`) and rejected.
The page sequence shows they belong under the separate `4.` heading at
`P026-L003`; its `a)` item at `P026-L004` is a sibling subheading. The false
merge removes the clean parent needed by those later headings.

These observations support an oscillation canary. They do not make a generic
“return to a previously rejected partition” rule safe by themselves: the
separate V17/V24 source adjudication found cases where partition A was the
source-correct partition after a model proposed B. A returned partition must
therefore remain unresolved or be compared through an independent, source-
grounded qualification step before any production policy is chosen.

## Source-adjudicated hierarchy errors

On page 8, `P008-L013` is Chương II, `P008-L016..L017` is Mục 1, and
`P008-L018` is Điều 16. The current run correctly accepted Chương II as a root,
but accepted Điều 16 with parent `P008-L013` even though its evidence references
also included Mục 1. The source structure places Điều 16 under Mục 1. This is a
resolved-but-wrong hierarchy edge, not an unresolved cascade.

For `P008-L024` (Điều 17), the initial edge to Mục 1 was rejected, the repair
proposed the same parent, and fresh re-verification rejected it again. Page 8
shows Điều 17 follows Điều 16 within Mục 1, so this is a source-supported false
negative. The current safe audit records the rejection and evidence references,
but does not retain the verifier's source-context packet.

The old false edge Chương II → Chương I did not recur in this execution:
`P008-L013 → ROOT` was accepted without a repair entry. That run-specific result
does not establish that whole-graph semantic false accepts are fixed.

## Upstream recall cases

The loss of `P002-L021..L023` (Điều 3) is a grouping dispute: both initial and
review selection chose the lines, while `GROUP-001` and `REVIEW-GROUP-001` both
ended `DISPUTED`. `P023-L004` (the “1. Lời chứng chứng thực bản sao từ bản
chính” heading) is a separate selection false negative: `SEG-039` returned
`NOT_SELECT`, with no later selection review for that alias.

The two accepted false merges, the two kinds of hierarchy error, the grouping
dispute, and the selection miss are separate failure classes. A single
placed/unresolved count cannot measure them reliably.

## Evidence limits

The review inspected source rasters for PDF pages 8, 23, and 26–29 and matched
them to the retained OCR cache for the source PDF hash above. It covers these
local structures only. It is not exhaustive document review and must not be
promoted to Strict Gold without the required user adjudication.
