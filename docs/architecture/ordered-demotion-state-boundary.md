# ARCH-4R Ordered Demotion State Boundary

ARCH-4R resolves the ownership of the three remaining demotion operations,
but does not introduce a new state abstraction or move any operation.

## Proven execution dependency

The frozen order is:

`Initial HeadingCandidatePolicy` -> `DemoteCoverPageBlock` ->
`DemoteInlineEmphasis` -> `DemoteRunsWithoutOwnProse` ->
`TocStructuralFeatureDeriver` -> `PostClassificationPolicy`.

`DemoteCoverPageBlock` scans the whole ordered paragraph collection and mutates
`Role` and `Score` for the cover run. `DemoteInlineEmphasis` reads the current
candidate state and the policy-controlled `HasBuiltInHeadingStyle` exemption,
with a whole-document structural-marker gate. `DemoteRunsWithoutOwnProse`
then reads the roles after the earlier operations and maintains an incremental
candidate run while scanning neighboring paragraphs. Its first-loss boundary
is therefore ordered mutable state, not a stateless style feature.

## Ownership decisions

- `DemoteCoverPageBlock`: keep at the Slim compatibility boundary until the
  first-prose and mutable-neighbor state have a real contract.
- `DemoteInlineEmphasis`: blocked by policy state. The built-in identity fact
  from ARCH-4Q is not substituted for the existing trusted-style flag.
- `DemoteRunsWithoutOwnProse`: blocked by mutable neighbor/run state and its
  dependency on preceding mutations.

The three operations remain explicit owners of `SlimParagraph.Role` and
`SlimParagraph.Score`. No source or derived fact is mutated. The artifact
records zero characterization deltas and zero normal-authority Slim references;
these are compatibility-side state transitions only.

## Decision

`DEMOTION_STATE_BOUNDARY_INTRODUCED = false` and
`ORDERED_MUTATION_REQUIRED = true`. A future ARCH-4S may proceed only after a
real ordered state contract is justified. It must preserve the exact order and
transition semantics; an interface that merely renames Slim would not satisfy
this boundary.
