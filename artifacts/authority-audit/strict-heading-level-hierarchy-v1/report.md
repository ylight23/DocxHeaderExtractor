# Strict heading Gold level/hierarchy authority audit

Generated: 2026-09-15
Provider/model calls: **0**
Gold mutation: **false**

## Conclusion

**Existing strict heading Gold DOES NOT constitute general hierarchy Gold.**

The current materialized strict-gold-v4 heading artifacts contain reviewed heading occurrences, roles, source references/spans, and direct historical level values. Their parent fields are null and their capability declarations mark parentEvaluable=false and hierarchyEvaluable=false. Therefore their levels are classified as DIRECT_HISTORICAL_LEVEL_ANNOTATION, not TREE_DERIVED_LEVEL.

The canonical vNext semantic registry is not a replacement hierarchy authority: it explicitly freezes semantic totals with exactOccurrenceFreeze=false; it does not provide an exhaustive occurrence list, bindings, parent edges, or tree.

## Strict heading Gold counts

| Measure | Count |
|---|---:|
| strict heading Gold documents | 9 |
| strict heading Gold occurrences | 520 |
| documents with level | 9 |
| occurrences with level | 520 |
| proven tree-derived level documents | 0 |
| proven explicit parent/tree strict Gold documents | 0 |
| direct/projected historical-level-only documents | 9 |
| unknown level-provenance strict documents | 0 |

## Interpretation

- Historical level can benchmark heading detection only where the heading rows are valid and source-linked.
- Historical level may be used for a restricted final-level comparison, but only with the restriction that it is direct annotation, not proof of a parent relation or tree topology.
- It cannot authorize parent-edge accuracy, tree validity, or a claim that level = depth(tree) was Gold-derived.
- The dedicated HDSA DOC-0205 artifact is explicit source-only hierarchy Gold for its scoped review (10 semantic nodes, 9 parent edges, one excluded masthead occurrence) and separately records levelIsDerived=true. It must not be generalized to the strict-Gold corpus.
- The keys/hierarchy artifact is retained as evaluation-only historical hierarchy evidence, not current canonical authority.

### DOC-0258 forensic result

strict-gold-v4/DOC-0258.strict-gold-v4.json materializes 24 reviewed heading rows with direct level values and historical-key source references. Every parentHeadingOccurrenceId is null; the artifact declares parentEvaluable=false and hierarchyEvaluable=false. Its paired occurrence-binding artifact proves source bindings for those 24 rows but also carries no parent/tree topology. The current canonical vNext DOC-0258 semantic freeze is 37 semantic heading occurrences, explicitly has exactOccurrenceFreeze=false, and states that the historical 24-occurrence artifact is retired for canonical all-true-heading truth. Thus the 24 historical levels are not a copied or derived canonical tree; they remain historical level annotations only.

## Priority document notes

The matrix includes all authority-shaped strict/canonical artifacts discovered under the audited roots, including the requested DOC-0001, DOC-0205, DOC-0252, DOC-0256, DOC-0258, DOC-0092, DOC-0133, DOC-0158, DOC-0165, DOC-0255, DOC-0259, and DOC-0264 where present. Documents with only canonical semantic totals are explicitly marked as lacking occurrence and hierarchy authority.

See manifest.json, document-authority-matrix.json, level-provenance.json, and hierarchy-authority.json for hashes and per-artifact evidence.
