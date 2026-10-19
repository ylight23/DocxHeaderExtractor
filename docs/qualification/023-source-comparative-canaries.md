# 023 source-comparative qualification canaries

These two experiments are deliberately separate from extraction and production
authority. Neither writes headings, changes hierarchy edges, updates Gold, or
changes the production assembly. Their only source is the previously pinned
023 scan/OCR manifest and its cached transcription. The declared source PDF
SHA256 is `762b29b5c61411349d08858bba9eb3be3f417f00dc02a4c4179ca8400ec006a8`.

The local visual source review in
[the live adjudication note](023-a7c0c35-live-adjudication.md) is not Strict
Gold. Model choices in these canaries are qualification observations, not
ground truth.

## Boundary oscillation comparison

The command predeclares exactly three cases:

| Case | A | B | Purpose |
|---|---|---|---|
| `P026-OSCILLATION` | final merged partition `[[P026-L003,P026-L004]]` | split partition `[[P026-L003],[P026-L004]]` | The observed live A→B→A trajectory ending in ACCEPT of A |
| `V17-CONTROL` | three structural headings on page 8 | merges Mục 1 with Điều 16 | Control where returning to A was source-adjudicated as correct |
| `V24-CONTROL` | Mục 3 and Điều 23 remain separate | merges both headings | Second control where returning to A was source-adjudicated as correct |

Each case gets one fresh source-grounded comparative POST. The packet contains
only the predeclared source aliases, their OCR readings and layout facts, the
two partitions, and the structural A→B→A trajectory. It contains no old
verifier reason, completion, or authority. A valid response selects A, B, or
INDETERMINATE and cites only aliases in the packet. The C# parser checks the
response shape and citation scope; it does not decide which partition is
correct. A/B labels and the recurrence are explicitly described as non-evidence
in the prompt.

From the repository root, validate source/cache availability without a model
call:

```powershell
dotnet run --project src/DocxHeaderExtractor.V6Qualification -- boundary-oscillation-canary .dhx-evidence/eval/023-static-ab-f3454ff.manifest.json .dhx-evidence/eval/023-boundary-oscillation-canary --validate
```

For the explicitly authorized qualification run, use a new output directory:

```powershell
dotnet run --project src/DocxHeaderExtractor.V6Qualification -- boundary-oscillation-canary .dhx-evidence/eval/023-static-ab-f3454ff.manifest.json .dhx-evidence/eval/023-boundary-oscillation-canary --live
```

The command admits at most three POSTs and USD 0.50. Full requests and
completions are retained only under that local output's `private` directory;
`report.json` contains only pinned source hashes, request hashes, finish/size,
decisions and alias references.

## Hierarchy comparative canary

One fresh model call jointly compares immediate-parent candidates for
`P008-L018` (Điều 16) and `P008-L024` (Điều 17). Both targets receive the same
nearby candidate parents—`P008-L016` (Mục 1), `P008-L013` (Chương II), ROOT,
or UNRESOLVED—and source lines/layout facts from page 8. The response must
account for both children, cite supplied aliases, and declare whether both
selected parents match. The report computes whether both children received
the same parent, but does not promote a result or repair an edge.

Validate without a model call:

```powershell
dotnet run --project src/DocxHeaderExtractor.V6Qualification -- hierarchy-comparative-canary .dhx-evidence/eval/023-static-ab-f3454ff.manifest.json .dhx-evidence/eval/023-hierarchy-comparative-canary --validate
```

Run the one-POST live qualification:

```powershell
dotnet run --project src/DocxHeaderExtractor.V6Qualification -- hierarchy-comparative-canary .dhx-evidence/eval/023-static-ab-f3454ff.manifest.json .dhx-evidence/eval/023-hierarchy-comparative-canary --live
```

It is capped at one POST and USD 0.50. As with the boundary canary, the full
request and response stay local under `private`; the report is alias-only and
qualification-only. A result assigning both articles to Mục 1 would agree with
the local source review, but still would not be Strict Gold.

Neither experiment includes the Điều 3 grouping dispute or the `P023-L004`
selection miss. Those are separate upstream failure classes and remain outside
these comparisons.
