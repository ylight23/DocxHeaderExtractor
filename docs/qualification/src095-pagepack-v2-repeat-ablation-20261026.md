# SRC-095 page-packing repeat ablation (V2 response contract)

Date: 2026-10-26  
Purpose: repeat the page 47–48 split-versus-paired Luna-agent ablation after adding the source-bound NOT_SELECT reason contract, while keeping the PDF V12 semantic instruction unchanged.

## Frozen setup

- The three request packets were frozen and reused byte-for-byte for three independent blind Luna-agent trials per branch: pages 47+48 together, page 47 alone, and page 48 alone.
- All packets use V12 instruction SHA-256 `a71ab606f8bfb1d7a8f543e967aa8de6ef8373c7d970d9a6b5cf10ca04878b7b` and response protocol `PDF_UNIT_SELECT_SOURCE_BOUND_V2`.
- Each response was frozen before Gold scoring. The complete request/response SHA-256 manifest is [all-trials-manifest.json](../../artifacts/qualification/src095-pagepack-v2-repeats-20261026/frozen/all-trials-manifest.json); trial 1 also has a standalone freeze manifest.
- Gold was `SRC-095.gold.v1.json`, SHA-256 `8c7cea0ada3d3af2a2fe9f7f48375575f40e987442a1d4586462ad7706f58ef5`.

## Packet sizes and protocol validation

| Branch | Target units | Serialized request | Pages/images | V12 instruction hash | Response validation |
| --- | ---: | ---: | --- | --- | --- |
| Paired | 44 | 591,708 bytes | 47–48 / 2 | identical | 3/3 passed |
| Page 47 | 28 | 295,605 bytes | 47 / 1 | identical | 3/3 passed |
| Page 48 | 16 | 271,995 bytes | 48 / 1 | identical | 3/3 passed |

All nine responses had exactly one decision for every target unit, no duplicate or missing unit IDs, source-bound selections, and a nonempty reason of at most 300 characters on every NOT_SELECT.

## Gold results

The page-scoped Gold contains two headings on page 47 (`L1677:S0`, `L1688:S0`) and two on page 48 (`L1710:S0`, `L1729:S0`).

| Trial | Branch | TP | FP | FN | Selected aliases |
| ---: | --- | ---: | ---: | ---: | --- |
| 1 | Paired | 4 | 0 | 0 | L1677, L1688, L1710, L1729 |
| 1 | Page 47 | 2 | 0 | 0 | L1677, L1688 |
| 1 | Page 48 | 2 | 0 | 0 | L1710, L1729 |
| 2 | Paired | 4 | 0 | 0 | L1677, L1688, L1710, L1729 |
| 2 | Page 47 | 2 | 0 | 0 | L1677, L1688 |
| 2 | Page 48 | 2 | 0 | 0 | L1710, L1729 |
| 3 | Paired | 4 | 0 | 0 | L1677, L1688, L1710, L1729 |
| 3 | Page 47 | 2 | 0 | 0 | L1677, L1688 |
| 3 | Page 48 | 2 | 0 | 0 | L1710, L1729 |

## Interpretation and action

This repeat does **not** reproduce the page-packing FN: all three branches selected all four Gold headings in all three trials. It therefore does not justify changing the generic scheduler or imposing a split-page rule. Keep the existing measured request-budget scheduler unchanged for now.

The earlier V1-contract observation and this V2-contract result are not a clean causal comparison of only page packing: the current experiment holds V12 semantic instruction fixed across branches but changes the response contract from the earlier V1 run. The new reason requirement may affect model behavior. The within-V2 branch comparison is the relevant packing ablation, and it shows no packing regression in these Luna-agent trials.

For context, the earlier single V1 run scored the three Appendix subsection targets as 0/3 in the paired branch, 0/1 on page 47 (`A.1. Streams` still missed even when isolated), and 2/2 on page 48. The new V2 agent runs scored 3/3 of those subsection targets in every branch; they also selected the Appendix A title on page 47. This cross-run change makes the old page-packing hypothesis less conclusive and points to run/contract variation as an unresolved confound. It does not prove that the reason field caused the improvement.

This is **not production-provider evidence**. Luna agents authored the responses; these were not Qwen/OpenRouter calls, and no provider latency or five-minute SLA was measured. A prior provider preflight returned HTTP 402. Do not promote a scheduler change based on this run. Repeat the frozen packet comparison through the actual configured provider when that route is available, then measure the full-document SLA separately.
