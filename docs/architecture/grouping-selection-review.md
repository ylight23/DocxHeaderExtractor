# Grouping authority in production

Updated 2026-10-19. Selection owns heading membership; grouping owns only the boundaries between
selected aliases. The production grouping contract has no `dispute` result. A dispute-shaped or
otherwise malformed answer is a protocol failure and receives the ordinary bounded protocol
repair. Persistent protocol/transport failures remain unresolved.

Normal grouping output is accepted only after C# validates exact run and alias coverage, ordered
contiguous groups, source order, and allowed group sizes. Only protocol-failed/fallback aliases are
withheld from binding; the grouper cannot veto a selected alias based on membership. The original
selection answers and grouping provenance remain in the execution audit.

`AgentSelectionReview` and `AgentGroupVerification` remain in the repository as qualification
helpers and historical experiments. Production selector IDs carry the
`selection-boundary-grouping-csharp-validation-v2:no-semantic-membership-veto` contract marker.
