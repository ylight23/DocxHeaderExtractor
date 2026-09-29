# V5 baseline-01 response-shape audit

Status: `TRANSPORT_PASS_SEMANTIC_WIRE_CONTRACT_FAIL`

Provider calls in this phase: `0`. Gold read: `false`. Only the 31 immutable response artifacts were inspected; no response was repaired.

## Classification

| category | responses |
|---|---:|
| SCHEMA_NAMING_MISMATCH | 0 |
| GROUNDING_MISSING | 30 |
| TRUNCATED | 1 |
| JSON_SHAPE_FAILURE | 0 |
| TASK_VOCABULARY_FAILURE | 0 |
| SEMANTICALLY_UNASSESSABLE | 0 |

## Observed protocol

- Valid JSON: `30/31`; claims-like array: `30/31`.
- Missing `claimId`: `30/31` responses (`496` items).
- Missing `subject`: `30/31` responses (`483` items).
- Any `sourceAlias`: `0/31`; any `sourceParts`: `0/31`.
- Valid task predicate on every item: `25/31` responses (`461/461` predicate-bearing items).
- Mechanically recoverable without inventing grounding: `0/31`.
- Finish reasons: `stop=30, length=1`.

The dominant returned shape was a `claims` array whose items contained `predicate` and `value`: JSON mode was satisfied, but the V5 grounding contract was not. `claimId` is therefore classified as harness-owned; no prompt or response repair was performed.
