# V2A.1 Role Prefilter Forensic Recovery Audit

Offline source-backed forensic audit only. V2A production prefilter remains inactive; no provider, Gold, or scoring path was used.

- V7 heading-like occurrences that V2A would skip: **9**
- V7 uncertain occurrences that V2A would skip: **56**
- Grouping basis: generic source-derived evidence signatures; no document-ID or text-literal rule was added.
- V2B status: **BLOCKED_ON_BEHAVIOR_PRESERVATION** until a generic recovery policy is separately designed and audited.

## Heading-like evidence groups
- `{"evidenceSignature":"SKIP_NO_STRUCTURAL_SIGNAL|Normal|not-candidate|appendix|Unknown|no-marker|not-table|no-format-signal|no-numbering-signal|other|medium","count":2,"sourceIds":["body[1]/p[892]","body[1]/p[1003]"]}`
- `{"evidenceSignature":"SKIP_NO_STRUCTURAL_SIGNAL|Normal|not-candidate|appendix|Unknown|no-marker|not-table|no-format-signal|numbering-signal|period|long","count":2,"sourceIds":["body[1]/p[907]","body[1]/p[911]"]}`
- `{"evidenceSignature":"SKIP_NO_STRUCTURAL_SIGNAL|Normal|not-candidate|document_body|Unknown|no-marker|not-table|no-format-signal|no-numbering-signal|other|medium","count":2,"sourceIds":["body[1]/p[519]","body[1]/p[555]"]}`
- `{"evidenceSignature":"SKIP_NO_STRUCTURAL_SIGNAL|Normal|not-candidate|document_body|Unknown|no-marker|not-table|no-format-signal|numbering-signal|other|medium","count":2,"sourceIds":["body[1]/p[557]","body[1]/p[558]"]}`
- `{"evidenceSignature":"SKIP_NO_STRUCTURAL_SIGNAL|Normal|not-candidate|document_body|Unknown|no-marker|not-table|format-signal|numbering-signal|other|long","count":1,"sourceIds":["body[1]/p[638]"]}`

## Uncertain evidence groups
- `{"evidenceSignature":"SKIP_NO_STRUCTURAL_SIGNAL|Normal|not-candidate|appendix_table|TableTitle|no-marker|table|no-format-signal|no-numbering-signal|other|short","count":19,"sourceIds":["body[1]/tbl[29]/tr[10]/tc[2]/p[1]","body[1]/tbl[30]/tr[4]/tc[1]/p[1]","body[1]/tbl[30]/tr[4]/tc[3]/p[1]","body[1]/tbl[30]/tr[4]/tc[4]/p[1]","body[1]/tbl[30]/tr[4]/tc[5]/p[1]","body[1]/tbl[30]/tr[4]/tc[7]/p[1]","body[1]/tbl[30]/tr[4]/tc[9]/p[1]","body[1]/tbl[30]/tr[5]/tc[4]/p[1]","body[1]/tbl[30]/tr[5]/tc[5]/p[1]","body[1]/tbl[30]/tr[5]/tc[7]/p[1]","body[1]/tbl[30]/tr[5]/tc[9]/p[1]","body[1]/tbl[30]/tr[6]/tc[1]/p[1]","body[1]/tbl[30]/tr[8]/tc[1]/p[1]","body[1]/tbl[30]/tr[10]/tc[1]/p[1]","body[1]/tbl[30]/tr[14]/tc[1]/p[1]","body[1]/tbl[30]/tr[14]/tc[4]/p[1]","body[1]/tbl[30]/tr[15]/tc[4]/p[1]","body[1]/tbl[30]/tr[16]/tc[1]/p[1]","body[1]/tbl[30]/tr[18]/tc[1]/p[1]"]}`
- `{"evidenceSignature":"SKIP_NO_STRUCTURAL_SIGNAL|Normal|not-candidate|table|TableTitle|no-marker|table|no-format-signal|no-numbering-signal|other|short","count":13,"sourceIds":["body[1]/tbl[5]/tr[3]/tc[2]/p[1]","body[1]/tbl[6]/tr[4]/tc[4]/p[1]","body[1]/tbl[6]/tr[4]/tc[5]/p[1]","body[1]/tbl[6]/tr[4]/tc[7]/p[1]","body[1]/tbl[6]/tr[4]/tc[9]/p[1]","body[1]/tbl[6]/tr[5]/tc[4]/p[1]","body[1]/tbl[6]/tr[5]/tc[5]/p[1]","body[1]/tbl[6]/tr[5]/tc[7]/p[1]","body[1]/tbl[6]/tr[5]/tc[9]/p[1]","body[1]/tbl[6]/tr[6]/tc[1]/p[1]","body[1]/tbl[6]/tr[8]/tc[1]/p[1]","body[1]/tbl[6]/tr[12]/tc[1]/p[1]","body[1]/tbl[10]/tr[2]/tc[1]/p[1]"]}`
- `{"evidenceSignature":"SKIP_NO_STRUCTURAL_SIGNAL|Normal|not-candidate|table|TableTitle|no-marker|table|format-signal|no-numbering-signal|other|short","count":3,"sourceIds":["body[1]/tbl[5]/tr[2]/tc[10]/p[1]","body[1]/tbl[5]/tr[2]/tc[11]/p[1]","body[1]/tbl[5]/tr[2]/tc[13]/p[1]"]}`
- `{"evidenceSignature":"SKIP_NO_STRUCTURAL_SIGNAL|Normal|not-candidate|appendix_table|TableTitle|no-marker|table|format-signal|no-numbering-signal|period|short","count":2,"sourceIds":["body[1]/tbl[30]/tr[2]/tc[10]/p[1]","body[1]/tbl[30]/tr[2]/tc[12]/p[1]"]}`
- `{"evidenceSignature":"SKIP_NO_STRUCTURAL_SIGNAL|Normal|not-candidate|appendix_table|TableTitle|no-marker|table|no-format-signal|no-numbering-signal|other|medium","count":2,"sourceIds":["body[1]/tbl[19]/tr[13]/tc[2]/p[7]","body[1]/tbl[19]/tr[13]/tc[2]/p[9]"]}`
- `{"evidenceSignature":"SKIP_NO_STRUCTURAL_SIGNAL|Normal|not-candidate|appendix_table|TableTitle|no-marker|table|no-format-signal|no-numbering-signal|semicolon|long","count":2,"sourceIds":["body[1]/tbl[19]/tr[13]/tc[2]/p[8]","body[1]/tbl[19]/tr[13]/tc[2]/p[10]"]}`
- `{"evidenceSignature":"SKIP_NO_STRUCTURAL_SIGNAL|Normal|not-candidate|appendix_table|TableTitle|no-marker|table|no-format-signal|numbering-signal|period|long","count":2,"sourceIds":["body[1]/tbl[19]/tr[14]/tc[2]/p[1]","body[1]/tbl[19]/tr[14]/tc[2]/p[2]"]}`
- `{"evidenceSignature":"SKIP_NO_STRUCTURAL_SIGNAL|Normal|not-candidate|document_body|Unknown|no-marker|not-table|no-format-signal|no-numbering-signal|other|long","count":2,"sourceIds":["body[1]/p[538]","body[1]/p[540]/txbxContent[1]/p[5]"]}`
- `{"evidenceSignature":"SKIP_NO_STRUCTURAL_SIGNAL|Normal|not-candidate|document_body|Unknown|no-marker|not-table|no-format-signal|numbering-signal|period|long","count":2,"sourceIds":["body[1]/p[528]","body[1]/p[530]"]}`
- `{"evidenceSignature":"SKIP_NO_STRUCTURAL_SIGNAL|Normal|not-candidate|document_body|Unknown|no-marker|not-table|no-format-signal|numbering-signal|period|medium","count":2,"sourceIds":["body[1]/p[527]","body[1]/p[529]"]}`
- `{"evidenceSignature":"SKIP_NO_STRUCTURAL_SIGNAL|Normal|not-candidate|appendix_table|TableTitle|no-marker|table|format-signal|no-numbering-signal|other|short","count":1,"sourceIds":["body[1]/tbl[29]/tr[2]/tc[13]/p[1]"]}`
- `{"evidenceSignature":"SKIP_NO_STRUCTURAL_SIGNAL|Normal|not-candidate|appendix_table|TableTitle|no-marker|table|no-format-signal|no-numbering-signal|other|long","count":1,"sourceIds":["body[1]/tbl[19]/tr[13]/tc[2]/p[6]"]}`
- `{"evidenceSignature":"SKIP_NO_STRUCTURAL_SIGNAL|Normal|not-candidate|document_body|Unknown|no-marker|not-table|no-format-signal|no-numbering-signal|other|medium","count":1,"sourceIds":["body[1]/p[535]"]}`
- `{"evidenceSignature":"SKIP_NO_STRUCTURAL_SIGNAL|Normal|not-candidate|document_body|Unknown|no-marker|not-table|no-format-signal|no-numbering-signal|period|long","count":1,"sourceIds":["body[1]/p[540]/txbxContent[1]/p[3]"]}`
- `{"evidenceSignature":"SKIP_NO_STRUCTURAL_SIGNAL|Normal|not-candidate|table|TableTitle|no-marker|table|format-signal|no-numbering-signal|period|short","count":1,"sourceIds":["body[1]/tbl[5]/tr[2]/tc[12]/p[1]"]}`
- `{"evidenceSignature":"SKIP_NO_STRUCTURAL_SIGNAL|Normal|not-candidate|table|TableTitle|no-marker|table|no-format-signal|no-numbering-signal|other|medium","count":1,"sourceIds":["body[1]/tbl[10]/tr[2]/tc[1]/p[3]"]}`
- `{"evidenceSignature":"SKIP_NO_STRUCTURAL_SIGNAL|Normal|not-candidate|table|TableTitle|no-marker|table|no-format-signal|no-numbering-signal|period|long","count":1,"sourceIds":["body[1]/tbl[10]/tr[2]/tc[1]/p[4]"]}`

## Nine-case index
| Source ID | Ordinal | Role | Score | Candidate | Skip reason | V7 predicted role | Evidence signature |
|---|---:|---|---:|---|---|---|---|
| `body[1]/p[519]` | 1483 | Normal | 0 | False | SKIP_NO_STRUCTURAL_SIGNAL | HeadingTopic | `SKIP_NO_STRUCTURAL_SIGNAL|Normal|not-candidate|document_body|Unknown|no-marker|not-table|no-format-signal|no-numbering-signal|other|medium` |
| `body[1]/p[555]` | 1560 | Normal | 0 | False | SKIP_NO_STRUCTURAL_SIGNAL | HeadingTopic | `SKIP_NO_STRUCTURAL_SIGNAL|Normal|not-candidate|document_body|Unknown|no-marker|not-table|no-format-signal|no-numbering-signal|other|medium` |
| `body[1]/p[557]` | 1562 | Normal | 0 | False | SKIP_NO_STRUCTURAL_SIGNAL | HeadingTopic | `SKIP_NO_STRUCTURAL_SIGNAL|Normal|not-candidate|document_body|Unknown|no-marker|not-table|no-format-signal|numbering-signal|other|medium` |
| `body[1]/p[558]` | 1563 | Normal | 0 | False | SKIP_NO_STRUCTURAL_SIGNAL | HeadingTopic | `SKIP_NO_STRUCTURAL_SIGNAL|Normal|not-candidate|document_body|Unknown|no-marker|not-table|no-format-signal|numbering-signal|other|medium` |
| `body[1]/p[638]` | 1643 | Normal | 0 | False | SKIP_NO_STRUCTURAL_SIGNAL | HeadingTopic | `SKIP_NO_STRUCTURAL_SIGNAL|Normal|not-candidate|document_body|Unknown|no-marker|not-table|format-signal|numbering-signal|other|long` |
| `body[1]/p[892]` | 2235 | Normal | 0.1 | False | SKIP_NO_STRUCTURAL_SIGNAL | HeadingTopic | `SKIP_NO_STRUCTURAL_SIGNAL|Normal|not-candidate|appendix|Unknown|no-marker|not-table|no-format-signal|no-numbering-signal|other|medium` |
| `body[1]/p[907]` | 2250 | Normal | 0 | False | SKIP_NO_STRUCTURAL_SIGNAL | HeadingTopic | `SKIP_NO_STRUCTURAL_SIGNAL|Normal|not-candidate|appendix|Unknown|no-marker|not-table|no-format-signal|numbering-signal|period|long` |
| `body[1]/p[911]` | 2254 | Normal | 0 | False | SKIP_NO_STRUCTURAL_SIGNAL | HeadingTopic | `SKIP_NO_STRUCTURAL_SIGNAL|Normal|not-candidate|appendix|Unknown|no-marker|not-table|no-format-signal|numbering-signal|period|long` |
| `body[1]/p[1003]` | 2380 | Normal | 0.1 | False | SKIP_NO_STRUCTURAL_SIGNAL | HeadingTopic | `SKIP_NO_STRUCTURAL_SIGNAL|Normal|not-candidate|appendix|Unknown|no-marker|not-table|no-format-signal|no-numbering-signal|other|medium` |

Confidence was not persisted in the frozen V7 prediction artifact; it is reported as unavailable rather than reconstructed.
providerCalls=0; goldReads=0; scoring=false; productionPrefilterActivated=false.
