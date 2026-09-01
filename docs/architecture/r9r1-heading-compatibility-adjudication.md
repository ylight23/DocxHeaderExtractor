# R9R1: Heading Compatibility Adjudication

Status: `PASS`

R9's provider implementation remains unchanged and remains `PASS`. A strict replay performed
after the R9 merge found a pre-existing heading projection drift from R5-5R1: the generic PDF
authority correctly retained parser-owned block identities and spans, while the compatibility
projection had been reading those coordinates instead of the legacy `SourceAnchor` coordinates.

R9R1 repairs only that boundary. `StructuralAuthorityMaterializer` keeps PDF catalog source
references as the generic authority. It also records legacy ordinal, stable ID, span, and grounded
text in projection-only metadata. `HeadingOutlineProjection` consumes that metadata only when
building `HeadingRecord`; sections, chunks, and fact consumers continue to read the parser-owned
generic sources.

## Revision authority

- R9 provider execution: `69d0a3333c7deb6a47d468d293cc278351ce13e3`
- R9 publication: `cf8aa9fba7bf8fd7f28179228108661b3b063ea3`
- Initial strict replay: incomplete; it exposed the R5-5R1 compatibility drift
- R9R1 execution revision: `bef9d5e1b9d42f42998cef99a19a458503e1deba`
- Publication revision: the commit containing this supplement
- Root-cause round: `R5-5R1`

The original R9 publication is not rewritten. This supplement adjudicates and closes the replay
gap it did not measure.

## Evidence

- Generic PDF source authority changed: `false`
- PDF catalog-bearing compatibility: `PASS`
- Catalog-free compatibility: `PASS`
- Regression fixture replay: `028/056/091 = 3/3`, final structure, decisions, product, and final
  heading JSON deltas all `0`
- Coordinate separation regression: `PASS` (generic `b17/16/4..20`, legacy `para-451/451/10..26`)
- Provider calls: `0`
- Host fingerprint changed: `false`
- Expected baseline rebased: `false`
- Frozen fixture changed: `false`

The exact execution full suite measured `901 total / 899 passed / 2 failed / 0 skipped`. The two
failures are the unchanged frozen `C1` and `N15` fingerprints; there are no new, changed, or
unjoined failures.

`R9R1 = PASS`  
`ROUND9 = CLOSED`  
`ROUND10 = AUTHORIZED`
