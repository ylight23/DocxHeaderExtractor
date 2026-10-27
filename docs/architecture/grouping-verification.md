# Group verification: qualification-only

Updated 2026-10-19. The verifier-as-authority flow documented in the earlier history of this
file is no longer part of production heading binding. Experiments showed that an independent
semantic verifier can disagree with source-correct grouping, oscillate across repair cycles, and
both falsely accept and falsely reject boundaries. Adding more verifier/repair passes did not
provide a reliable authority.

Production now follows:

1. Selection returns selected source aliases.
2. The grouping model returns contiguous ordered groups.
3. C# validates protocol shape, exact run coverage, alias ownership, contiguity and source order.
4. Only validated groups are bound. Grouping has no semantic dispute response; malformed or
   incomplete protocol/transport results remain unresolved after bounded repair and do not rewrite
   selection membership.

DOCX and native PDF grouping receive the selected heading span, rather than treating the entire source line as the
boundary target: `text` and `selectedText` contain the selected substring; `headingExtent` gives its
UTF-16 offsets in the original `sourceText`, which is context only. Without an extent the whole
paragraph remains selected. The grouping request hash (and cache key) includes these fields.
Native PDF selection now uses the extent-enabled answer contract (the legacy objects-only contract
remains for historical comparisons). Native PDF and DOCX selector IDs carry `group-selected-text-v1`.
Scan grouping also receives `readingViewId`: `sourceText` is that selected reading, and the extent
is measured in that reading, never in an alternative OCR text. Missing reading choices use the same
default reading as selection and binding. The grouping cache key includes reading choices as well
as extents. Scan selector IDs carry `group-selected-reading-text-v1`. Original geometry remains the
source line's geometry; no substring box is invented. All routes reject invalid extents rather than
widening them or switching readings. No extent means the whole chosen source representation.

Binding preserves the model's partition across languages. It does not split groups by chapter/article
keywords. The `f024246` structural-marker override has been removed. This selected-text change fixes
an evidence handoff, not a guarantee that model-generated partitions are semantically correct;
009 needs a fresh measured run to assess boundary quality. No additional semantic verifier is added.

`AgentGroupVerification` and the historical canary artifacts remain available for qualification
tests and analysis. Their verdicts, boundary suggestions, cycle observations, and ACCEPT states
must not be used to authorize or veto production groups. Strict source identity and protocol
checks remain in force; this rollback removes semantic verifier authority, not deterministic
validation.

## Native PDF default after the 2026-10-24 qualification

The application profile now uses `PDF_HEADING_BOUNDARIES_V1`: the grouping model returns one
source-bound JOIN/SPLIT verdict for every adjacent selected pair, rather than only complete groups.
Each pair echoes its own aliases and selectedText; C# checks exact source/coverage/order, then folds
the model decisions and runs the established group-size/contiguity checks. The request adds the
existing measured block/style facts, document style table and three source context lines per side.
Facts never label a boundary, and no C# keyword split or semantic verifier authority is introduced.
Malformed pairs receive bounded protocol repair; exhaustion remains fail-closed as before.

`DocumentRunner` uses V14 selection units with the V12 membership instruction, measured page images
(`PdfAdaptiveVisualSelection`, request ceiling 786,432 bytes), source binding and pairwise grouping for
native PDF by default (owner, 2026-10-26; see the [V12/V14 cohort](../qualification/pdf-request-optimization-20261026.md)).
For the previous profile set `PdfMembershipInstructionVersion = 12`, `PdfAdaptiveVisualSelection = false`
and `PdfVisualSelectionMaxRequestBytes = null`.
Parent-chain repair is scoped with `PdfRepairParentChains`; the shared DOCX/scan placement policy
continues to use the explicit `RepairParentChains` option. Host-provided selectors/placers keep
precedence. Low-level `PdfSelector` constructor defaults retain the legacy wire contract for
explicit callers and historical comparisons. Qualification modes set their own flags, including
the repair scope, so a frozen baseline does not silently inherit new application defaults.

For a legacy application profile, explicitly set `PdfSourceBoundSelection`, `PdfClarifyMembership`,
`PdfSeparateGroupingInstruction`, `PdfPairwiseGrouping` and `PdfRepairParentChains` false, with
`PdfMembershipInstructionVersion = 2`. Optional inline grouping, whole-document placement and
membership review remain off by default. See the [qualification record](../qualification/pdf072-membership-v6-pairwise-20261024.md)
for all failed revisions, actual measured quality/latency and the remaining PDF072 metadata extra.
