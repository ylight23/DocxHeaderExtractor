# Target facts in the first production request

Owner request, 2026-10-16: extend the scan route's existing `targetFacts` delivery to native text PDF and DOCX.

All three production selectors now supply each segment's complete target facts in its first request, ordered like
`targets`. PDF and DOCX read their existing `get_line_facts` handlers locally, following `hasMore`/`nextOffset` until
every target is present. This makes no provider call. It preserves the compact fact content, including PDF's
`repeatedText` observations and DOCX's `repetition` summary; it does not send the full formatting/span records.

The runner records the supplied targets in its core-facts coverage ledger. References actually shown by the facts
are citable, but mentioning another line does not claim that line's own core facts were supplied. Detail evidence
remains a separate ledger. `get_line_facts` and all other evidence tools remain available on demand.

The PDF and DOCX system prompts describe the new delivery and allow a first-turn answer. The user instructions,
membership decisions, heading extents, grouping, binding and hierarchy rules are unchanged. Their selector IDs
add `facts-in-request`. Scan's prompt and behavior are unchanged by this extension. The frozen qualification
implementation and its historical artifacts are not modified.

The admission budget now measures the actual first request, including facts, for every route. PDF keeps its
page/block segmentation, splitting a core further only if its facts exceed the same 28 KB target used for DOCX;
every source line remains in exactly one core, with context across core boundaries. Request byte, call, cost and
optional-tool limits still apply. This removes the mandatory facts round trip; optional calls or repairs can
still produce multiple POSTs. No latency or precision improvement is claimed without a live measurement.

Offline checks cover first-turn completion in one POST on both text routes, exact equality with paginated tool
facts, full target coverage, reference citations versus core coverage, on-demand context facts, request sizes,
and PDF parity for source data, tool definitions/results and the remaining conversation behavior.
