# Heading-only materialization contract

Baseline: `f81d0be`. This change narrows the materialized structural contract to the current
heading extraction product; it is not an accuracy experiment or provider promotion.

## Ownership and retirement

- `SourceSelectionContracts.cs`: exact spans, observed source identity and proposed selections.
- `HeadingStructuralContracts.cs`: heading taxonomy, proposals, validation/results, provenance
  and projection metadata. DTO namespaces and live JSON names remain unchanged.
- `HeadingHierarchyContracts.cs`: the ParentChild relation and its DTOs.
- `ValidatedStructure.cs`: heading-only element admission, unique IDs and parent projection.
- The proposal/source/span gate and relation validator remain in their existing service owners.

Only `Heading = 2`, `HeadingTopic = 0`, and `ParentChild = 0` remain. Retired enum members are
not aliases and are not silently converted. Numeric legacy values are refused by validation/graph
admission; unsupported string values cannot deserialize into the narrowed enums.
The relation validator's optional multi-type compatibility-map argument is removed with its dead
compatibility branches. External consumers of retired members/signatures must migrate/rebuild;
compatibility is preserved for emitted live heading payloads, not for the retired generic API.

The upstream model-role vocabulary, prompt, schema, source snapshots, runtime inference chain,
Gold and historical artifacts remain unchanged. Title extraction still emits the common Heading
type; sections/chunks still carry body source text without inventing non-heading structural units.

## Regression authority

Before edits, a provider-free heading graph fixture was serialized using baseline implementation
`cb2246b` in the clean verification checkout (same implementation as `f81d0be`). Its SHA256 is
`931fc4b2312baf43bf7ccaef1efbd604dd2b8838e72f5ed0146ae2476fff39cd`.
The regression pins these bytes, including hierarchy, validation, provenance, and OutlineElements.
This is a new compatibility fixture, not a rebaseline of a historical frozen artifact.

Tests retain source/span and multipart coverage, add retired numeric/string rejection checks,
and assert exact supported taxonomy/file ownership. The old mixed synthetic graph is replaced
by headings plus unclaimed body-source sections/chunks; it no longer asserts table/figure support.

Verification pending clean tracked build/suite. User H3 edits and untracked research files are
preserved/excluded. Provider calls: 0. Gold mutation: 0. Frozen artifact rebaseline: 0.
