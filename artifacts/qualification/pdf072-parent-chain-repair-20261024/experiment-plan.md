# Parent-chain repair qualification

Do not change Gold, source text, selection, grouping, or the initial placement protocol/prompt.
Candidate `RepairParentChains` is false by default. After initial placement, identify claimed ancestor
chains that cannot reach ROOT; ask the model to reconsider their affected descendants and model-owned
blocking ancestors. Upstream relations are fixed. Preserve all unaffected relations. Never infer a
replacement parent or turn a NONE/UNRESOLVED into ROOT in C#.

At most two repair passes, each using the existing three-round bounded protocol repair, request-byte,
context/output, attempt, cost and transport guards. Recheck the whole graph after each pass. Stop on
unchanged proposals. A model may explicitly return UNRESOLVED; this does not count as a resolved parent.

First pass adversarial unit/regression tests. Then reconstruct the exact frozen source-bound PDF072
selection and the three original placement request/reply pairs. Prove A reproduces the original 13
unresolved parents. Replay those same initial pairs for B, send only its repair requests live, compare
all requested/changed relations and heading identities. Require zero residual chain conflicts, at most
five unresolved parents (the retry-fixed production baseline), unchanged heading membership/text/groups,
no fabricated parent aliases and no unexpected protocol failures before an end-to-end candidate.

If the controlled trial passes, run one end-to-end PDF072 extraction with source-bound selection and
chain repair enabled, other failed experimental flags disabled. Score against registered DOC-0252 Gold.
Compare against both retry-fixed production and the prior source-bound run. Require no selected-alias
precision/recall or exact-boundary precision/recall regression from the source-bound run, at most five
unresolved parents, zero residual chain conflicts, and elapsed time under 300 seconds for this sample.
Do not rerun failed full samples selectively until a passing score appears. A single document cannot
establish general accuracy, parent accuracy (Gold lacks hierarchy capability), or a p95 five-minute SLA.
Broad production promotion requires evidence beyond this development document.
