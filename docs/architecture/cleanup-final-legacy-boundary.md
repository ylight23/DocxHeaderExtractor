# Cleanup — Final Legacy Boundary

Status: PASS

This cleanup was executed from `main@c67ac6683156efd25ddf1105557ac1d4f82ab60a` on
`cleanup/final-legacy-boundary`.

## Execution

- Execution revision: `3b318f93fe6ac94813a30afc8a8615ad9bcc445d`
- Publication revision: containing-closure-commit
- Normal authority and product APIs remain unchanged.
- Historical `PdfLegacyValidatedOutputPolicy` is isolated in the `DocxHeaderExtractor.Eval`
  assembly. Its only remaining use is the explicit historical PDF evaluation command; normal
  extraction routes do not reference it.

## Boundary census

- Removed `DocumentDomainPolicy.HierarchyTier`.
- Removed `DocumentDomainPolicy.IsExcludedFromOutline`.
- Removed `DocumentDomainPolicy.IsConventionalOutlineRole`.
- Removed the obsolete `ValidatedStructure.Headings` view.
- `MergeStructuralSources` references: `0`.
- Core runtime references to `PdfLegacyValidatedOutputPolicy`: `0`.
- Normal CLI, Web, MCP, AgentHarness, repair, section/chunk, retrieval, and fact routes do not
  depend on the historical PDF policy.
- `SlimXmlChunker` remains the model-input chunker.
- `LegacyDocConverter` remains the `.doc` to `.docx` compatibility converter.

`HeadingRecord` remains only at the compatibility output, repair, diagnostic, and explicit
evaluation/shadow boundaries. It is not used as generic structural, section/chunk, retrieval, or
fact authority.

## Verification

- Focused cleanup and host/replay tests: `45/45/0/0`.
- Replay fixtures `028`, `056`, and `091`: joined, zero structure/product/heading deltas.
- Host fingerprint: `16284414abee710236b27fe92f710b95efb32928e169ba0e1ede2e63891b8429`.
- Provider calls: `0`.
- Release build: `PASS`.
- Full suite: `910/908/2/0`.
- Frozen failures: `C1`, `N15`.
- New failures: `0`.
- Changed failure fingerprints: `0`.
- Unjoined failures: `0`.
- `git diff --check`: `PASS`.

The full suite's two failures are the established frozen `C1` and `N15` diagnosis probes. They
remain frozen; neither is reclassified as resolved or ignored by this cleanup.
