# ARCH-4D Derived Feature Extraction

ARCH-4D introduces `IDocumentFeatureDeriver` and the pure
`DocumentFeatureDeriver` component. It accepts immutable `SourceDocument` facts
and returns `DerivedDocumentFeatures`; it does not mutate source facts and has
no dependency on heading policy, candidate selection, ranking, model proposals,
validation, hierarchy, or route fallback.

The first moved inventory fact is `BodyFontSizePt`. The old character-weighted
body-font calculation now runs from the source contract before the existing
heuristic stages and populates the same Slim compatibility field. The derivation
also exposes immutable character-weight statistics for later consumers.

The remaining ARCH-4A derived fields stay in their existing producer-local
stages for now. They depend on numbering/style metadata, document traversal
state, or policy-adjacent operations that are not currently represented in the
source-only contract. Keeping them in place avoids inventing a second authority
or changing behavior; they are explicit deferred scope, not silently claimed as
migrated.

Verification: ARCH-4C `5/5`, ARCH-4D `4/4`, combined architecture regression
`26/26`, F regression `2/2`, Release build `PASS`, provider calls `0`.
