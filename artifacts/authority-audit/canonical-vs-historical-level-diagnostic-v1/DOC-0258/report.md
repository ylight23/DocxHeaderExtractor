# DOC-0258 canonical vs historical level diagnostic

Status: DIAGNOSTIC_COMPLETE

Canonical authority was frozen before historical level access at commit 0c7f22c.
The canonical tree and derived levels were not changed.

## Counts

- Canonical occurrences: 44
- Historical direct level rows: 24
- Exact deterministic bridges: 24
- Canonical-only occurrences: 20
- Historical-only rows: 0
- Ambiguous bridges: 0
- Exact level matches: 3
- Level mismatches: 21
- Matched-level accuracy: 0.125

## Delta distribution

- delta 0: 3
- delta 1: 21

## Branch distribution

- ANNEX_1_AGENDA: matched=1, exact=1, mismatched=0
- ANNEX_2_PARTICIPANTS: matched=1, exact=1, mismatched=0
- DOCUMENT_NARRATIVE: matched=22, exact=1, mismatched=21

## Interpretation

All bridged narrative mismatches are +1 canonical depth relative to the direct historical level. The diagnostic label is ROOT_DEPTH_SHIFT: the canonical tree treats the document title/root occurrence as an ancestor of the narrative branch, while the historical annotation does not apply that same depth convention. Bridged Annex 1 and Annex 2 root rows match. This is an annotation-policy compatibility observation, not a canonical-tree error claim.

## Firewall

- Canonical Gold mutation: false
- Parent mutation: false
- Level mutation: false
- Historical level read: true
- Historical parent read: false
- Provider/model calls: 0/0

Historical compatibility results must not be used to repair the canonical tree.
