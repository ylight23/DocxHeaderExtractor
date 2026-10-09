# Portable source-only review bundle

Package the frozen P7 pilot for an independent reviewer who cannot open the local capture folders.
No source labels, interpretation records, semantic predictions, API credentials or provider calls.
The full original five PDFs, existing six page renders, all 213 selected occurrences with native glyph
geometry, a full-document occurrence-coordinate projection and PENDING review forms are packaged.
External page text/geometry is reference-only; it does not expand evaluation or become negative Gold.

```powershell
dotnet run --project scripts/P7ReviewBundle -c Release -- <pilot-dir> <source-pack> <shape-pack> <new-bundle-dir> <new-zip> <new-public-receipt>
```

Inputs must match the pinned pilot/source/screening/draft hashes. Outputs must be new paths.
Open `index.html` after extraction: all PDF/image/JSON links are relative and verified to exist.
The review worksheet keeps semantic function, heading membership, distinct anchor and exact extent
as separate axes. It is a human review input, not approved machine-consumable Gold.

Boundary closure is a review question, not a claim based on having six selected pages. Review source
around page edges using the full PDF and all-page geometry. If the last unit/first outside is unresolved,
keep PENDING and propose source-only adjacent-page review. Every evaluation expansion needs a new
approved manifest before predictions. Exact-evaluable rate stays null until adjudication.

Scorer membership is reconstructed occurrence-set membership, NOT direct F1/G2A stage accuracy.
Regression tests assert known membership FP stays visible when exact extent is NOT_EVALUABLE because
of unknown cross-scope truth or an open Gold boundary. Overlap is an observation, not semantic truth.

ZIP entries are sorted, timestamped identically and rehashed after compression. `bundle-integrity.json`
records each packaged file's immutable hash (except its own hash, recorded in the public receipt).
Reviewers edit separate working copies; preserve the original package for provenance.

Never commit the ZIP, PDF bytes, page images, source text or review labels to the public repository.
Only builder/tests and sanitized hash/count receipts belong in Git. File delivery does not confer
provider authorization, Gold approval, tokenizer verification or production promotion.
