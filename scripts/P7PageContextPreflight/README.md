# P7-D2 page source context: provider-free engineering checkpoint

This builds immutable context candidates, not runnable B-Spatial provider requests.
There is no provider transport, credential access, Gold input, semantic authority,
source mutation, or production dependency on this code. The existing control and B V2
provider bodies are hash-verified and remain unchanged.

The important correction to the preceding coverage discussion: G2A issued **10 primaries**
and exposed **19 occurrences including neighbors**, not 19 primaries. On this one fixture,
F1 has a single request containing all 79 occurrences. This is a per-request measurement,
not evidence that arbitrary multi-pack F1 executions expose entire pages in each request.

## Representation and scope policy

Context uses the existing raw store, grouped by observed page number. Every observation
retains its source alias, original source ordinal, exact span and raw observed fields.
All context observations are `selectable=false`. There are no new O# decision subjects,
semantic rows/columns, inferred table labels, or heading/continuation predicates.
Coordinates are the existing parser glyph-union bounds in PDF points, bottom-left origin.
Order remains canonical source order, explicitly not a table row/column ontology.

Three generic policies are fixed before reading any new model output:

- Local: include subject plus same-page boxes intersecting its vertical bounds expanded
  by 72 points above/below. No horizontal restriction: other columns in this region remain
  visible. This is a geometric observation window, not a claim of relatedness.
- Page: include all canonical source occurrences on each subject's page.
- Adjacent: page scope plus available page numbers within radius 1. Optional engineering
  probe for cross-page source context, not a semantic continuation judgment.

Missing geometry is reported, never converted into false. A local subject without bounds
remains included, but has no fabricated font-height or reading-order fallback window.
Page scope can retain unmeasured observations without assigning invented bounds.
Source pages without canonical occurrences, images, rules, raster renderings and page
dimensions are not supplied by this store; full canonical-page context is not full visual PDF.

Glyph span maps remain in the unchanged parser diagnostics: they are **not bulk-projected**
into context. No glyph retrieval service or agent tool has been implemented in this checkpoint.

## Actual fixture measurements

The private frozen baseline is the same reference PDF used for coverage audit, not a new
held-out corpus. There are 24 context candidates: 8 existing requests × 3 scope policies.
Existing B's stageInput/sourceEvidence stays intact; context is additive. Context count
is therefore not the total model-visible count. Added aliases measure genuinely new sources.

| Target | Local context | Page context | Local new aliases | Page new aliases |
| --- | ---: | ---: | ---: | ---: |
| G2A (all 10 primaries) | 34 | 79 | 16 | 60 |
| H2C O5 | 6 | 38 | 1 | 4 |
| H2C O45 | 11 | 41 | 2 | 6 |
| H2C O46 | 11 | 41 | 3 | 7 |
| H2C O47 | 11 | 41 | 4 | 8 |

For O45/O46/O47, the local window contains aliases L0038:S0, L0039:S0,
L0040:S0–S2, L0041:S0–S2 and L0042:S0–S2. Thus it contains preceding sources and
two subsequent groups of horizontally separated source boxes, without calling them
table rows or adjudicating their semantic role.
Local same-page prefix counts are 2, 3, 4 respectively; page counts are 6, 7, 8.
O5 local context has one preceding occurrence; page context has four. No claim of
accuracy improvement follows from these engineering coverage measurements.

## Size and verification gates

Context UTF-8 cap: 262,144 bytes. Draft additive user envelope cap: 1,048,576 bytes.
Exceeding either fails closed; never truncate or silently change scope.
Each context and draft user envelope receives its own deterministic hash. Only contexts
are persisted privately, and the public manifest contains aliases, numeric counts and hashes.
Raw source text is not committed in public artifacts.

UTF-8 bytes and Unicode scalar counts are measured exactly. **Input tokens are not measured**:
there is no pinned Qwen tokenizer in this tool. Scalar/byte counts must not be presented as
model token counts. A tokenizer/context-window budget gate must be closed before provider
authorization. Draft size excludes the yet-unfrozen additional context instruction, so it
is not a final complete provider input budget.

Existing B already requires references and interpretation. Consequently, `B+Page` versus
`B+Page+Interpretation` cannot isolate a new interpretation effect. A clean future design
should use two axes: scope (existing/local/page) and response requirement (evidence-only /
references+interpretation), with identical raw evidence inside each same-scope pair.
This checkpoint does not generate those six runnable arms or change response parsers.
In particular, the existing B verifier still limits references to its original source scope;
expanded context reference validation must be versioned and tested before execution.

Pending provider gates: independently reviewed multi-document cohort and scoring policy;
model tokenizer budget; stage-specific nonselectable context instruction; versioned reference
scope/parser; frozen system/user/provider bodies; explicit call authorization. No semantic
qualification, causal conclusion, or production promotion is claimed.

## Reproduce

`dotnet run --project scripts/P7PageContextPreflight/P7PageContextPreflight.csproj -c Release`
with repository, reference PDF, frozen control directory, B V2 request directory, **new**
private context directory, **new** sanitized output manifest. Use isolated build artifacts
inside `.verify-build`. Existing output paths are rejected. Reproduction must hash-identically
match all context bytes and the sanitized manifest.

Synthetic regressions cover multi-horizontal-region/borderless-layout geometry, prefix and
below-window inclusion, adjacent pages, Unicode/text preservation, missing bounds, immutable
spans and order, invalid subjects, deterministic enumeration, and no silent budget truncation.
These are representation tests, not cross-document semantic accuracy tests.
