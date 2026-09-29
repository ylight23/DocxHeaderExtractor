# Request Architecture V2

Status: provider-free executable preflight. V4 production remains unchanged; no provider request was made, no Gold was read, and no frozen V4/R1 artifact was modified.

## Objective and non-changes

V2 changes request partitioning and prepares a deterministic transport envelope. It does not change the semantic-function ontology, occurrence membership, binder, hierarchy semantics, source facts, Gold, model, reasoning effort, or production default. The model still decides meaning; the harness still decides coordinates.

## Packing contract

`RESOURCE_BOUNDED_SOURCE_PACKING_V1` is an explicit experimental policy. It walks source atoms in physical source order and packs verbatim source facts under deterministic ceilings: 120,000 serialized source-input bytes, 20,000 estimated input tokens, 128 visible atoms, 96 owned atoms, 12 atoms of left/right halo, and 12,288 reserved completion tokens. The fixed 120-owned-item default is untouched.

Each pack has `LEFT_HALO | OWNED_CORE | RIGHT_HALO`. Halos are visible raw context only; every classifiable source atom belongs to exactly one owned core. The harness rejects a model decision for a visible-but-not-owned atom as `OUT_OF_OWNED_SEGMENT`; it never silently relocates it. Source order, paragraph/table/page/row facts may determine boundaries, but no semantic score, heading rule, Gold, or post-filter participates.

The provider-free simulator proves exact ownership, complete coverage, verbatim source preservation, and reports duplication. It does not claim output equivalence or accuracy; that needs a separately authorized provider run.

The follow-up sweep varies serialized-byte ceiling, estimated-token ceiling, owned-core ceiling, and halo atoms independently. It freezes the resource-unit definitions and computes a Pareto frontier over call count, total input, tail size, repeated context, and context-to-owned ratio. P05, P07, and P06 are retained as SMALL, BALANCED, and LARGE review points (31/28/27 calls, all in the moderate primary-pack band); P03's 87-call shape remains a pathological-small-pack reference, not the V2 treatment. None is promoted to a provider treatment automatically.

Each sweep row also records a workload proxy as separate observables—calls, total input bytes, and repeated fixed-overhead bytes. It is a planning proxy only; no latency causality is inferred.

## Canonical serialization and cache readiness

The planned wire ordering is stable semantic contract and normative definitions first, then stable document/pack metadata, raw visible context, owned workset, and a task suffix. Field and array order are fixed; text is LF-normalized; number formatting is invariant; timestamps and volatile IDs are excluded. SHA-256 is recorded independently for `semanticRequest`, `providerEnvelope`, `sourceVisibleSet`, and `ownedSet`.

`session_id = SHA256(sourceHash + model + requestVersion + semanticContractVersion)` is transport metadata, never evidence. Explicit cache support for the exact OpenRouter/Alibaba route is `UNKNOWN`, so V2 sends no `cache_control`. Endpoint metadata reports implicit caching only. Cache readiness therefore cannot alter the semantic bytes the model sees.

`response_format: json_schema` with `strict: true` is also `UNKNOWN` for the exact route. The preflight retains the existing JSON-object variant; an equivalent closed schema (`headings[].sourceParts`, `semanticFunction`, `additionalProperties: false`) is only an experimental wire option after capability proof. The envelope invariants require `lossyContextTransformEnabled = false`; no transform may truncate, summarize, compress, drop, or rewrite visible source text.

## Execution, resume, and failure isolation

Each planned request has states `PLANNED`, `ATTEMPTED`, `SUCCESS`, `FAILED_RETRYABLE`, and `FAILED_FINAL`. Semantic identity hashes normalized system and user semantic bytes; provider-payload identity is deliberately separate. A local content-addressed store can reuse only a complete, validated success with the same semantic hash and provider semantic-execution fingerprint, reported as `LOCAL_FROZEN_RESPONSE_REUSE`.

For retryable transport failure, only the failed leaf splits at a deterministic source-order midpoint. Children inherit context halos, repartition the parent owned set with empty intersection, and retain parent hash, child hashes, reason, and attempt lineage. Successful leaves are never resent. This supports recovery from immutable telemetry rather than ordinal-specific repair code.

Timeouts are conceptualized separately as connect, first-byte, and total-request limits. The historical architecture preflight records a generic 180-second proposal, but it is not the V4R2 P05 pin: the provider-candidate preflight fixes total-request timeout at 300 seconds. Connect/first-byte limits must be wired only where the transport exposes them. Observed successful latency distribution, rather than document identity, governs later calibration. A timeout does not reset a campaign budget.

## Provider gate and proposed V4R2 experiment

No provider call is authorized by this implementation. The next experiment is `V4R2_REQUEST_ARCHITECTURE_V2`: pin `qwen/qwen3.7-flash`, reasoning effort, V4 semantic contract, schema semantics, source universe, binder, and Alibaba route; alter only `FIXED_OWNED_COUNT_120` to `RESOURCE_BOUNDED_SOURCE_PACKING_V1`. There is no automatic semantic-model fallback.

The selected candidate freeze is P05, recorded separately in `v4r2-provider-preflight.v1.json`: 90,000 serialized-input bytes, 28,000 estimated input tokens, 96 owned atoms, 128 visible atoms, 8 halo atoms, 12,288 reserved completion tokens, and a 300-second total-request timeout. It plans 31 primary calls (SRC-089: 7; SRC-095: 24). This artifact is a provider-free plan and does not mutate the neutral sweep/Pareto evidence.

Before it can run, an explicit provider authorization must approve the frozen planned workload, hard completion ceiling of 12,288 tokens per request, 300-second timeout pin, bounded recovery budget, and metadata-capability posture. It must persist raw predictions before opening Gold and measure execution (attempts, completion, retries, latency, tokens, cache, cost) separately from semantic quality (P/R/F1 and all existing held-out slices).

The current sweep does not yet select a provider candidate. P06 lowers tail and total serialized input relative to the 25-call baseline but increases calls; it remains a review point pending Release validation and explicit authorization. The next engineering decision is therefore between the selected Pareto points plus adaptive splitting, not a blind rollout of the 87-call shape.

## Direct answers

1. Fixed 120 is inadequate as a cost model: R1 shows incomplete execution with widely variable source payloads; it is not an accuracy verdict.
2. Serialized input size, visible/owned atoms, halo duplication, and reserved completion capacity are observable predictors to test; this preflight does not assert causality.
3. The unit is a source-order, resource-bounded owned core plus raw context halos.
4. The simulator records exact repeated-context bytes; it does not remove source text.
5. Every atom is serialized verbatim in its owned core and checked for exact-once ownership.
6. Adjacent raw left/right halos preserve boundary context without ownership duplication.
7. Only failed leaves split; validated successful content-addressed responses are reused.
8. Explicit cache control is `UNKNOWN`; no cache-control parameter is sent.
9. Strict structured output is `UNKNOWN`; current JSON-object output remains.
10. Ontology, binder, facts, source universe, model, reasoning, and production default remain unchanged.
11. The next provider experiment is the pinned, one-variable V4R2 arm described above, after explicit authorization.
