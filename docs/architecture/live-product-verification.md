# Live Product Verification

Status: `VERIFICATION_COMPLETE`

This record covers the isolated live-product smoke run on branch
`verification/live-product-smoke`.

Baseline SHA: `2a4ba9276dc6c2666f7787992c2bf1ac37c0056e`

## Scope and safety

- Only the synthetic DOCX fixture `samples/mau.docx` was used. Its SHA-256 was
  `AA4556D7946FD391EF8FA2D0B7BA9308D9CEACE2B94B9726954B59829543394D`.
- No provider credential, authorization header, raw external request, or raw
  NDJSON trace is committed.
- The product host was run from this worktree on isolated port `5109`.
- The branch was not merged to `main`.

## Live provider runs

OpenRouter was available through `OPENROUTER_API_KEY` (presence only was
checked); `/v1/models` returned HTTP 200 and listed `qwen/qwen3.5-9b`.

Both supported prompts were sent through Web `/api/extract` with
`backend=openrouter`, `noLlm=0`, `showRaw=1`, and the synthetic fixture.

| Run | Live result |
| --- | --- |
| Prompt 1: structure as a tree | HTTP 200; `Completed`; 25 timestamped agent events; 6 provider requests and 6 responses; 6 headings with source IDs and spans; plan `plan-74ac960182939eb2` |
| Prompt 2: structure to two levels and return a tree | HTTP 200; `Completed`; 25 timestamped agent events; 6 provider requests and 6 responses; 6 headings with source IDs and spans; `IntentProposal`, `ValidatedIntent`, and `SemanticTaskPlan` all report `depth=2`; plan `plan-e2eadbef278f7390` |

The two plan IDs differ. Raw provider exchange logging is opt-in and the
captured log scan found no `Bearer`, `OPENROUTER_API_KEY`, or test key value.

## Intent, policy, and failure behavior

- Unsupported prompt (`Translate the document into English.`): blocked at
  `intent.validation` with `IntentState=Unsupported`, no provider request, no
  result.
- Incomplete prompt (`Please inspect this file.`): blocked with
  `IntentState=NeedsClarification`, no provider request, no result.
- Invalid depth prompt (`...to -1 levels...`): blocked with
  `IntentState=Rejected`, no provider request, no result.
- Writeback request: policy trace reported `human-review-before-mutation`, the
  action stage was ordered after validation/gate, a download was produced, and
  the source fixture SHA remained unchanged. The run reported
  `writebackApplied=0` for this fixture.
- LM Studio unavailable case: produced `provider-failure` and an agent
  `Failed` event, with no fabricated result.
- Cancellation: client cancellation returned curl exit 28; the client received
  17 partial agent events, no result, and one raw request marker. Runtime
  telemetry recorded `Cancelled`.

## Regression gates

- Release solution build: PASS, 0 errors.
- Focused Web/automatic-harness/provider/authority matrix: PASS, 105/105.
- Full test suite: PASS, 943/943, 0 skipped, duration 2m15s.
- `scripts/source-tree-hygiene-gate.ps1`: PASS.
- `git diff --check`: PASS.

Product UI cutover and live-provider verification are therefore PASS on this
branch, pending review before any merge to `main`.
