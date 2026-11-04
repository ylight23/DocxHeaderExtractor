# Cache and wall-time A/B design — 2026-10-27

Status: design only. No provider call has been made. Runs need OpenRouter credit and the owner's permission for Qwen calls.

## Question

Does the current cache layout produce cache hits, and what does it cost or save in wall time on the PDF SELECT phase?

## What is already known

- Every request body marks two text blocks with `cache_control: ephemeral` (`OpenRouterWire.cs`): the system prompt and the user payload.
- The user payload is the whole packet: `instruction`, `targetUnits`, `context`, and `pageImages`. Only `instruction` is static across units, so the marked user block differs for every unit.
- The 26 tracked responses all report `cached_tokens: 0`. None reports `cache_creation_input_tokens`.
- The usage audit now records `CacheCreationTokens` and each provider post's duration (`AgentModel.PostDurationsMs`).

## Arms

Same document, same provider lock (`Alibaba`), same commit, same model, `temperature` 0.

| Arm | Layout | Purpose |
|---|---|---|
| A (baseline) | Current markers, 8 SELECT requests in parallel | Reference |
| B (warm-up) | One SELECT first, then the other 7 in parallel | Tests whether cache hits appear once the first response has finished |
| C (marker placement) | Marker only on the system block; the user packet has no marker | Tests whether removing the marker from the per-unit packet changes cost; the text is unchanged |

Arm C changes only where the markers sit. It does not change the instruction text or the packet content, so it is not a prompt change.

Documents: 072 (15 pages) and 089 (18 pages), which are small enough to repeat. Three repetitions per arm.

## Measurements

Per run:
- `cached_tokens`, `cache_creation_input_tokens`, prompt, completion, and reasoning tokens (from `Usage.Audit` and the per-model totals);
- provider cost (`usage.cost`);
- `PostDurationsMs` P50 and P95;
- wall time of the SELECT phase, from the first post to the last reply;
- post count, `ToolCallsEmitted`, and `ToolCallsSucceeded` (from `ToolConversation`);
- selection quality against Gold, read only after the run finishes: alias TP/FP/FN and exact groups.

## Decision rule

Keep a cache layout only if all three hold:
1. `cached_tokens` is above zero on the arm after the first request, so hits are real;
2. wall time of the SELECT phase is not worse by more than 5% against the baseline, measured on critical path, not on total tokens;
3. alias FP does not increase and exact groups do not decrease.

If cache creation is billed (`cache_creation_input_tokens` > 0) and no hits follow, the marker on the per-unit packet is removed (arm C) as a cost fix, with the same quality check.

## Preconditions

- OpenRouter credit, and the owner's permission to make Qwen calls.
- Gold stays frozen. It is read only after each run has finished.
- The spend cap is set before the first run.
