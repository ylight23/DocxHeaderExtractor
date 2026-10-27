# SRC-095 page-packing Luna ablation — 2026-10-26

## Result

The paired request contained pages 47–48 together (44 units, 591,696 bytes). Its frozen Luna response selected none of the three target Appendix headings. The same V12 instruction and system prompt were then used with two source-bound requests, one per page, retaining the same context and document styles and sending the matching page image and geometry evidence in each packet.

| Branch | Request bytes | Target headings selected |
|---|---:|---|
| Pages 47–48 together | 591,696 | 0/3 |
| Page 47 alone | 295,593 | 0/1 (`A.1. Streams` remained missed) |
| Page 48 alone | 271,983 | 2/2 (`A.2. HTTP Frame Types`, `A.2.1. Prioritization Differences`) |

This is evidence that joining pages 47 and 48 may have contributed to the two page-48 omissions in the full-file run. It does not explain the page-47 omission, which persisted when page 47 was sent alone. Because there is one Luna-authored response per branch and the paired response is reused from the full-file run, this is a useful signal, not a repeated-run causal proof.

## Integrity and limits

- Split requests preserve the same V12 and system prompt hashes, full shared context, and document styles. Page geometry and the image are scoped to each page.
- Page 47 packet: 28 units, 46 member aliases, 295,593 bytes. Page 48 packet: 16 units, 37 member aliases, 271,983 bytes. Both are below the 786,432-byte cap.
- All 44 / 28 / 16 unit decisions were present exactly once. Selected aliases were validated against their unit and source text before freeze.
- Three-branch evidence was frozen before reading canonical Gold. See [pre-Gold freeze](pre-gold-freeze.json) and [post-freeze score](post-freeze-gold-score.json).
- The direct provider preflight returned HTTP 402 and was excluded. Split responses were authored directly by a fresh Luna agent, matching the response-authoring method of the original Luna bridge run; this ablation does not measure provider latency or an external SLA.
- No production code, prompt, or route was changed for this experiment.
