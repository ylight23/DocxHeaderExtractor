# P7-D2.1: token budget and context integrity

Status: **PARTIAL — context integrity verified; exact tokenizer budget blocked**.
No provider requests are authorized. D2.2 expanded references and D2.3 cohort/scoring
freeze are not marked complete or automatically started.

## Exact counting prerequisites

The request uses `qwen/qwen3.7-flash`, Alibaba provider, reasoning enabled,
JSON-object response and 32,768 completion tokens. Production is not changed.
The locally retained endpoint metadata lists tokenizer family `Qwen` and null
`instruct_type`; it does not identify exact tokenizer vocabulary/merge revision,
special tokens, chat template or generation/thinking prefix.
The OpenRouter Models documentation describes that field as a tokenization method,
not a verifiable revision. A generic Qwen/Qwen3/Qwen3.5 tokenizer is therefore not
accepted as an exact substitute for this endpoint.

Sources checked read-only on 2026-10-09:

- [OpenRouter model metadata specification](https://openrouter.ai/docs/guides/overview/models)
- [Current endpoint metadata](https://openrouter.ai/api/v1/models/qwen/qwen3.7-flash/endpoints)
- [Alibaba model information](https://www.alibabacloud.com/help/en/model-studio/qwen3-7-flash)

The official sources consulted did not establish a pinned local tokenizer artifact
and chat-template mapping to this endpoint. This is an availability finding from this
audit, **not a claim that no such artifact could exist**.
No source text was sent to a remote token-counting service; no model inference was run.

To close the gate require:

1. Authoritative mapping from the deployed model revision to tokenizer files and
   chat template, including reasoning/JSON mode treatment and special tokens.
2. Local immutable files/revision hashes and tokenizer runtime version.
3. Fully versioned system/user bodies for every treatment, including context
   instructions, reference contract and semantic decision contract.
4. Full chat-template encoding, not `system token count + user token count`:
   concatenation, special tokens and framing can affect the total.
5. Per request: total input tokens, added tokens versus its frozen control, output
   ceiling, provider input cap and shared context-window remaining budget.

Provider billing/usage remains a separate post-capture observation. Even an exact
local tokenizer count does not retroactively establish billing-token parity.

## What is measured now

The page-context preflight already pins all 8 control and 8 B provider bodies and
24 context candidates. It reports exact context and draft user-envelope UTF-8
bytes, Unicode scalar counts and hashes. These are **not token counts**.
The draft user envelopes do not yet have a frozen page-context system instruction
or expanded-reference response verifier. They cannot be treated as complete
model inputs for a qualified budget calculation.

Maximum measured context: 140,192 UTF-8 bytes. Maximum draft additive user envelope:
333,322 UTF-8 bytes. Both pass byte caps, not model-token gates.

## Decision-universe integrity

`P7PageContextDecisionUniverseTests` uses existing stage response parsers with the
original issued request. It checks that page context includes the prefix and a
next-page source, then exercises forged model output:

- H2-C prefix/adjacent source as heading member: rejected.
- H2-C prefix/adjacent source as endpoint: rejected.
- H2-C prefix/adjacent source as first outside: rejected.
- Valid issued extent and terminal null successor: accepted, even with adjacent
  context available. The visible contextual successor does not override the
  contract's `NO_VISIBLE_SUCCESSOR` for the issued tail.
- F1 cannot replace an issued occurrence with an out-of-pack context source.
- G2A cannot admit a context-only neighbor as an issued primary.

This proves existing parser boundaries, not future-treatment parser parity. The
expanded-reference verifier must continue delegating semantic stageDecision to
the **original issued request**, never derive its decision universe from context.
Evidence references may eventually expand; heading members/endpoint/successor may not.

Next input needed: verified model-specific tokenizer/chat-template artifacts (or an
authoritative documented equivalence). Until then, exact token count and added-token
delta remain null and the provider gate fails closed. No mass rewrite, proxy count,
request rebaseline, Gold change or production promotion is justified by this blocker.
