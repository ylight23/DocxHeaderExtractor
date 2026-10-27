# PDF072 Gold correction and route prompt audit — 2026-10-25

> This prompt table records the original route audit. The V7 general prompt was tested
> in controlled LLM trials but did not pass the PDF072 gate, so the qualified V6 defaults
> remain active. See [general prompt trial](pdf-general-structural-prompt-20261025.md).
> Gold correction and rescoring remain valid for the saved V6 outputs.

## User-approved Gold correction

The user confirmed that the first title is **MINUTES OF THE INTERNATIONAL COMPARISON
PROGRAM TECHNICAL ADVISORY GROUP**, composed of `L0000:S0 + L0001:S0`, and explicitly
instructed correcting Gold. The previous reference omitted the second title component;
calling this component extra metadata was incorrect under this approval.

Only this title's source membership and corresponding wording/identity were amended.
The total remains 42 headings; selected aliases increase from 44 to 45. The other 41
semantic and occurrence claims are unchanged. This is not a fresh full-document review.
The current canonical reference, its structured occurrence authority and registry hash
are synchronized. Historical legacy freezes, run outputs and old score files are preserved.

- Approval: `eval/a99-closed-loop/gold-current/corrections/DOC-0252-title-composite-20261025.json`.
- Active reference: `eval/a99-closed-loop/gold-current/documents/DOC-0252.gold.v1.json`.
- Old Gold SHA-256: `a26b83e14005a9a3e4957cd7a027155e97a534a73fd737551726d18a1c695654`.
- New Gold SHA-256: `e966d4e57ba6b312a2cebe93446c4350d4ef7173458f7c528306775f9c743b03`.
- Before snapshots, amendment and new scores: `artifacts/qualification/pdf072-title-gold-correction-20261025/`.

| Saved V6 run | Alias membership | Exact alias groups | Original elapsed time | Unresolved parents |
|---|---:|---:|---:|---:|
| full-01 | 45 TP, 0 FP, 0 FN | 42 TP, 0 FP, 0 FN | 236.614 s | 0 |
| full-02 | 45 TP, 0 FP, 0 FN | 42 TP, 0 FP, 0 FN | 248.588 s | 0 |

These are rescored existing outputs, with **zero new model calls**. Exact alias groups
do not establish character-span or hierarchy correctness: those Gold capabilities remain
unchanged. In particular, zero unresolved parents is a runtime observation, not a Gold
parent-accuracy score. No universal five-minute SLA follows from these samples.

Reproduce amendment checks and rescoring:

```powershell
python tools/qualification/check_pdf072_title_correction.py
```

This validates all 23 active registry Gold hashes, preserves all other authorities,
checks the other 41 claims and the structured occurrence record, and writes fresh scores
into the correction folder. It does not overwrite historical experiment scores.

The extended provenance check also records one inherited mismatch: the old
`SEMANTIC_AUTHORITY` citation records hash `8c5a1c92…`, while that historical freeze
file currently hashes to `0f0c602e…`. The same citation was present before this title
amendment. It is retained and reported rather than silently rewritten. All 23 active
registry hashes, the amended structured authority and the new approval/snapshot citations
validate; rescoring uses the current registered canonical Gold.

## Prompts and runtime wiring checked

The new native-PDF profile is already the application default. **The PDF-specific
changes have not been promoted to DOCX or scan.** The existing common V6 agent name
does not mean that the three routes use identical instructions or protocols.

| Route | Selection instruction currently used | Grouping currently used | Source contract |
|---|---|---|---|
| Native PDF | `PdfMembershipInstructionV6`: structural core + PDF extents + V4 local titles/labels + V5 attributes + V6 participant subgroups | Separate `PdfBoundaryInstruction` plus `PdfBoundaryDecisions.Prompt`, JOIN/SPLIT for every adjacent pair | PDF aliases/segments, exact sourceText echo, layout and typography facts |
| DOCX | `DocxAgentProtocol.Instruction`: structural core + formatting evidence + exact paragraph extent + measurements are not decisions | `GroupingPrompts.Docx`, contiguous heading arrays | Paragraph aliases, styles, numbering, selected UTF-16 span in original paragraph |
| Scan | `ScanAgentProtocol.Instruction`: DOCX instruction + repeated boilerplate rule | `GroupingPrompts.Scan`, contiguous heading arrays with scan guidance | OCR/image evidence, listed readingViewId, span in the chosen reading, image geometry |

Wiring evidence: `App/HeadingAgentSetup.cs` constructs the three selectors separately and
uses a separate PDF placer when PDF parent repair is enabled. `PipelineOptions` enables
the PDF selection/grouping profile. `DocxSelector` still resolves its own default;
`ScanSelector` passes its own instruction to `AgentSelector.Select` without enabling
pairwise PDF grouping. Host-supplied agents and explicit custom instructions retain precedence.

Relevant prompt sources under `src/DocxHeaderExtractor.HeadingAgent/`:

- `Protocol/AgentProtocol.Prompts.cs`: shared structural definition, all levels and contributing title lines.
- `Protocol/PdfMembershipInstructionV4.cs`, `Protocol/PdfMembershipInstructionV5.cs`, `Protocol/PdfMembershipInstructionV6.cs`: the three appended PDF membership sections.
- `Protocol/DocxAgentProtocol.cs`: DOCX evidence and paragraph extent rules.
- `Scan/ScanAgentProtocol.cs`: OCR readings, UTF-16 extent in chosen reading, no declared scan font styles.
- `Grouping/PdfBoundaryDecisions.cs` and `Protocol/PdfBoundaryInstruction.cs`: PDF title composition and independent-unit boundaries.
- `Grouping/GroupingPrompts.cs`: current DOCX and scan grouping prompts and modality-specific evidence.

## What can be shared, and what needs adaptation

**The semantic criteria can be shared as a candidate common instruction section.**
The useful V4–V6 rules are not inherently PDF-only:

1. Include all contributing parts of main and embedded-document titles. Program or
   organization names can be title components when their local function establishes that.
2. Distinguish a unit's title from date, venue, address or meeting-format attributes.
   A dated day/session divider still qualifies when it introduces its own content unit.
3. Evaluate every participant subgroup label independently. Individual people and their
   affiliations remain entries/metadata; a label introducing a following subgroup can be a heading.
4. For grouping, distinguish completion of the same title from a separately named content
   unit. Parent/child containment, matching style or adjacency alone must not merge headings.

DOCX and scan currently have the broad structural definition, but **do not inherit the
explicit V4–V6 local-title/attribute/subgroup clarifications**, nor the PDF pairwise prompt's
document/program/committee title-composition guidance. This is an instruction difference,
not proof that they have the same observed failure. No DOCX/scan model-quality experiment
was performed in this audit.

Keep the source contracts separate. Copying the complete PDF prompt would tell DOCX/scan
to use PDF-specific evidence and output fields. For scan, a font/style reference inherited
from the generic DOCX evidence paragraph should be phrased as “available measured evidence”
in a future candidate; the scan system already states that a scan declares no font styles.
Do not discard OCR reading choice, UTF-16 offsets, paragraph extents or boilerplate checks
when adding the common semantic criteria.

**Pairwise grouping requires code adaptation as well as prompt text.**
`PdfBoundaryDecisions.Enrich(AgentSource, ...)` reads PDF layout facts and document styles;
the opt-in grouping path is PDF-specific. DOCX needs paragraph/style/span evidence and
scan needs chosen-reading/geometry evidence. A generalized implementation would retain
exact alias/text checks and complete boundary coverage, while obtaining evidence through
each route's adapter. Changing a string or a PDF flag alone does not accomplish this.

Before promoting either route, qualify the candidate against its approved references:
composite titles, adjacent independent parent/child headings, embedded titles versus
metadata, repeated participant subgroup labels, DOCX heading/body in one paragraph,
and scan OCR reading/extents/repeated boilerplate. Check complete selection, exact groups,
source binding, parent consistency and full-run latency per route. Existing PDF timings
and offline regression tests do not substitute for this evidence.

Finally, “scan” here means the existing scan selector over image/OCR evidence used by the
scanned-DOCX route. An image-only PDF currently returns `pdf-no-text-layer` in `PdfLane`;
sharing prompts does not add a PDF-to-OCR route. A scanned PDF with a usable text layer
can instead enter the native PDF lane through that text layer.

This change corrects Gold and documentation and adds a reproducible validation/rescoring
script. It does not change inference prompts or DOCX/scan application defaults.
