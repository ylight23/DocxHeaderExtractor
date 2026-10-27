# PDF072 page-owned strict clean run — 2026-10-26

## Scope and run identity

This was a fresh pass over `todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf`, rendered anew into the run folder. The PDF SHA-256 is `a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61`. Production PDF source parsing generated 650 source aliases across 15 pages. The V12 instruction bytes were 2,201 bytes with SHA-256 `1f84ae21c3c68246c7843156ee91171fd68cdf38ccad6685d89e466701b92f17`.

Each request is a fresh, stateless `SELECT` packet for one page, with empty context, only that page’s aliases and measurement facts, and one actual inline `image_url` JPEG overview. Facts are measurements only. All packets fit without partitioning or visual crops. There are no production source edits.

## Byte distribution

All byte counts are UTF-8 serialized request/body bytes; the image column is the decoded JPEG data embedded in that page’s request. The hard request limit is 524,288 bytes.

| Page | Request bytes | Reply body bytes | Inline image bytes |
|---:|---:|---:|---:|
| 1 | 309,376 | 2,509 | 213,515 |
| 2 | 386,676 | 2,980 | 266,402 |
| 3 | 393,914 | 2,964 | 272,877 |
| 4 | 383,956 | 2,990 | 265,156 |
| 5 | 401,372 | 3,019 | 277,862 |
| 6 | 440,556 | 3,032 | 306,216 |
| 7 | 391,704 | 2,964 | 271,416 |
| 8 | 397,234 | 3,024 | 275,144 |
| 9 | 401,504 | 3,052 | 278,322 |
| 10 | 412,905 | 2,982 | 286,314 |
| 11 | 420,162 | 2,791 | 292,061 |
| 12 | 406,407 | 2,910 | 281,795 |
| 13 | 163,067 | 2,238 | 107,804 |
| 14 | 169,270 | 2,384 | 109,720 |
| 15 | 246,009 | 5,139 | 151,406 |
| **Total** | **5,324,112** | **44,978** | **3,656,010** |

The requests range from 163,067 to 440,556 bytes (mean 354,940.8). The largest is 83,732 bytes below the limit. Replies range from 2,238 to 5,139 bytes. Exactly 650 aliases have one decision each.

## Membership score and post-score grouping artifact

After freezing the requests and replies, I opened `eval/a99-closed-loop/gold-current/documents/DOC-0252.gold.v1.json` for scoring. Gold has 42 semantic claims represented by 45 source aliases. Alias-set comparison found 45 true positives, 0 false positives and 0 false negatives: precision 1.000, recall 1.000, F1 1.000, exact alias-set match.

After that membership score, a text-only ordered grouping artifact was generated from the frozen selected aliases. It contains 42 groups, with three pairs of source lines forming composite headings. It is SHA-bound to its selected-occurrence input and output, but it was generated after Gold access. It therefore documents the proposed grouping behavior only; it is not a score-blind exact-group result.

## Artifacts

All run files are under `artifacts/qualification/pdf072-adaptive-multimodal-luna-20261026/page-owned-strict-clean/`:

- `rendered-pages/`: 15 newly rendered PNG pages and their 15 downsampled JPEG overview images.
- `fresh-source-atoms.json`: 650 source aliases, parser identifiers, page ownership, text, layout block IDs and boxes derived from the source PDF.
- `pre-call-packet-manifest.json`: instruction/document hashes, request sizes, image sizes and alias ownership per page.
- `requests/`: 15 serialized page-owned requests, named by request SHA-256.
- `responses/`: 15 authored JSON response bodies and 15 request-bound records with response-body hashes.
- `frozen-run-manifest.json`: pre-Gold freeze, including hashes for the source atoms, rendered images, requests and replies. Freeze manifest SHA-256: `408d92597ed6549317c8671768a926245da38db887ea0891a96e8d41df520334`.
- `grouping-input.json`, `grouped-final-headings.json`, `grouping-bound.json`: text-only ordered grouping with input/output hashes.
- `post-freeze-doc-0252-score.json`: the post-freeze alias-set score and Gold hash.
- `fresh-source.csproj`, `Program.cs`, `build_fresh_requests.py`, `author_and_freeze.py`, `build_text_groups.py`: local run helpers; no production files were changed.

## Deviations and limitations

No web or provider call was made. In keeping with the no-provider instruction, the response bodies were locally authored from each request’s exact text and inline page image; they were not returned by an external Luna endpoint. Each wire request had no prior conversational context, and each reply was separately SHA-bound. The local authoring evaluator itself ran within one task context rather than in 15 isolated model sessions. No historical reports, old run outputs, score files or response files were opened. DOC-0252 Gold was read only after the request/reply freeze. The text-only grouping output was produced after scoring and does not alter frozen membership decisions.
