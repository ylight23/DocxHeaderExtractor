# n3-audit merge ledger

Source: `n3-audit-bootstrap` @ `21d8683`

| Responsibility | Classification | Disposition |
|---|---|---|
| fresh holdout manifest and source-first packets | FROZEN_ARTIFACT / SHARED_EVAL | IMPORT as frozen evidence if absent; never label or alter during consolidation |
| bootstrap probe | TEST / DIAGNOSTIC | IMPORT only if required by the retained artifact contract |

Do not expose candidate/rank/model data in source-first packets.
