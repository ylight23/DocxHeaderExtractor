# ARCH-3B: Normal Authority Route Policy Wiring

**Revision under verification:** `8204721fef05a29dc5937f9a1b91485c653c87be`
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

## Verification closure

The combined tree now builds cleanly: test-project build completed with zero
errors (28 existing warnings), the ARCH-3/3B route and contract tests pass
`12/12`, the F regression harness passes `2/2`, and the Release solution build
completes with zero warnings/errors. These checks ran on the current combined
tree after ARCH-3B and ARCH-4B were present.

The full suite was not rerun in this focused closure and is not claimed here.

```text
ROUTE_POLICY_WIRED = true
CURRENT_RUNTIME_DOMAIN_EQUIVALENCE = PROVEN
LEGACY_FALLBACK_SEMANTICS_IMPORTED = false
AUTHORITY_PIPELINE_DEPENDS_ON_LEGACY_ROUTE = false
ARCH3B_WIRING_TESTS_PASS = true
F_REGRESSION_PASS = true
RELEASE_BUILD_PASS = true
PROVIDER_CALLS = 0
PRODUCTION_BEHAVIOR_CHANGED = false
```

The machine-readable record is
`eval/architecture/route-policy-wiring.v1.json`.
