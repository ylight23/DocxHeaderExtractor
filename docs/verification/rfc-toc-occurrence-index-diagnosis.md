# RFC-3 RFC TOC Occurrence Identity & Index Reconciliation

## Reproduction

RFC-2 remains reproduced: `DictionaryEntries = 67`, `BodyAnchors = 67`, `BodyAnchorRatio = 1.0`. The audit-only probe passes without changing production or test expectations.

## Index Semantics

`ParagraphWalker` assigns a zero-based traversal ordinal while walking `document.xml`. `DocxSlimExtractor.BuildParagraph` copies it to `SlimParagraph.Index`, and `RfcTocDictionaryOutline` copies the selected body paragraph index to `HeadingRecord.Index`. This is the raw source traversal index, stable across filtering and table nesting. `StableId` is the XML structural identity and is also preserved.

For `1. Introduction`:

| Occurrence | Index | StableId | Table depth | Meaning |
| --- | ---: | --- | ---: | --- |
| TOC | 60 | `body[1]/p[14]` | 0 | source dictionary occurrence |
| body anchor | 100 | `body[1]/tbl[13]/tr[1]/tc[1]/p[1]` | 1 | selected heading occurrence |

The runtime result `Index = 100` is therefore internally consistent. The expected `Index = 8` is a hard-coded test literal; no source paragraph, filtered ordinal, TOC ordinal, body ordinal, OpenXML local index, or projection operation produces 8. It is not the same index space.

Same-document controls show the same runtime convention: `5. Field Definitions` resolves to source index 729 and `Appendix A Collected ABNF` to source index 1251, with stable identities preserved.

## Conclusion

`OCCURRENCE_IDENTITY_MATCH = true` and `POSITIONAL_INDEX_MATCH = false`. The first divergence is at the test expectation, not body-anchor construction or result projection. Classification is `STALE_TEST_EXPECTATION`; production remediation is not justified. Test-expectation review is justified, but is intentionally outside RFC-3.

`PROVIDER_CALLS = 0`, `PRODUCTION_CODE_CHANGED = false`, and `TEST_EXPECTATION_CHANGED = false`.
