# PDF072 full-page-image Luna experiment (2026-10-25)

## Result

Recovery run `run-02` completed the native `PdfHandler.ExtractAsync` V12 chain. It used the same 15 frozen image-conditioned SELECT decisions as failed `run-01`, verified by identical request SHA values and replayed response files. The two recovery GROUP request SHA values were identical to run-01; the SHA-bound GROUP replies authored for run-01 were reused. Four PLACE batches were authored fresh in run-02. The 15 page renders were viewed before any semantic decisions. No provider API was called.

Run identity, raw requests/replies, lane output, and pre-Gold freeze are in `artifacts/qualification/pdf072-full-image-luna-20261025/run-02/`. Run-01 remains preserved as the failed partial attempt. The image-evidence sidecars and original full-page PNGs are in the experiment directory.

### Image-input boundary

Luna was shown all 15 rendered pages in the same agent context before it authored the semantic replies, so it could compare evidence across pages. The saved production request JSONs themselves are text-only (about 103 KB for the sampled request) and contain no image payload/reference. This is therefore a full-document visual-context experiment through the Luna operator context, not a wire-level test of sending each page image inside the provider packet. Do not treat it as proof that the production provider transport currently accepts or uses image payloads.

## Frozen decisions and completeness

- Source: `a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61`; DOC-0252 Gold: `e966d4e57ba6b312a2cebe93446c4350d4ef7173458f7c528306775f9c743b03`.
- SELECT: 650 unique source aliases, all assigned exactly once; 64 selected.
- Lane: complete (15/15 units); 60 final heading groups.
- Calls: 15 SELECT, 2 GROUP, 4 PLACE; every response is SHA-bound to its request.
- Frozen native-run-result JSON SHA-256: `795132e46fe7b9b6563ee40805872f347a828a78a853313ff153d8ca531c4edb`.
- Frozen envelope (`frozen-output.json`) SHA-256: `af0474cfd17d6ffea0c0cebf8dbf2893497da9542145f2885f80555a74f8e70e`.

### Failed initial attempt

`run-01` is not a valid full output: route audit records semanticLane `partial`, 7/15 units completed, 8 not started. GROUP-001 failed `CALL:TRANSPORT:TIMEOUT_OR_CANCELLED` for R1–R8; GROUP-002 failed likewise for R9–R15. Both matching request and response files are present and their response envelopes contain the exact request SHA, but the reply files were written about seven minutes after request creation, after the bridge wait expired. The bridge call log contains no completed GROUP calls. Earliest loss was AgentGrouping transport timeout; response coverage and downstream binding/assembly cannot be blamed from this failed attempt. Recovery replays these same bound GROUP replies against identical request SHAs and completes the production path.

## DOC-0252 Gold score

The 45 Gold aliases form 42 groups; hierarchy is not evaluable in this Gold.

| Stage | TP | FP | FN | Precision | Recall |
|---|---:|---:|---:|---:|---:|
| SELECT membership | 43 | 21 | 2 | 0.672 | 0.956 |
| Final native output membership | 43 | 21 | 2 | 0.672 | 0.956 |
| Exact groups | 41 | 19 | 1 | — | — |

SELECT and final membership agree: GROUP formed the selected schedule entries into groups but did not change membership.

### False-positive aliases

All 21 are page 13–14 Agenda schedule rows or agenda-topic entries that the model treated as structural headings. The page image and raster/layout evidence were present; this is a classification error, not missing visual evidence.

- `L0522:S1` (p. 13): Welcome and Opening Remarks [TAG Co-Chairs, World Bank]
- `L0524:S1` (p. 13): Update on the ICP 2021 and 2024 Cycles [World Bank and ICP Regional
- `L0527:S1` (p. 13): Research Topics Emerging from the Previous ICP Cycles [World Bank]
- `L0528:S1` (p. 13): Forthcoming Research Topics [Prasada Rao]
- `L0529:S1` (p. 13): Setting Up a Short- and Long-Term Research and Development Agenda [Co-
- `L0532:S0` (p. 13): 14:00 – 14:15 TAG Composition and Terms of Reference [TAG Co-Chairs]
- `L0534:S1` (p. 14): Terms of Reference for a Task Force on Annual PPP Production [World Bank]
- `L0535:S1` (p. 14): Terms of Reference for a Task Force on ICP Classification Update [World Bank]
- `L0537:S1` (p. 14): The Treatment of Import and Export Prices in International Comparisons
- `L0541:S1` (p. 14): A Survey Based Approach to Adjustment for Quality Differences in Services in
- `L0542:S0` (p. 14): International Price Comparisons [Naohito Abe and Prasada Rao]
- `L0543:S1` (p. 14): Treatment of Scanner (Transaction) Data in the European Comparison
- `L0544:S0` (p. 14): Program (ECP) [Eurostat]
- `L0545:S1` (p. 14): Treatment of Negative Expenditures in ICP [Prasada Rao and Sergey Sergeev]
- `L0546:S1` (p. 14): Fine Tuning Global Linking Procedures
- `L0547:S1` (p. 14): Alternative Global Linking Methods [Robert Hill]
- `L0548:S1` (p. 14): Similarity Linking Within and Between Regions [Erwin Diewert and Yuri
- `L0550:S0` (p. 14): 14:00 – 15:00 Improving Reliability of Price Comparisons
- `L0551:S1` (p. 14): Basic Heading PPPs and Sparsity of Price Tableaus [Erwin Diewert]
- `L0552:S1` (p. 14): Alternative Basic Heading PPP Aggregation Methods [Yuri Dikhanov]
- `L0554:S0` (p. 14): 15:00 – 15:15 Any Other Business and Closing [TAG Co-Chairs]

### Missing Gold aliases

- `L0513:S0` (p. 13): International Comparison Program (ICP)
- `L0514:S0` (p. 13): TECHNICAL ADVISORY GROUP (TAG)

The two misses are the page 13 document-title lines `International Comparison Program (ICP)` and `TECHNICAL ADVISORY GROUP`; the run selected the separate Agenda schedule entries instead.

## Comparison to native text-only run

The authorized text-only run scored TP 37 / FP 7 / FN 8. This image run scored TP 43 / FP 21 / FN 2: +6 TP, +14 FP, and 6 fewer FN. On this single run, full-page image context improved recall but reduced precision. Exact groups scored 41 TP / 19 FP / 1 FN.

## Limits

- DOC-0252 does not score hierarchy, so placement accuracy is not reported as a quality metric.
- This is one exploratory run and supports no production-promotion claim.
- Run-01 remains a failed transport attempt and is excluded from the valid score.

### Stage audit for run-01’s 32 withheld aliases

The selector made 650 decisions and selected 64 aliases. Both GROUP requests covered all 17 requested boundaries (9 in GROUP-001 and 8 in GROUP-002), and both saved replies cover their packet targets and carry the exact request SHA. At runtime, however, GROUP-001 timed out for R1–R8 and GROUP-002 timed out for R9–R15 before those late reply files appeared. Therefore the first confirmed loss is in AgentGrouping’s transport result, before SelectionAuthority’s grouping/binding output; StructureAssembler and ParentResolver received an incomplete grouping result. The production artifacts do not provide a causal per-alias field for those 32 `upstreamWithheld` markers, so the timeout is the run-level cause and not a proved unique explanation for each alias.

The 32 aliases marked `upstreamWithheld` in run-01 are: `L0000:S0`, `L0001:S0`, `L0025:S0`, `L0026:S0`, `L0165:S0`, `L0166:S0`, `L0271:S0`, `L0272:S0`, `L0334:S0`, `L0335:S0`, `L0359:S0`, `L0360:S0`, `L0519:S0`, `L0520:S0`, `L0521:S0`, `L0522:S1`, `L0523:S0`, `L0531:S0`, `L0532:S0`, `L0535:S1`, `L0536:S0`, `L0539:S0`, `L0540:S0`, `L0541:S1`, `L0542:S0`, `L0543:S1`, `L0544:S0`, `L0552:S1`, `L0553:S0`, `L0554:S0`, `L0556:S0`, `L0557:S0`.

Run-02 resolves the experiment-level propagation question: the same 64 frozen selected aliases all appear in its completed native output, grouped into 60 headings. Thus the 32 run-01 absences were a failed-run artifact, not evidence of loss in SelectionAuthority, StructureAssembler, or ParentResolver under a completed GROUP response.
