# ARCH-4Q Built-in Heading Style Ownership

ARCH-4Q closes with **MIXED** ownership. The name `HasBuiltInHeadingStyle`
does not have one semantic meaning in the current pipeline.

## Source-derived identity

`StyleResolver` reads the paragraph style and walks `w:basedOn` from the
document style definitions. The pure `BuiltInHeadingStyleIdentity` projection
then recognizes the existing built-in identity grammar (`Heading1` through
`Heading9`, `Title`, `Subtitle`, and the existing `TOC Heading` compatibility
case). It only needs resolved style metadata and the paragraph's style id.

That level is now available as
`SourceStyleFacts.BuiltInHeadingStyleLevel` and is copied into
`NumberingStyleFeatures.ParagraphStyleFeatures`. It is a derived style feature,
not an assertion that the paragraph is a heading.

## Policy state remains separate

`HeadingHeuristics` sets `SlimParagraph.HasBuiltInHeadingStyle` only when the
style-selection policy is trusted. `DocxSlimExtractor` clears it when
`StyleTrustAudit` rejects style selection and re-runs classification. The flag
therefore controls policy behavior and compatibility/demotion readers; it is not
stable source authority. Built-in Word style identity must not be treated as a
heading decision.

The feature boundary deliberately does not expose `Role`, `Score`,
`IsCandidate`, `GuessedLevel`, trust, or demotion state. No demotion operation
was moved and no heading behavior changed.

## Verification

`NumberingStyleFeatureBoundaryTests` passes 6/6, including pure identity cases
and the existing source-feature contract. The source, candidate, role, score,
and level deltas are zero by characterization. Provider calls are zero.

`DemoteInlineEmphasis` remains partially covered because its complete input
boundary still includes ordered mutable compatibility state. The next bounded
step is ARCH-4R, an ordered demotion state boundary, not a generic style-policy
merge.
