# P7-F1Q — independent semantic question experiment (PRE-EXECUTION)

Status: DESIGN_ONLY. Provider calls = 0. Raw response files = 0. This branch is separate from production; do not merge or rewrite historical D3 receipts.

## Research question
Can an independent, source-grounded question about an occurrence's function **relative to the user's goal of extracting document headings** reduce semantic errors without coupling F1 to grouping, anchors, candidate selection, hierarchy or exact spans?

Keep three semantic functions:
- ESTABLISHES_STRUCTURE: source wording contributes to a real document title or substantive heading operating **here**.
- REPRESENTS_STRUCTURE: source wording lists/refers/navigates to a heading operating **elsewhere**.
- OTHER: neither above; a substantive judgment, NEVER a fallback for unavailable evidence.

An epistemic `INSUFFICIENT_EVIDENCE` assessment is distinct from these three semantic labels: function=null, assessment=INSUFFICIENT_EVIDENCE. No forced OTHER. Its incidence and evaluator coverage must be reported explicitly.

## Independent system question (treatment F1Q-v1)
You are an independent document-semantic analyst. The user's intent is to extract the document's headings, including a title identifying the document and titles introducing substantive content regions.

A source occurrence is a parser-grounded piece of text, NOT necessarily a heading, paragraph, line, sentence, or complete title.

For **each issued occurrence**, answer: *What semantic function does this occurrence actually serve relative to the requested document headings, on the evidence supplied from this source?*

First distinguish directly observed source facts from interpretations. Consider the occurrence in document context. Decide whether it contributes wording to a heading operating at this source location, represents a heading operating elsewhere (e.g. contents/navigation), or performs another function. Do not confuse organizational metadata, administrative letterheads, or local data labels with document headings merely because these organize content. A heading inside a table may still be genuine; table membership, boldness, capitalization, alignment, numbering and prominence are not conclusive. Identical words may serve different functions at different locations. A heading can span multiple occurrences; classify each contributing occurrence independently.

Never invent source text, page context, evidence references, structure, relations or authority. If the evidence does not distinguish plausible functions, mark INSUFFICIENT_EVIDENCE, give missing evidence kinds, and leave function null; do not guess. Do not decide where a heading starts, which occurrences group together, where it ends, or its hierarchy. Do not report private reasoning traces; a short checkable semantic interpretation is sufficient.

Return one JSON object containing exactly one record for every selectable issued occurrence. Use only provided stable IDs. Output function must be one of the three listed above when assessment=SUPPORTED, otherwise null. Include a concise observed-role description, strictly source-grounded evidenceRefs, and a short interpretation; do not echo whole source evidence/geometry. Context-only occurrences may be cited but may not become output decision subjects.

## Minimal treatment response contract (proposal)
```json
{
  "protocolVersion": "P7_F1Q_V1",
  "decisions": [
    {
      "occurrence": "O1",
      "assessment": "SUPPORTED",
      "function": "OTHER",
      "observedRole": "Issuer metadata, if supported by evidence",
      "evidenceRefs": ["L0000:S0"],
      "interpretation": "Names issuing institution, not document title wording",
      "missingEvidence": []
    }
  ]
}
```
Possible assessment: SUPPORTED | INSUFFICIENT_EVIDENCE. With INSUFFICIENT_EVIDENCE, function=null, missingEvidence nonempty. Source aliases above are SCHEMA EXAMPLES only, not actual evidence. EvidenceRefs must resolve to issued/source catalog and observations must be supported. Reject hallucinated references. Store raw response even when contract validation fails, never repair/overwrite.

## Planned diverse corpus (exact source IDs / hashes must be frozen before provider)
- D01–D05: five existing P7 adjudicated PDFs (event invitation, program/schedule, form/table, TOC and institutional/report variants as actually documented in inventory; verify actual types and do not assume these labels).
- SRC-089: government decree with multi-occurrence headings and front-matter negatives.
- SRC-095: table of contents with REPRESENTS_STRUCTURE positives.
- Additional documents (contracts, financial reports, textbook, meeting minutes, generated software reports) only if source PDFs and independently reviewed labels actually exist; otherwise reserve as UNAVAILABLE, not fabricated.
- Include hard negatives: issuer metadata, national motto, page furniture, table column labels, object captions; hard positives: document identity, subtitle, multi-line heading, genuine heading inside table; navigation positives: TOC. These are review strata, NOT rules or model-visible Gold.

## Paired experiment and validity
- Control: historical F1 frozen prompt/inputs, reused raw where request bytes match exactly, otherwise freshly collected under separately frozen authorization.
- F1Q treatment: identical selectable occurrence universe and same source text/context as Control; isolate question effect FIRST. A different evidence/tool/Vision payload is a **separate arm**, not pooled into F1Q.
- Freeze source SHA, pack IDs, O#→sourceAlias mapping, model + route, transport envelope, system/user prompt bytes and hashes, cap, ordering, attempt plan, intended budget and response schema before opening Gold.
- Do not feed Gold, reviewer notes, label-derived categories, previous predictions, or any adjudication to provider.
- Never silently treat abstentions or failed responses as OTHER; report valid coverage, abstention coverage, strict contract acceptance and semantic score separately.
- No retries/repair unless explicitly preauthorized and budgeted; attempts retain raw transcripts, exact body, raw SSE, finish status, usage and provider cost receipts.
- Never alter the executed frozen historical D3 experiment. Provider authorization for this new paid experiment is separate.

## Raw capture destination (after actual provider execution)
`artifacts/web-pdf-semantic-diagnostic/p7.f1q.independent-semantic-raw.v1/<case>/<attempt>/`
Must include `request.json` (or exact original body bytes), `response.txt`, `response.sse`, `observation.json`, `attempt-receipt.json`, `raw-freeze.json` and an execution manifest documenting sha256, universe, mapping and tool evidence provenance. Preserve failed / incomplete attempts unchanged, do not claim hidden model thinking; report only generated interpretations and exposed provider metadata. Scan for secrets and personal information before any public push.

## Frozen offline evaluation (only AFTER immutable response capture)
1. Independent source review and evidence-grounding audit of interpretation assertions: SUPPORTED / UNSUPPORTED / NOT_ASSESSABLE, with source aliases.
2. Exact ID mapping by frozen pack manifest; reject missing/duplicated decisions and cross-pack O# confusion.
3. Score only independently adjudicated Gold occurrences; UNKNOWN remains excluded, never scored as OTHER.
4. Three-class confusion matrix; ESTABLISHES precision/recall/F1; REPRESENTS score; per-document slices, hard-positive/negative changes, abstention coverage; paired change ledger with original raw response citations.
5. Separate transport, schema/cardinality, source-reference, semantic and calibration errors.
6. Do not feed F1Q into G2/H2 or claim production improvement in this experiment.

## Execution gates
DESIGN_ONLY until code implementation, offline dry-run verification, exact source inventory / hashes, capped request/attempt/cost budget, and provider authorization are recorded. No evidence of a new provider run or semantic improvement exists at design time.
