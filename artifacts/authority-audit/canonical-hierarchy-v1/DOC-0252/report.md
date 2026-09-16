# DOC-0252 — canonical semantic-node hierarchy adjudication

Status: **READY_FOR_USER_APPROVAL_CANONICAL_HIERARCHY**

## Result

- Semantic nodes: **39**
- Parent edges: **39**
- ROOT children: **8**
- Max depth: **3**
- Ambiguities: **0**
- Global consistency violations: **0**
- Validator: **READY_FOR_USER_APPROVAL_CANONICAL_HIERARCHY**

The hierarchy is built over semantic nodes, not physical heading occurrences. The frozen identity authority contains 40 occurrences mapped to 39 nodes. N032 contains H032 (SESSION V: Current Research) and H034 (SESSION V: Current Research (Cont’d)), receives one parent edge to $continuationParent, and has one derived level shared by both occurrences.

## Structural decisions

The narrative sessions are independent top-level branches under synthetic ROOT. Session II, III, IV and V contain their source-backed subsections. Agenda is an autonomous branch under ROOT; its Day headings contain agenda sessions. The Session V agenda semantic node is anchored under Day 1 because its primary occurrence begins there; the Day 2 (Cont’d) occurrence is continuation evidence, not a second parent relation. Annex 2 is a separate ROOT branch with participant subsections.

## Firewalls

- Identity authority: frozen user-approved identity at 4f227e
- Historical level/parent/hierarchy: not read
- Old semantic total: not used for decision
- Provider/model calls: 0
- Occurrence mutation: false
- Identity mutation: false
- Reviewer-entered level: false

Levels were derived only after graph validation and are stored in derived-levels.json. This is source-backed hierarchy proposal output and is **not** user-approved hierarchy Gold until explicit user approval.
