# Membership clarification and scheduler audit

Before further provider calls, preserve prior failures and distinguish three hypotheses: missing source
coverage, corruption/lost results due to local concurrency, and semantic disagreement in provider replies.
Audit all prior scope ownership and raw-to-final decisions. A complete NOT_SELECT in the provider reply is
not a collector omission. Independent scope context is the same whether requests are serial or parallel.
Do not claim that a small live sample can rule out effects of provider load or hidden backend variation.

Candidate B adds a generic clarification to the instruction: names/introduces/divides are alternatives;
local titles at embedded-document/appendix boundaries are distinct from accompanying descriptive metadata;
membership is independent of hierarchy. No source aliases, document names or Gold labels enter the prompt.
Keep source-bound V1 system/schema/parser, model/provider, facts, reasoning and output settings unchanged.
Keep all production defaults unchanged; candidate PdfClarifyMembership is opt-in.

First test the actual selector collector with deterministic replies, complete scope ownership and forced
out-of-order completion at parallelism 1/6/6. Require identical request hashes, coverage and every final
decision. Run regression checks before live calls.

Live study: four fixed scopes: PDF072 first page, the misbound heading page, the agenda-annex page, and
PDF076 first page as an independent document not used in this prompt audit. Use A-serial, B-parallel,
B-serial, A-parallel order. Four jobs are available, so configured parallelism=6 means at most four
concurrent live jobs. Each request owns the same targets, with complete per-job tools and budgets.
Only the instruction and its hash may differ between A/B request bodies. This is exploratory, not a
statistical causal estimate. Capture all replies and repairs; do not selectively retry failed outcomes.

Score only after collecting results, using hash-checked registered DOC-0252 and DOC-0256 Gold. Report
all false positives/negatives and COMPLETE target coverage for every phase. To advance B to full extraction,
require exact membership in both B runs on all three development scopes and no membership regression
versus A on the independent PDF076 scope. If the gate fails, preserve it and diagnose the remaining
mechanism; do not rerun unchanged B until it passes.

If the gate passes, run two predeclared full PDF072 samples with B, source binding and focused parent
repair enabled. Inline grouping and whole-document placement stay disabled. Require zero missed Gold
aliases, no precision or exact-group regression versus the previous best source-bound run, at most five
unresolved parents, zero residual parent-chain conflicts, no incomplete/fallback scopes, and each sample
under 300 seconds. Both samples count. Broader production promotion additionally requires independent
full-document evidence; Gold has no hierarchy accuracy capability. Do not change Gold.
