# P7 offline budget audit: separate mapping and measurement gates

This checkpoint supersedes the earlier **sequencing** restriction that D2.2/D2.3 had
to wait for an exact tokenizer. The older D2.1 receipt remains immutable historical
evidence. D2.2 reference validation and D2.3 cohort/scoring gates can progress offline.

Two independent gates remain not established:

- `EXACT_TOKENIZER_MAPPING`: authoritative endpoint ↔ tokenizer revision/chat template.
- `TOKEN_BUDGET_MEASUREMENT`: per-request tokens from a controlled actual endpoint or
  verified equivalent official counter. Actual endpoint usage would not itself identify
  tokenizer revision. Opening a usage-calibration experiment needs its own authorization,
  finite request budget, costs and freeze; it is not authorized by this audit.

## Measurements

The executable takes repository root, private frozen control root, private B V2 root,
private page-context root, captured metadata snapshot and a **new** sanitized output.
It never fetches metadata or calls inference itself. The snapshot was obtained by a
separate public GET, with time/hash pinned in the metadata receipt; no PDF text was sent.

For 8 controls, 8 B requests and 24 context-added drafts (40 rows), it reports:

- Actual UTF-8 serialized provider-body bytes, including JSON escaping/envelope.
- Decoded system/user message bytes separately.
- Disjoint system semantic-stage prefix and interpretation/reference/assertion/output
  instruction suffix, summing to the complete existing B system prompt.
- Existing user stageInput/sourceEvidence JSON component sizes and page-context bytes.
- Signed byte deltas versus each frozen Control and B.
- All original body/context hashes and a deterministic draft body hash.

All eight B bodies must be reproduced byte-identically by the production provider
composer. All control and B body hashes, 24 context hashes, and the preceding draft
user-envelope hashes must match their frozen manifests.
Draft system prompt is still the B prompt: the extra context instructions/expanded
reference contract have not been frozen. Draft size is **not** a qualified complete
treatment input budget. References/assertions size here means the instructions that
require them, not future model-output references, which do not exist before capture.

Bytes are neither token counts nor claimed token upper bounds. No proxy tokenizer is
used. If added later, its results must carry `PROXY_ESTIMATE_ONLY` and cannot pass either
gate without independent equivalence evidence. Provider usage/billing belongs to a
separate post-capture lane, not a reconstructed offline estimate.

## Redundancy is visible, not an implicit accuracy claim

The additive drafts preserve B's sourceEvidence and copy page-context observations.
Some facts are therefore repeated. F1 already sees all 79 sources: local/page/adjacent
context adds no new source aliases on this fixture, but doubles some evidence text.
G2A page context does add 60 previously unseen aliases. These treatments have different
information gains and duplication costs; byte growth alone is not new-information gain.
Do not choose an arm merely because it is larger or call this a geometry-only causal test.

## Metadata drift

`p7.openrouter-endpoint-metadata.20261009.v1.json` retains the decoded public response.
The receipt pins GET time, URL, status, byte hash and a separate configuration projection.
The full snapshot differs from historical metadata; the model/family/provider endpoint
name/context/input/output caps/supported-parameter projection is equal. Volatile uptime
can change raw hashes without changing those fields. Neither equality proves hidden
weights or tokenizer revision remain identical.

Provider inference, production authority, Gold mutation and frozen request rebaseline
remain forbidden. Exact-budget readiness and provider calibration remain OPEN while
the offline validator/cohort work proceeds independently.
