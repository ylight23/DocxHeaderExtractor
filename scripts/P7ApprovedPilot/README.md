# P7 approved pilot Gold serialization and scorer dry-run

Provider-free, qualification-only. Reads the original source-review ZIP, the assistant proposal
ZIP, original source snapshots and explicit user approval. Preserves all inputs; refuses existing
output paths. It never reads model responses or changes production, older Gold or frozen requests.

Run from the repository root:

```powershell
dotnet build scripts/P7ApprovedPilot/P7ApprovedPilot.csproj -c Release --artifacts-path .verify-build/p7-approved-gold
dotnet .verify-build/p7-approved-gold/bin/P7ApprovedPilot/release/P7ApprovedPilot.dll <source-review.zip> <proposal.zip> <original-source-pack> artifacts/web-pdf-semantic-diagnostic/p7.d2.3.user-approval.v1.json <new-private-dir> <new-public-receipt> <approved-v1-dir>
```

The ZIP and proposal hashes are pinned to this approved pilot. Every original PDF, snapshot,
review entry, selected row, part and immediate exit must match. Each whole-atom span is
`start=0, length=source.Text.Length`, measured in .NET UTF-16 code units without normalization.
Document-scoped aliases are never joined across PDFs. All out-of-scope rows remain explicitly
unlabelled. The two pending rows are resolved only by the user-approved policy overrides.

Private outputs contain text/native source IDs: keep them outside Git. Public receipts contain
hashes, counts and statuses only. Provenance is assistant-proposed/user-reviewed and approved,
not two independently certified reviewers or provider-verified Gold.

Gold V2 removes `unitRole` from decision DTOs. Reviewer role strings and free-text explanations
are optional sidecars, never parser facts, required model enums, or scoring labels. The original
proposal and V1 approval (including historical role strings) stay immutable provenance inputs.
The policy amendment preserves all approved task decisions. `OTHER` means outside this particular
heading task, not a universal negative for future supertitle/administrative-region tasks.

Before writing V2, the tool requires byte-identical scoring projections to all five hash-pinned
V1 documents and identical outcomes for every dry-run scenario. A representation hash changes;
no semantic label, source identity, span, successor or selected page may change. V1 artifacts
are preserved. Gold contains no mandatory reviewer interpretation field; sidecars are independently
hash-addressed and explicitly unscored. The task policy is separately versioned and hash-bound.

Dry-run oracle scores validate the evaluation plumbing, **not model accuracy**. Included synthetic
scenarios exercise omitted predictions, false-positive membership, under/overextent, duplicate
anchors, overlap and crossing-scope unknown truth. The latter preserves the full prediction and
known in-scope FP/FN. D05 has no outside document occurrence, so crossing is not applicable there.

The request manifest is explicitly `NOT_FROZEN`, with no fabricated request hashes. Source/Gold/
scorer hashes are closed; new pilot control/treatment requests require a separate preflight.
D2.3 remains OPEN; provider inference and production promotion stay LOCKED.
