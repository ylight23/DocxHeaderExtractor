# ARCH-3B: Normal Authority Route Policy Wiring

**Revision:** `de20821` plus the uncommitted ARCH-3B wiring changes  
**Scope:** normal authority route decision only.

## Change

The route decision in `AuthorityExtractionPipeline` now goes through the
application-level contract:

```text
SourceCapabilities
    -> IAuthorityRoutePolicy.Decide
    -> AuthorityRoute
    -> existing PDF or DOCX body
```

`DefaultAuthorityRoutePolicy` is in `Core.Application.Routing` and references
no legacy pipeline, fallback flag, source model, OpenXML, PDF, or provider
implementation. The existing one-argument constructor remains compatible and
uses the default policy; an overload permits injection for characterization and
future composition.

The PDF/DOCX extraction bodies and all downstream stages are unchanged. The
only production behavior intended by ARCH-3B is ownership of the already
characterized route predicate.

## Characterized matrix

| DOCX | PDF | Analyst | Decision |
|---|---|---|---|
| yes | yes | yes | `PdfAuthority` -> `pdf-authority-v1` |
| yes | yes | no | `DocxAuthority` -> `docx-authority-v1` |
| yes | no | yes | `DocxAuthority` -> `docx-authority-v1` |
| yes | no | no | `DocxAuthority` -> `docx-authority-v1` |
| no | yes | yes | `Unsupported` (target contract extension; outside current runtime input domain) |

The ARCH-3 characterization matrix remains green (`6/6`). The legacy
`HeaderExtractionPipeline.PdfFirstValidatedFallback` semantics are not
imported into this normal route.

## Verification boundary

The Core project builds successfully with zero warnings/errors. The focused
test-project attempt is currently blocked before test execution by pre-existing
compile errors in `SourceFactsCompatibilityTests.cs` (`DocumentMode.Normal`,
the `DocumentModeReport` constructor, and `IsExternalInit`). Those errors are
outside ARCH-3B and were not modified or suppressed. Therefore the wiring test
suite and F regression are not claimed as passed yet.

```text
ROUTE_POLICY_WIRED = true
CURRENT_RUNTIME_DOMAIN_EQUIVALENCE = PROVEN
LEGACY_FALLBACK_SEMANTICS_IMPORTED = false
AUTHORITY_PIPELINE_DEPENDS_ON_LEGACY_ROUTE = false
PROVIDER_CALLS = 0
PRODUCTION_BEHAVIOR_CHANGED = false
```

The machine-readable record is
`eval/architecture/route-policy-wiring.v1.json`.
