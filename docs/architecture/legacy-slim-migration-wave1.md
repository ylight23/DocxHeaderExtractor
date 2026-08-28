# ARCH-4L Legacy Slim Migration Wave 1

ARCH-4L was audited against `be8d9a4` using the ARCH-4J caller inventory.
No production component met the strict source-only selection rule without
also importing TOC, numbering, demotion, policy, mutable paragraph, or
writeback state. The wave therefore performs no migration and is explicitly
blocked rather than creating a cosmetic adapter.

The closest candidates are not safe yet. `SourceFactsBuilder.FromParagraph`
still reads Slim numbering and table-role state, while `SourceParagraph` does
not carry an equivalent table-role fact within the ARCH-4D2 boundary.
`DocumentStructureEvidence` has source-backed inputs but its production callers
still expose Slim, so migrating it would be a wider route cutover. Evaluation
anchor resolution also reads TOC compatibility state. Repair, writeback and
the legacy route have explicit mutable or mapping responsibilities.

No Slim dependency, source identity, candidate, policy, route, or output
behavior changed. The normal authority Slim reference count remains zero, and
the 170 intentional legacy/test caller files remain unchanged. The three
demotion operations remain deferred. Provider calls were zero.

The next valid wave requires first resolving an equivalent source boundary for
the remaining numbering/table facts. Until then, ARCH-4L should remain
`BLOCKED_NO_SAFE_SOURCE_ONLY_WAVE`; mass migration is not justified by a
reference count alone.
