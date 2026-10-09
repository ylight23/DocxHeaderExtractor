# P7-F1Q tool-augmented F1 experiment (Issue #5)

Qualification-only, F1-only. Qwen `qwen/qwen3.7-flash` via OpenRouter, Alibaba route, no fallback.
No production, G2/H2, candidate or Gold coupling. Gold is opened only by `score`, after the raw
capture is frozen and pushed.

Arms per case (same issued O# universe, source text, correspondences and context as Control):

- `Control`: the frozen production F1 prompt/body. D01–D05 reuse the historical D3 accepted
  raw (`p7.d3.authorized-f1-raw.v1`); SRC-089/SRC-095 packs get a fresh Control call.
- `F1QNoTools`: the independent P7-F1Q-V1 question, no tools (isolates the question effect).
- `F1QTools`: the same question plus four read-only evidence tools, `tool_choice=auto`,
  at most 3 tool rounds and then one forced-answer turn with `tool_choice=none`.

Tools (`P7F1QEvidenceTools`): `get_occurrence_context`, `get_page_geometry`, `get_source_span`,
`get_repeated_occurrences`. Strict typed/bounded arguments, targets limited to issued O# or the
document's own source aliases, deterministic JSON with provenance hashes, 16 KiB result cap. They
return parser observations only — never labels, Gold or previous predictions.

Response contract `P7_F1Q_V1`: one decision per issued O# with `assessment`
(`SUPPORTED` | `INSUFFICIENT_EVIDENCE`), `function` (3 labels, or null when abstaining),
`observedRole`, `evidenceRefs` (must resolve to supplied aliases or returned tool evidence),
`interpretation`, `missingEvidence`. Invalid responses are preserved and never repaired;
abstentions are never scored as OTHER.

```
dotnet build scripts/P7F1Q -c Release -o <bin>
dotnet <bin>/dhx-v5-qualify.dll probe
dotnet <bin>/dhx-v5-qualify.dll prepare <frozen-request-dir> <full-source-dir> <new-plan.json> <new-bodies-dir>
# commit + push the plan before any provider call
dotnet <bin>/dhx-v5-qualify.dll execute <plan.json> <plan-sha256> <bodies-dir> <frozen-request-dir> <full-source-dir> <capture-root> [handle,...]
dotnet <bin>/dhx-v5-qualify.dll freeze <plan.json> <capture-root> <new-freeze-receipt.json>
# commit + push raw before scoring
dotnet <bin>/dhx-v5-qualify.dll score ...
```

Retry policy: one identical-body retry per model turn on transport failure only. Stops: three
consecutive transport-failed requests, a request costing more than USD 0.50, or cumulative
reported cost above USD 5 (runaway guard; the user approved exceeding USD 2 for this cohort).
The API key is read from `OPENROUTER_API_KEY` and is never written; headers are not recorded.
