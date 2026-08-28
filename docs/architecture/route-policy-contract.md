# Normal Authority Route Policy Contract

**ARCH-3 status:** contract characterized; production wiring deferred.  
**Revision:** `42585a177f6a1d4992153c5aecc211f33ecdcaa2`

## Contract

The smallest useful application-level input is:

```text
SourceCapabilities(HasDocx, HasPdf, AnalystAvailable)
    -> IAuthorityRoutePolicy.Decide(...)
    -> DocxAuthority | PdfAuthority | Unsupported
```

The policy does not receive provider objects, confidence, validation counts,
quality scores, repair mode, or fallback scores. It therefore cannot recreate
the semantics of `PdfFirstValidatedFallback`.

The characterized rules are:

| DOCX | PDF | Analyst | Route | Reason |
|---|---|---|---|---|
| yes | yes | yes | `PdfAuthority` | `PDF_AVAILABLE_AND_ANALYST_AVAILABLE` |
| yes | yes | no | `DocxAuthority` | `PDF_AUTHORITY_CAPABILITY_UNAVAILABLE` |
| yes | no | yes | `DocxAuthority` | `PDF_AUTHORITY_CAPABILITY_UNAVAILABLE` |
| yes | no | no | `DocxAuthority` | `PDF_AUTHORITY_CAPABILITY_UNAVAILABLE` |
| no | yes | yes | `Unsupported` | `DOCX_CAPABILITY_UNAVAILABLE` |

The last row is explicitly outside the current normal input contract: the
current pipeline requires a DOCX conversion before route selection. The target
contract makes that unsupported state explicit instead of inventing behavior.

## Production equivalence

The current `AuthorityExtractionPipeline` has the equivalent predicate:

```text
HasPdf && analyst != null -> pdf-authority-v1
otherwise                 -> docx-authority-v1
```

This is a static characterization of the current branch in
`AuthorityExtractionPipeline.cs:51-91`, not a runtime route change. The
test-local characterization matrix locks the same truth table without calling
any provider. `BEHAVIOR_EQUIVALENCE = PROVEN` means predicate equivalence;
`AuthorityExtractionPipeline` is unchanged and not wired to the contract.

## Legacy guard

The contract has no dependency on `HeaderExtractionPipeline` and no input or
branch named `PdfFirstValidatedFallback`. That legacy route remains available
for compatibility/evaluation callers, but its semantics are deliberately not
imported into the normal application policy.

ARCH-3 does not move files, create Domain/Application projects, or alter old
expected values. ARCH-3B may later wire an equivalent policy into the normal
authority orchestrator, followed by the regression/provenance gates.

```text
PROVIDER_CALLS = 0
PRODUCTION_CODE_CHANGED = false
PRODUCTION_BEHAVIOR_CHANGED = false
```
