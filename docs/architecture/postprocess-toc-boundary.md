# ARCH-4E3 PostProcess TOC Boundary

`PrecedesTableOfContents` is a derived structural relation, not a candidate
decision. ARCH-4E3 moves only its adjacency calculation to
`TocStructuralFeatureDeriver`, keyed by `SourceId` and driven by the existing
TOC-entry identities produced by `MarkTypedTableOfContentsRuns`.

The TOC detector itself is unchanged. `PostProcess` still owns the existing
policy mutation that promotes a preceding paragraph to `HeadingCandidate` and
adjusts its score. It also retains the independent next/previous heading score
adjustments. Therefore the remainder is now pure policy, while TOC detection and
the new structural derivation remain separate.

No demotion operation moved. No candidate, role, score, level, validator,
hierarchy, route, or model behavior changed. Duplicate text is safe because the
relation is keyed by source identity, never text.
