# A99-R2 independent human annotation instructions

This package is for independent offline Gold creation. It is not an inference or optimization
task. Annotators must use only the source document identified by each source artifact and must
record exact source-backed occurrences.

## Blindness and independence

Annotator A and Annotator B work independently. Do not open model predictions, A99 reports,
current residual lists, prompt files, or another annotator's output. Do not search for historically
reported strings or structures. Do not infer a heading merely because a style, numbering marker,
table position, or model output suggests it.

## Required row

For each semantically plausible heading occurrence, record `sourceOccurrenceId`, exact heading text,
UTF-16 `start` and exclusive `end`, `headingPresence`, and `semanticFamily`. Record `role` only
if independently determinable; otherwise use null. The source occurrence plus exact UTF-16 span is
the identity authority. Do not silently union disagreements.

## Families

Use one of: DOCUMENT_TITLE, PART_CHAPTER, SECTION, SUBSECTION,
REGION_OR_GEOGRAPHIC_LABEL, PROGRAM_OR_TOPIC_LABEL, TABLE_OR_LOCAL_LABEL,
NAVIGATION_OR_AGENDA, ANNEX, CAPTION, OTHER_STRUCTURAL_LABEL, UNKNOWN.

For semantically plausible heading-like negatives, optionally record one of:
NON_HEADING_TITLE_LIKE, NON_HEADING_REGION_TOPIC_MENTION, NON_HEADING_PARTICIPANT_LABEL,
NON_HEADING_NAVIGATION, OTHER_HARD_NEGATIVE.
Do not exhaustively label arbitrary prose.

## Procedure

Review the complete source, record source-backed spans in document order, and leave uncertainty
explicit. After both annotations are complete, compute exact-span, heading-presence, and family
agreement. Every disagreement goes to adjudication with both original rows preserved.

These annotations are offline diagnostic Gold. They must never become runtime rules.
