# A99 Strict Gold Authority Policy V2

Strict Gold is determined by user adjudication, not by whether ChatGPT or another model assisted the review.

## Authority rule

A reference is Strict Gold only when the user explicitly finalized it, the entire document was reviewed, the heading set was declared exhaustive, and no unresolved semantic uncertainty remains. `FINAL_AUTHORITY=USER` is required.

Review assistance remains truthful audit metadata. `HUMAN_WITH_MODEL_ASSISTANCE` is allowed and does not disqualify Strict Gold; it is never rewritten as human-only.

## Evaluation capabilities

Semantic Gold status is separate from evaluation capability. Each reference reports whether semantic, occurrence, character-span, role, level, parent, and hierarchy evaluation is supported. Unsupported dimensions are `NOT_EVALUABLE`; spans are never fabricated.

## Historical finalized references

`DOC-0255`, `DOC-0259`, `DOC-0202`, and `DOC-0123` are user-finalized references. `DOC-0202` remains semantically fixed at 111 headings and is not character-span evaluable. `DOC-0123` remains semantically fixed at 317 headings; its model-assisted provenance is preserved and its exact occurrence/span capability remains unavailable until source mappings are validated.

These historical references do not reseed the frozen 15-document DEV cohort. Cohort membership and global Strict Gold authority are reported separately.

Holdout remains sealed and provider calls remain zero. The A99 baseline and claim remain unmeasured until the active cohort has complete Human Gold.
