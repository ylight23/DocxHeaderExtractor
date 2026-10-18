# Qualification D: grounded REGROUP contract and consistency repair

Run explicitly with:

```text
boundary-grounded-d <ab-manifest.json> <seed.json> <frozen.json> <new-output-directory> --live
```

The command shares C's frozen-source loader and checks the source document, alias universe, evidence store, original verdict, rejected groups and static packet against the A/B snapshots. Cached transcription is mandatory; new preflight HTTP is forbidden. It does not resample selection, grouping or the historical C0 verdict. As with C, D starts a fresh verifier conversation supplied with the frozen packet and full historical complaint; it does not claim to continue the original conversation. Tools/crops are not exposed in this qualification conversation.

The qualification schema is `QUALIFICATION_GROUNDED_REGROUP_V1`. It retains the production verdict's run, decision, reason, evidence references and exact ordered per-alias membership. It adds two required fields:

| Field | Constraint |
|---|---|
| `currentGroups` | Exactly equals the input `proposedGroups`, including ordered group boundaries, for every decision |
| `suggestedGroups` | For REGROUP: a valid different partition; otherwise null |

C# reuses the production verdict validator and strict grouping parser. Partitions must cover every candidate alias once in source order, with contiguous nonempty groups of at most eight lines. Comparing only alias sets would be insufficient: `[[A,B],[C]]` differs from `[[A],[B,C]]`. No reason text is parsed or interpreted.

A valid but different current partition fails with `BOUNDARY_CURRENT_GROUPS_MISMATCH`. A REGROUP whose valid suggestion equals the actual input fails with `BOUNDARY_VERDICT_SELF_CONTRADICTORY`. Malformed JSON, membership/coverage violations and other schema faults fail with `GROUNDED_VERDICT_PROTOCOL_INVALID`.

These failures append the actual assistant response and a deterministic user protocol-consistency repair to the same conversation. That repair includes the error code and exact rejected input partition; it asks for a faithful current graph plus ACCEPT, a different valid proposal, or dispute/unresolved as appropriate. There are at most three protocol POST rounds, independent of production's semantic boundary-cycle budget. Persistent failures remain unresolved. Incomplete/non-stop output, tool calls and oversized responses stop immediately without salvaging a verdict. Cancellation or exhausted budget prevents another POST.

A grounded ACCEPT qualifies the current partition in this experiment. A valid changed REGROUP proposal enters a fresh production verifier conversation, supplied with source evidence and selected reading/extent maps but no historical complaint, proposing reason or consistency-repair history. Only its ACCEPT qualifies that changed partition. Other decisions remain unresolved; no semantic repair recursion is invoked. Even qualified acceptance emits no production headings or writeback, and is not Gold correctness.

Safe observations retain round number, finish reason, response byte count, validated decision, validated alias-only partitions and error code. Invalid model-supplied partitions are excluded from safe observations, so arbitrary strings cannot leak through malformed alias fields. Full packets, reasons, completions and independent verifier checks are saved only in the separate local private directory. Shared experiment admission is capped at 12 POSTs and USD 1.

Production's verifier prompt/schema, grouper, no-progress guard, two-cycle limit, hierarchy and resolver are unchanged. This is a qualification test of a missing boundary consistency invariant; it does not establish that every historical boundary failure has the same cause. Structured boundary edit operations are not added: an exact echoed graph and a validated distinct target partition suffice to test this invariant first.
