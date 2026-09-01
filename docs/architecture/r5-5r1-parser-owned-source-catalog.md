# R5-5R1 Parser-Owned Source Catalog

R5-5R1 makes the source catalog a parser-owned inventory. DOCX uses the
`SourceDocument` read by `OpenXmlDocumentSource`; PDF uses semantic blocks
built from the PDF parser lines and annotations. Sections and chunks join
validated structural references to that inventory by `SourceId`.

The old `MergeStructuralSources` path was removed. No generic output path
creates a source unit from `ValidatedStructuralElement.Text`; a missing source
is an explicit grounding error. Catalog units retain complete parser raw text
and a full-source span, while structural references retain their exact narrow
span. This keeps source and structural coordinate systems separate, including
multi-source elements.

## Verification

- Base revision: `f84a84bab13075bcd6e589299fd05d107f571591`
- Execution revision: `e32ed8b0df1e40d17fa318fdfbb1bb585e7439b5`
- Publication revision: `containing-closure-commit`
- DOCX catalog: `SourceDocument` parser facts, PASS
- PDF catalog: parser-owned PDF blocks/source facts, PASS
- Structure-to-catalog reconstruction: `0`
- Element text used as catalog source text: `0`
- Nonzero-span source test: PASS (`7..23` remains structural; catalog keeps `0..30`)
- Multi-source element test: PASS
- Source offset coordinate preservation: `true`
- Source unjoined/invented text: `0/0`
- Sections/chunks: non-empty; chunk text is `100%` source-catalog-backed
- Focused suite: `53/53` passed
- Release build: PASS; `git diff --check`: PASS
- Deterministic replay `028/056/091`: joined, zero structure/decision/product/final-heading delta
- Host E2E: PASS; fingerprint unchanged:
  `16284414abee710236b27fe92f710b95efb32928e169ba0e1ede2e63891b8429`
- Provider calls: `0`
- Full suite at the exact execution revision: `857 total / 855 passed / 2 failed / 0 skipped`
- Frozen failures: `C1`, `N15`; new failures `0`, changed fingerprints `0`, unjoined failures `0`

The full suite count is measured from the current tree. It is not compared by
assuming an earlier inventory size.

## Result

`R5-5R1 = PASS`. The generic extraction result now combines a parser-owned
`DocumentSourceCatalog` with `ValidatedStructure`; it does not reconstruct
source text from structural output. The next layer may consume sections and
chunks for retrieval or IE without treating headings as document body text.
