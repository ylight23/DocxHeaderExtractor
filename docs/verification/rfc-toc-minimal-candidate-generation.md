# RFC-2 Minimal TOC Candidate Generation

## Decision

The exact first-loss owner was `RfcTocDictionaryOutline.FindTocCluster`. The remediation is limited to candidate generation for the 092 fixture:

- numeric and appendix markers accept the generated DOCX form without a space;
- an explicit `Table of Contents` marker selects the front-matter window;
- the first repeated top-level marker bounds the TOC before body headings;
- marked table rows inside that already-discovered window are included as TOC source paragraphs.

Dictionary construction, navigation alignment, numbering preservation, route policy, and expected tests were not changed.

## Evidence

The direct analyzer now creates a candidate and reaches:

| Measure | Result |
| --- | ---: |
| TOC paragraphs | 18 |
| Dictionary entries | 67 |
| Body anchors | 67 |
| TOC-only entries | 0 |
| Body-anchor ratio | 1.0 |

The offline diagnostic probe passes with zero provider calls. The controlled negative behavior remains owned by the existing downstream assertions: the 092 test still contains an occurrence-index expectation of `Index = 8`, while the extracted source occurrence is `Index = 100`. RFC-2 does not rewrite that expectation or alter occurrence identity.

## Guards

`PROVIDER_CALLS = 0` and `PRODUCTION_CODE_CHANGED = true` for this remediation. No route-policy or candidate-ranking work was introduced.
