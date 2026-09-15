# Strict heading parent/tree review contract

This directory is a source-only review preflight, not hierarchy Gold.

Input authority: the nine materialized strict-gold-v4 heading lists. Historical
level fields are deliberately omitted from packets to prevent level-to-parent
anchoring. No model predictions, candidate sets, old hierarchy outputs, or
identity evaluation artifacts are exposed.

The reviewer must assign semantic node identity and parent edges from source
meaning/evidence. A parent edge is between semantic nodes, not necessarily
between every physical occurrence. Repeat/continuation occurrences retain
provenance. If evidence is insufficient, mark the occurrence/node UNRESOLVED.

Required freeze validation after review:

- every reviewed occurrence belongs to the frozen source document;
- no duplicate physical occurrence assignment;
- resolved semantic nodes have at most one structural parent;
- exactly one root per resolved tree/scope, unless the reviewer explicitly freezes a forest;
- no parent cycle;
- no parent edge synthesized from level or order alone;
- canonical serialization and SHA256 are stable.

Only after this review is adjudicated and frozen may the lane derive
level = depth(tree) and compare it with the historical level annotation.
