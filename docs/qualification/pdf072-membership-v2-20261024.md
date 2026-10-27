# PDF072: audit concurrency and clarify heading membership

## Observed omissions and local scheduler

The preceding source-bound and combined trials each own all 650 PDF072 aliases exactly once in 15
scopes. All 15 scopes complete. Every last accepted provider decision agrees with the collected unit
decision. The combined trial's missing L0513:S0, L0514:S0 and L0519:S0 are already NOT_SELECT in the
provider response; they were not dropped by the collector. See `prior-concurrency-audit.json`.

Selection uses an indexed outcome array: each parallel unit writes its own array entry, and collection
happens after Parallel.For completes. Each conversation constructs its own task, evidence-tool state
and history. The source snapshot and scope ownership are shared read-only. Serial scheduling does
not automatically pass earlier decisions or the whole document into a later scope: context and
instruction are the same. Scheduling and segmentation are separate experimental factors.

The actual selector collector is tested at parallelism 1, 6 and 6 with controlled completion ordering
and deterministic per-target responses. All 650 decisions, selected aliases and per-scope request
hashes agree, including decisions to reject a target. This checks local aggregation without asking a
provider to make deterministic semantic judgements. Existing fake-transport tests also exercise the
shared client with concurrent selector conversations.

This evidence does not support local concurrency loss as the mechanism of the inspected omissions.
It does not prove that provider load cannot affect a completion, or that page-sized independent
context is sufficient for every document. A small live scheduler study cannot establish those claims.

## Candidate change

`PdfClarifyMembership` is false by default. When explicitly enabled, the PDF selector appends a generic
clarification to its membership instruction. The existing source-bound V1 system/schema/parser stays
unchanged. An explicit custom instruction still takes precedence; the instruction hash distinguishes
the candidate selector and request identity.

The clarification addresses a hypothesis from the prior visible JSON explanations: the model sometimes
reduces “names, introduces, or divides” to “divides,” calls an embedded title metadata, and conversely
includes dates and locations as title lines merely because they share formatting. It asks the model
to judge local titles at embedded-document and appendix boundaries by the same functions, separate
accompanying descriptive metadata, and keep membership independent of later parent placement.

No document name, alias or Gold label is in the instruction. No C# keyword rule restores headings.
There is no extra semantic-review call in this candidate. Source facts, protocol validation, extent
guards, grouping and transport settings remain in effect. Whole-document placement and inline grouping
remain disabled. A prompt clarification is a candidate fix, not proof of correctness by itself.

## Verification and live-study design

28 focused tests and 241 broader regression tests pass, with no failures or skips. The regression
suite includes the forced concurrency test, custom-instruction precedence, source binding, existing
PDF parity, grouping, parent repair, hierarchy, retry/budgets, DOCX/scan and architecture. TRX:
`tests/DocxHeaderExtractor.Tests/TestResults/membership-v2-regression.trx`.

The predeclared live study compares A=source-bound V1 instruction and B=clarified instruction, in
A-serial, B-parallel, B-serial, A-parallel order. It holds the target sets and evidence fixed for
PDF072 scopes 000/007/012 and PDF076 scope 000. B differs only in user instruction and its hash;
complete wire comparisons enforce this. Parallelism is configured to six, but there are four jobs,
so at most four live conversations run concurrently. Each job keeps a private conversation and budget.

Current registry Gold for DOC-0252 and DOC-0256 is used only by the scorer, never in provider requests.
Every outcome, including failed membership and protocol repairs, counts. Both B repetitions must have
exact membership on all three development scopes and must not regress versus A on the independent
document's scope before full extraction is authorized by the experiment gate. Two predeclared full
PDF072 samples are required if this gate passes; the complete criteria are in `experiment-plan.md`.

Artifacts: `artifacts/qualification/pdf072-membership-v2-20261024/`, including source identity,
isolated builds, prior audit, fixed-study requests, raw local replies and scored outcomes.

## Results and decision

V2 fails and is not promoted or advanced to full extraction. All 16 live outcomes are retained.
Both documents' Gold hashes pass verification. The 311 snapshotted source files match before starting
the next revision. Total confirmed study spend: USD 0.017030328, 16 provider attempts.

| Profile | Scheduling | Gold aliases selected / 20 | False positives | Missed Gold aliases |
|---|---|---:|---:|---:|
| A: source-bound V1 | Serial | 20 | 6 | 0 |
| B: clarification V2 | Parallel, at most 4 jobs | 18 | 1 | 2 |
| B: clarification V2 | Serial | 15 | 1 | 5 |
| A: source-bound V1 | Parallel, at most 4 jobs | 20 | 6 | 0 |

B-parallel misses L0513:S0 and L0514:S0 on PDF072. B-serial misses those plus L0519:S0, and also
misses the two title lines of PDF076. These are explicit model NOT_SELECT decisions in complete
responses, without protocol repair. The raw-to-final audit and deterministic collector test rule out
a lost collector result for the inspected cases. The live serial failure demonstrates that parallel
scheduling is not necessary for the omission. No statistical conclusion about provider-load effects
is made from one observation per cell.

The V2 attempt reduces metadata false positives but suppresses genuine institutional title wording;
this is a regression, not a successful fix. The next revision retains V2 as frozen evidence, revises
the positive local-title criterion and adds explicitly opt-in, bounded model review. It has its own
plan and source identity under `pdf072-membership-v3-review-20261024/`.
