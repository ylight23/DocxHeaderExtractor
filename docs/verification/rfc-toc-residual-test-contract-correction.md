# RFC-6 RFC TOC Residual Test Contract Correction

RFC-5 identified three latent failures caused by pipeline tests retaining
semantic expectations from the pre-cutover declared-outline route.

The three pipeline tests now assert the current authority route,
`pdf-first-authority-v1`, and no longer assert headings that their current
pipeline does not produce. RFC analyzer coverage remains direct: 092 keeps its
67-entry identity/semantic contract, and 093 has a dedicated direct analyzer
characterization test with 73 dictionary entries and 73 body anchors. The 094
historical `>= 97` count was not recreated because RFC-5 found no authoritative
current direct-analyzer result for it.

Verification:

- `RfcTocDictionaryOutlineTests`: 5/5 PASS
- Direct analyzer semantic coverage: 1/1 PASS
- RFC-2 invariants: 67 dictionary entries, 67 body anchors, 0 TOC-only, ratio 1.0
- SourceFacts regression: 3/3 PASS
- F regression: 2/2 PASS
- Release build: PASS, 0 errors
- Provider calls: 0

C1 was not reclassified again. Counts remain `35 / 18 stale / 0 real
production / 15 legacy-only / 2 diagnostic`, with
`DUPLICATE_RECLASSIFICATION = false`.
