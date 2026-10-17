# Evidence-based hierarchy placement

Selected-line scan, text-PDF and DOCX authorities now use
`HEADING_HIERARCHY_EVIDENCE_V1`. Other authorities without a source packet keep their existing
placement contract. No heading words, document IDs, article numbers or expected levels are
hardcoded in production. The test fixture is synthetic, not Gold or a live 023 result.

## Source and upstream losses

Placement receives the complete bound heading outline with source ordinals and part aliases,
measured styles of current targets, two source lines on either side of each target, source locations when available,
and the source SHA-256. Source lines withheld upstream are also visible with their original
selection/verification/binding states. These lines are not asserted to be ancestors. Only the
model judges their structural function. A selected line that failed binding is distinguished
from a line already unresolved at selection or verification.

Full styles are not duplicated into every outline row and every upstream marker. Read-only
`get_source_context(alias,before,after)` and `get_source_range(first,last)` operate
on the same immutable source snapshot and return full style measurements for requested lines.
`search_source_text(query,offset,limit)` is a bounded, case-insensitive literal search across
all source lines, including lines explicitly marked `NOT_SELECT`; it returns at most 12 matching
lines per request and supports pagination. Search results are source evidence only. They do not
restore excluded lines as headings or change membership.
Each request returns at most 24 source lines; at most
four tools per response round. Successful returned aliases extend the evidence-reference
catalog. Missing/invalid tool arguments do not manufacture source evidence. This is a text and
location surface, not a new image/VLM call.

## Proposed relations are not published directly

1. The placement model proposes parent aliases, `ROOT`, `NONE` or `UNRESOLVED`.
2. A fresh independent conversation sees the complete proposed graph and source evidence,
   without the proposal model's reasoning. It returns per-edge `ACCEPT`, `REJECT`,
   `MISSING_ANCESTOR` or `UNRESOLVED`, with grounded evidence references. An acceptance cites
   the child and its parent (or the child for ROOT/NONE). A missing ancestor names precise
   unbound source aliases; C# validates references, not whether the text means "chapter".
3. Only rejected edges enter one targeted parent-repair pass. Newly proposed edges and their
   dependent descendant chains receive fresh verification. There is no recursive repair or
   rerun of OCR, transcription, selection or grouping.
4. Only independently accepted relations reach hierarchy derivation. Headings remain emitted
   when ancestry is unknown. An unresolved parent chain supplies neither a parent relation nor
   an invented depth to descendants.

The verifier can still make semantic mistakes. These contracts establish evidence and
independent checks, not a claim of 100% accuracy. Upstream heading membership is not repaired
by placement, and an unavailable ancestor is never automatically reinstated.

## Bounded work and latency

Each phase batches at most 16 headings and executes at most four batches concurrently. A
normal batch needs one proposal and one verification call. Only rejected edges and descendants
affected by an ancestry change receive further calls. Each phase has at most three response
rounds shared by source requests and protocol repairs. Valid rows are retained independently;
only pending rows are requested again. Transport retry policy remains owned by the authorized
transport, so there is no second nested transport retry loop here.

Requests are capped at 96 KiB and responses at 16 KiB. These are protocol guards, not a USD
budget; provider authorization, retry policy and telemetry remain the transport's responsibility.
The new independent verification adds work compared with the old unverified placement pass.
Bounded parallelism reduces serialization; no end-to-end speedup is claimed without a live run.
The concurrency regression uses 40 synthetic headings, proves overlapping batches under the
four-worker cap, preserves source order and checks six normal logical calls (3 proposal +
3 verification). It does not measure provider latency or document accuracy.

## Same-execution audit

`PlacementExecution` reports the contract, logical call count, wall seconds, verified count and
repair count. The safe Web audit shows each edge's proposed parent, verifier status, resolution
basis, blocking aliases and evidence references. Its source-state table includes every explicit
upstream selection answer, including `NOT_SELECT`, and can be filtered by alias or status.
Logical calls exclude internal transport retries. Scan provenance now records
these logical calls instead of assuming placement always costs one call. The Web displays
these observations and the `hierarchy` timing; that duration is included in scan `structure`.
Text-PDF and DOCX provenance use selector-recorded calls plus hierarchy logical calls, rather
than counting diagnostic strings as if each string were a provider call. This remains a producer
count, not an independent count of transport-internal retries.
Exhausted protocol errors, request-cap refusals and transport exception classes are also listed
as safe failure codes; source text, exception messages and raw replies are excluded from these codes.

Each relation records the proposed parent, verification status, evidence references, precise
blocking aliases and resolution basis. `SourceTrace` joins upstream decisions to actual binding
and final placement resolution. This allows tracing, for example, `P015-L010`, `P015-L012`,
`P021-L010` and `P021-L012` without guessing from missing outline rows. These aliases are test
and diagnostic examples, not production rules.

Full request/response evidence, logical request sizes and hashes join the existing raw model audit.
The logical hash covers prompt + packet, not the transport's final HTTP body; transport telemetry
owns the latter. Web safe
audit excludes these payloads and shows only identities, statuses and observations. A pasted
outline cannot substitute for the actual execution's source trace and placement responses.
No claim is made about the loss stage of the user's previous 023 execution: those raw responses
have not been supplied or recovered in this workspace.

The Web distinguishes explicit source-line answers from final semantic-unit completeness. The
older `UnitsComplete` count includes downstream grouping/review outcomes; it must not be read as
the number of raw selection answers. A hierarchy-only replay needs the exact same-execution bound
headings, source snapshot, upstream states and placement evidence. The 023 hierarchy replay was
not run here because the prior 61-heading execution snapshot is not present in the workspace;
re-running extraction would also rerun selection and would not isolate hierarchy.
