# R4-6R Comparator

`Compare-R4Behavior.ps1` is an external, fail-closed comparator. It does not
load production assemblies and it does not change production behavior.

Run the exporter separately in three clean worktrees:

```text
C:\r4-compare-diagnostic  @ 3b350543d3f6b88d074553915169d84587fecf00
C:\r4-compare-pdf         @ a920b2adfc0a7e6caa8eea3c2d93fb63067b530c
C:\r4-compare-current     @ 9b8a91bd75966d86eecbf32648c572d3ff0d57da
```

Each exporter must write one `<id>.json` file per enabled corpus item into a
revision-specific snapshot directory. Snapshot files must contain the
observable fields described by `r4-behavior-corpus.v1.json` and must include:

```json
{
  "documentId": "...",
  "providerCalls": 0,
  "networkEnabled": false,
  "liveLlm": false,
  "liveVlm": false
}
```

The comparator preserves array order, treats null and empty as different,
sorts object keys for comparison, and rounds floating-point values to nine
decimal places. It compares only the explicit observable projection and stops
at the first stage that differs.

Example diagnostic comparison:

```powershell
.\tools\r4\Compare-R4Behavior.ps1 `
  -Corpus .\eval\reconciliation\r4-behavior-corpus.v1.json `
  -BaselineRevision 3b350543d3f6b88d074553915169d84587fecf00 `
  -CurrentRevision 9b8a91bd75966d86eecbf32648c572d3ff0d57da `
  -BaselineSnapshots C:\r4-snapshots\diagnostic-baseline `
  -CurrentSnapshots C:\r4-snapshots\current `
  -Mode diagnostic `
  -Output .\eval\reconciliation\r4-diagnostic-comparison.v1.json
```

Exit codes are `0` for PASS, `2` for an unclassified delta, and a terminating
error for missing files, hash mismatch, missing snapshots, or live/provider
activity. A delta must be adjudicated before the R4-6 gate can be closed.
