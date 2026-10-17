# Grouping disagreement and selection review

Production change, 2026-10-17: `group-review-v1` is appended to all three selector IDs
(scan, text PDF and DOCX). The user's heading instruction is unchanged.

The subsequent [independent group verification](grouping-verification.md) extends this flow:
all accepted proposals are checked, and membership disagreements from verification can also
request selection review. Selector IDs additionally include `verified-groups-v1`.

Grouping may return a normal division or an explicit disagreement for each run:

```json
{
  "protocolVersion": "PDF_HEADING_GROUPS_V1",
  "runs": [{
    "run": "R14",
    "dispute": {
      "aliases": ["P015-L014", "P015-L015"],
      "reason": "These lines appear to be body text; reconsider selection."
    }
  }]
}
```

The aliases must cover the entire run in order; the reason must contain 1–1000 characters.
`headings` and `dispute` are mutually exclusive. Empty headings remain invalid. The repair
message explicitly offers disagreement instead of asking the model to invent headings.
C# validates this protocol; it does not interpret the reason or classify document text.

Each affected original segment gets one new selection conversation (`REVIEW-SEG-*`).
It receives the original source context, core facts, unchanged instruction, previous
selection outcome and model-reported disagreement. These model claims are distinguished
from source facts. The usual tools, reading and extent validation are available.
Only decisions for disputed aliases replace previous decisions; other aliases stay unchanged.
Scan reading choices and extents are replaced together with reviewed membership.

Successfully reselected lines get at most one regroup (`REVIEW-GROUP-*`); accepted groups
outside the disagreement are preserved. There is no recursive review. A failed/incomplete
review or repeated disagreement remains unresolved: those aliases are withheld from emitted
headings, not declared body text. Their original segments cease counting as complete and the
result records the failure. Other usable headings remain available.

Each review conversation is capped at eight POST attempts including repairs and retries.
Every attempt uses the shared budget; cancellation still prevents publication. The default
budget reserves room for review and one regroup, but no extra request is sent without a
grouping disagreement. This is reserved capacity, not a predicted charge.

`HeadingSelection.ModelAnswers` retains grouping evidence (unit/turn, request SHA-256,
visible response and parser failure), previous and reviewed selection outcomes (including
decisions, reasons, extents, readings and request hashes), and regroup evidence. Production
retains these in `routeAudit.rawModelResponses`; the standalone scan JSON includes
`ModelAnswers`. The Web's safe audit projection continues to omit raw model responses.
No private reasoning field is parsed to make a decision. Evidence is retained with the
result; this change does not introduce a separate automatic disk archive.

Offline scripted tests cover rejection, reaffirmation, partial reselection, failed/invalid
reviews, exhausted budget, repeated disagreement, malformed dispute scope, and scan
reading/extent replacement. They establish protocol behavior, not Qwen accuracy on 011.
