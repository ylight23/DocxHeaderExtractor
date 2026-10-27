# Per-heading audit

Run: `artifacts/qualification/pdf072-pairwise-grouping-v2-20261024/full-02/run-result.json`

Compared with: `artifacts/qualification/pdf072-parent-chain-repair-20261024/full/run-result.json`

Gold evaluates membership and exact groups. Parent/level below are model output; hierarchy has no evaluable Gold.

## All membership changes

| Alias | Before | After |
|---|---|---|
| L0002:S0 | SELECT | NOT_SELECT |
| L0003:S0 | SELECT | NOT_SELECT |
| L0513:S0 | NOT_SELECT | SELECT |
| L0514:S0 | NOT_SELECT | SELECT |
| L0519:S0 | NOT_SELECT | SELECT |
| L0550:S0 | NOT_SELECT | SELECT |
| L0554:S0 | NOT_SELECT | SELECT |

## Every output heading

| # | Aliases | Text | Exact Gold group | Same group before | Parent | Level |
|---|---|---|---|---|---|---|
| 1 | L0000:S0, L0001:S0 | MINUTES OF THE INTERNATIONAL COMPARISON PROGRAM TECHNICAL ADVISORY GROUP | False | False | ROOT | 1 |
| 2 | L0004:S0 | Session I: Welcome and meeting objectives | True | True | ROOT | 1 |
| 3 | L0025:S0 | Session II: Update on the ICP 2021 Cycle | True | True | ROOT | 1 |
| 4 | L0026:S0 | 1. Global office update | True | True | L0025:S0 | 2 |
| 5 | L0078:S0 | 2. Regional updates | True | True | L0025:S0 | 2 |
| 6 | L0081:S0 | Africa | True | True | L0078:S0 | 3 |
| 7 | L0089:S0 | Asia and the Pacific | True | True | L0078:S0 | 3 |
| 8 | L0107:S0 | Commonwealth of Independent States (CIS) | True | True | L0078:S0 | 3 |
| 9 | L0123:S0 | Eurostat–OECD PPP Program | True | True | L0078:S0 | 3 |
| 10 | L0137:S0 | Latin America and the Caribbean | True | True | L0078:S0 | 3 |
| 11 | L0150:S0 | Western Asia | True | True | L0078:S0 | 3 |
| 12 | L0165:S0 | Session III: Short- and Long-Term Research and Development Agenda | True | True | ROOT | 1 |
| 13 | L0166:S0 | 1. Research Topics Emerging from the Previous ICP Cycles | True | True | L0165:S0 | 2 |
| 14 | L0219:S0 | 2. Forthcoming Research Topics | True | True | L0165:S0 | 2 |
| 15 | L0271:S0 | Session IV: TAG Functioning and Terms of Reference for Task Forces | True | True | ROOT | 1 |
| 16 | L0272:S0 | 1. TAG Composition and Terms of Reference | True | True | L0271:S0 | 2 |
| 17 | L0288:S0 | 2. Terms of Reference for a Task Force on Annual PPP Production | True | True | L0271:S0 | 2 |
| 18 | L0315:S0 | 3. Terms of Reference for a Task Force on ICP Classification Update | True | True | L0271:S0 | 2 |
| 19 | L0334:S0 | Session V: Current Research | True | True | ROOT | 1 |
| 20 | L0335:S0 | 1. The Treatment of Import and Export Prices in International Comparisons | True | True | L0334:S0 | 2 |
| 21 | L0359:S0, L0360:S0 | 2. A Survey Based Approach to Adjustment for Quality Differences in Services in International Price Comparisons | True | True | L0334:S0 | 2 |
| 22 | L0377:S0 | 3. Treatment of Scanner (Transaction) Data in the European Comparison Programme (ECP) | True | True | L0334:S0 | 2 |
| 23 | L0400:S0 | 4. Treatment of Negative Expenditures in ICP | True | True | L0334:S0 | 2 |
| 24 | L0420:S0 | 5. Fine Tuning Global Linking Procedures | True | True | L0334:S0 | 2 |
| 25 | L0470:S0 | 6. Improving Reliability of Price Comparisons | True | True | L0334:S0 | 2 |
| 26 | L0507:S0 | Session VI: Any Other Business | True | True | ROOT | 1 |
| 27 | L0513:S0, L0514:S0 | International Comparison Program (ICP) TECHNICAL ADVISORY GROUP (TAG) | True | False | ROOT | 1 |
| 28 | L0519:S0 | Agenda | True | False | L0513:S0 | 2 |
| 29 | L0520:S0 | DAY 1: Friday, March 7, 2025 \| Chair: Paul SCHREYER | True | True | L0519:S0 | 3 |
| 30 | L0521:S0 | SESSION I: Welcome and Opening Remarks | True | True | L0520:S0 | 4 |
| 31 | L0523:S0 | SESSION II: Update on the ICP 2021 and 2024 Cycles | True | True | L0520:S0 | 4 |
| 32 | L0526:S0 | SESSION III: Short- and Long-Term Research and Development Agenda | True | True | L0520:S0 | 4 |
| 33 | L0531:S0 | SESSION IV: TAG Functioning and Terms of Refence for Task Forces | True | True | L0526:S0 | 5 |
| 34 | L0536:S0 | SESSION V: Current Research | True | True | L0526:S0 | 5 |
| 35 | L0539:S0 | DAY 2: Saturday, March 8, 2025 \| Chair: Prasada RAO | True | True | L0519:S0 | 3 |
| 36 | L0540:S0 | SESSION V: Current Research (Cont’d) | True | True | L0539:S0 | 4 |
| 37 | L0550:S0 | 14:00 – 15:00 Improving Reliability of Price Comparisons | False | False | L0540:S0 | 5 |
| 38 | L0553:S0 | SESSION VI: Closing | True | True | L0539:S0 | 4 |
| 39 | L0554:S0 | 15:00 – 15:15 Any Other Business and Closing [TAG Co-Chairs] | False | False | L0553:S0 | 5 |
| 40 | L0556:S0 | Annex 2: List of Participants | True | True | ROOT | 1 |
| 41 | L0557:S0 | ICP Technical Advisory Group members | True | True | L0556:S0 | 2 |
| 42 | L0566:S0 | ICP experts, guest speakers, and observers | True | True | L0556:S0 | 2 |
| 43 | L0576:S0 | ICP Inter-Agency Coordination Group (IACG) | True | True | L0556:S0 | 2 |
| 44 | L0587:S0 | World Bank | True | True | L0576:S0 | 3 |
