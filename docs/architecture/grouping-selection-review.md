# Grouping disputes in production

Updated 2026-10-19. A grouping dispute does not reopen selection and does not start another
semantic review. It records that grouping could not provide a valid division; the disputed run
stays unresolved. A model verifier cannot change membership or decide whether the selection was
right.

Normal grouping output is accepted only after C# validates the exact run and alias coverage,
ordered contiguous groups, source order, and allowed group sizes. Protocol-invalid/fallback runs
and explicit disputes are withheld from binding. The original selection answers and grouping
provenance remain in the execution audit.

`AgentSelectionReview` and `AgentGroupVerification` remain in the repository as qualification
helpers and historical experiments. Production selector IDs carry the
`selection-grouping-csharp-validation-v1:no-semantic-verifier-authority` contract marker.
