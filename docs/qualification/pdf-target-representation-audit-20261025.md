# PDF target representation audit and controlled pair — 2026-10-25

## Scope

Audit V13 versus V14, then isolate input target representation. This work does not change application defaults, the production selection parser, DOCX, scan, grouping, placement, or Gold. Artifacts: `artifacts/qualification/pdf-target-representation-audit-20261025/02/`.

## Historical request evidence

The harness reconstructs all 15 first requests for each historical version. The audit compares their UTF-8 SHA-256 to the first-turn `RequestSha256` in the actual historical traces. **All 15 V13 and all 15 V14 hashes match.** These are therefore byte-verified reconstructions, not requests inferred merely from code comments or resource plans.

Across all pages, target alias/text/order, base target facts, context text, document styles, tool declarations, and generation settings match. Both versions retain complete pages on PDF072. Neither historical first packet includes context facts; later tool results must be inspected separately.

| Difference | V13 | V14 |
|---|---|---|
| System prompt and output | Production source-bound alias decisions | New unit decisions with selected member arrays |
| Semantic instruction string | Shared V12 policy plus block-evidence paragraph | Shared V12 policy |
| Target facts | Flat targetFacts including expanded layoutBlockEvidence | Base facts nested inside members, no expanded layoutBlockEvidence |
| Layout evidence gate | SelectionTask enforces get_page_layout target coverage | PdfUnitSelectionTask does not enforce that gate |
| Extent and uncertain results | Existing parser handles headingExtent and unresolved/clarification states | Unit parser has COMPLETE and SELECT/NOT_SELECT only; does not preserve extents |
| Strict validation | Existing parser rejects extra/repeated keys | Unit parser lacks equivalent strictness |
| Planning measurement | Alias first request corresponds to selection request | Planner measures alias packet although actual selection uses custom unit packet |

The gate difference follows from AgentSource constructing PdfFormat with requirePageLayoutEvidence=true for both experimental flags, while the two task implementations enforce different checks. Historical V13 page 14 first returned an answer, then called tools in turn 2, then returned its answer in turn 3. V14 is not an isolated test of block size or input representation.

## Controlled experiment

`tools/qualification/TargetRepresentationTrial/` creates two arms from the same production SelectionTask. Both use the same source-bound alias response contract, parser, semantic instruction, shared system prompt, facts, extended block evidence, context text and facts, styles, tools, completion allowance and resource profile. One common system explanation describes both input shapes without assigning semantic meaning to geometry.

- Alias arm: flat `targets` array.
- Unit arm: `targets` array of parser block containers whose members are exactly the same source occurrences.

Assertions flatten the unit arm back to aliases and require exact content/order equality. After removing targets, complete payloads must be identical. After masking that verified payload difference, complete wire requests must be identical. Actual serialized requests from both arms must pass byte, prompt estimate, context and completion admission with continuation reserve.

All 30 page packets pass parity and admission. Page 14 has 33 targets: alias 111,195 bytes, unit 112,221 bytes; completion cap 32,768 in both. Page 15 has 76 targets: alias 158,643 bytes, unit 161,531 bytes. Its increase over historical packets is attributable to the common expanded evidence and context facts, not an atom-count change. Admission uses the configured 262,144-byte profile with continuation reserve.

A single independent pair is run on page 14 with a six-post limit per arm. Each request and raw model reply is saved. The offline scorer loads registered Gold after execution; Gold is never sent to the model. Results are recorded in `audit-and-score.json`.

## Observed results

| Arm | Status | TP | FP | FN | Posts | Seconds |
|---|---|---:|---:|---:|---:|---:|
| Alias | COMPLETE | 4 | 1 | 0 | 1 | 46.861 |
| Layout unit | COMPLETE | 4 | 2 | 0 | 1 | 49.332 |

Both arms incorrectly select `L0550:S0` (the time-prefixed Improving Reliability of Price Comparisons agenda entry). The unit arm also selects `L0554:S0` (the time-prefixed Any Other Business and Closing agenda entry). These are membership false positives relative to the registered Gold, not parser source-binding failures. Both arms accept all four Gold headings and finish on their first post without tool calls.

The flat alias arm's false positive demonstrates that small standalone target units are not necessary for FP under the common conditions. The one-extra-FP difference does not establish a reproducible representation effect. The former 16-FP V14 result cannot be explained from this pair as input nesting alone: evidence, output protocol, system prompt and tool gate differ between the historical experiment and this controlled one. No individual contribution has yet been estimated.

## Interpretation limits

One pair can show the observation under these shared conditions, not stability or a definitive causal explanation of the old V14 regression. It isolates input nesting while retaining alias output. It does not separately isolate V14's unit-output contract, tool gate removal, or dropped expanded evidence. No grouping or hierarchy is evaluated. A pass is not permission to promote.

The interrupted first harness preparation attempt is preserved in `01/`. It failed locally because a facts range crossed the non-owned core between context sides; no provider requests were made. The corrected harness reads each context side as a separate contiguous source range.
