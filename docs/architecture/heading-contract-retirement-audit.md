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

## Verification

Implementation tested: `6a05072309e12a11a5f27785da1d3031f98116db`, in a clean tracked
verification checkout, not the concurrently edited working tree.

- Release solution build: PASS, 0 errors (34 existing warnings).
- Focused contract/output/architecture/semantic/parity/lifecycle tests: PASS, 124/124,
  0 failed or skipped. TRX SHA256:
  `9808dc0992e01af6b6bcc79963921f3732558659a3ecdf63bfd58a20f35dfe71`.
- Live references to the retired non-heading enum members: 0.
- `git diff --check`: PASS; verification checkout tracked status: clean.
- Full Windows tracked suite: NOT COMPLETED. The earlier execution session was lost
  without a full-suite TRX. A fresh run against the same clean implementation aborted
  with `The active test run was aborted. Reason: Test host process crashed`, also
  without a TRX. No specific failing test has been established; this is not a PASS.
- GitHub Ubuntu deterministic CI: not run for this local checkpoint.

Concurrent user edits, staged changes and untracked research files are preserved/excluded.
Provider calls: 0. Gold mutation: 0. Frozen artifact rebaseline: 0.
