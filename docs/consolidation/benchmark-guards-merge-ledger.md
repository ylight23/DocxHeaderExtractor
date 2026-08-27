# benchmark-guards merge ledger

Source: `infra/benchmark-run-guards` @ `030a24f`

| Responsibility | Classification | Disposition |
|---|---|---|
| benchmark manifest/profile assertion, exclusive lock, canonical-output guard | SHARED_EVAL | IMPORT if absent; preserve tested behavior |
| guard tests | TEST | IMPORT with implementation |
| unrelated files | OTHER | REJECT |

The commit is already reachable from canonical HEAD, so no new import is required.
