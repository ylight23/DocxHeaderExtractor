# accuracy-r1 merge ledger

Source: `accuracy/round2-ranking` @ `81e677f`

| Responsibility | Classification | Disposition |
|---|---|---|
| Round 1/2/3/4/5/6 diagnostic probes and JSON artifacts | DIAGNOSTIC | KEEP_DIAGNOSTIC; import only as an explicit evidence batch if required |
| `Docx004StructuralFirstLossAuditProbe` and report | DIAGNOSTIC | KEEP_DIAGNOSTIC |
| `PdfLayoutEvidenceOutline`, `PdfStageCheckpoint`, `RouteExecutionAudit` changes | SHARED_EVAL | review individually; do not merge whole tree |
| CLI option changes | SHARED_EVAL | compare against canonical and other infra branches |
| Web `index.html` | EXPERIMENTAL / user-dirty | REJECT from consolidation until separately reviewed; preserve local change |
| Round 1 remediation code | EXPERIMENTAL | REJECT production promotion; retain history |

Source worktree has uncommitted `index.html`; no files from it are staged by inventory.
