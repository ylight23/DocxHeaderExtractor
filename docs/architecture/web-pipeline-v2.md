# Web pipeline V2

Baseline: `41b84d1`. This is a Web projection change, not an accuracy treatment or a new authority.

## Same-execution data flow

```text
upload bytes → /api/extract → DocumentAgentHarness → PipelineDocumentExtractionTool
  → DocumentExtractionRouter → DOCX/PDF extraction pipeline
  → DocumentExtractionExecutionResult { Result.Structure, Result.SourceCatalog, HeadingPipeline, Outline }
  → final harness attempt (reference-identity checked)
  → WebPipelineProjection → NDJSON result.pipeline → pipeline-v2.js
```

The runtime-only `HeadingPipeline`, `DocumentAgentRunResult.Execution`, and checkpoint observation
sidecars are `[JsonIgnore]`. CLI/MCP outline serialization is unchanged. No second parse, inference,
materialization, hierarchy inference or qualification evidence is used to build the viewer.
The existing independent OOXML human-review snapshot path is unchanged; V2 does not use it.

## API

The result event retains `outline`, `stats`, `review`, `humanReview`, links and `agent`.
It adds `pipeline` with schema version `web-pipeline-v2`:

| Field | Authority / availability |
| --- | --- |
| executionId, outcome, sourceKind | Current harness run and byte detector |
| headings | Emitted, admitted graph elements; structural ID distinct from stable/source IDs |
| headings.sources | Every bound source reference, exact `[start,end)` UTF-16 span, parser text and coordinates from the retained catalog, joined by source ID only |
| relations | Emitted-endpoint `ParentChild` relations admitted in `ValidatedStructure` |
| parentId, parentStatus, children | Admitted relations only; filtered parents remain explicit `parent-not-emitted`, not fabricated roots |
| hierarchyResolution, decision, validation | Producer metadata / admitted validation; not model proposals or calibrated accuracy |
| inlineBody, boundaryEvidence | Producer projection context, unavailable when absent |
| roots, unresolved | Explicit buckets; no tree derived from levels, text, numbering or ordinal |
| summary | Accepted/unknown/review/filtered counts; rejected is unavailable without an actual rejection census |
| stages, checkpoints | Evidence-backed statuses, or `not-recorded` |
| provenance, audit | Whitelisted execution metadata; no provider payload, usage, reasoning, raw SSE or source paths |

`stats.rejected` is retained as a legacy compatibility field, **not** a trustworthy rejection census.
Neither result view uses it to claim rejected headings. Web alone replaces `outline.routeAudit` with a
safe whitelist. Its top-level outline fields and heading wire shape remain; the original full audit
and generic result are not mutated. CLI/MCP retain their existing contracts.

If the same-execution sidecar is unavailable/mismatched, V2 emits `availability=unavailable`,
no graph evidence and no inferred hierarchy. The flat compatibility outline may remain visible,
explicitly not a structural tree. Failed extraction produces an error event with unavailable result
and failed execution observation, never an accepted placeholder heading.

## Stage evidence

Source detection is the Web byte detector; parsing is evidenced by the returned production catalog.
Semantic execution reflects the producer's lane status. A declared protocol is **not** proof that a
call ran: live PDF F1/G2A/H2-C sub-stage status is `not-recorded` because the current result does not
retain individual call observations. Explicit producer `not-run` records support `skipped` instead.
DOCX uses its canonical text semantic lane, not the PDF stage names.

Admitted elements evidence successful exact binding/validation for those elements, not complete
coverage of all source occurrences. Producer hierarchy records evidence placement output. Projection
and final-result are supported by the returned outline and validated harness result. Empty graphs do
not prove that binding or hierarchy was skipped. No fake percentage or synthetic completion event
is added. Harness stages show only received events, and label protocol names as reference contracts.

The live PDF checkpoint sidecar is recorded only after the existing source-selection checkpoint write,
drain and successful lease publication; no path, checkpoint payload or source excerpt is exposed.
No checkpoint is fabricated for DOCX/source-only/failed runs.

Provider-call counts are **producer provenance**, not an independently measured attempt counter.
Raw provider debug callbacks are suppressed in production Web even when the old diagnostic checkbox
is submitted; transport configuration, requests and retries are unchanged. Error messages withhold
exception payloads. The inspector intentionally displays selected source text using inert DOM text.

## UI and verification

`pipeline-v2.js` builds topology from relations only, provides expand/collapse controls and ID-based
selection, and uses `textContent` rather than HTML sinks for all source/evidence values. The inspector
lists all multipart source references; duplicate text/ordinal does not collapse identity. At 720px
and below it becomes a single-column layout. Blind Gold mode continues hiding result panels.

Tests: `WebPipelineV2ProjectionTests`, `WebPipelineV2ApiTests`, existing host/compatibility/byte/lifecycle
tests, plus `node --test tests/web/pipeline-v2.test.cjs`. API tests use in-process fake transports only,
exercise the real DOCX and PDF pipelines, and cover accepted/empty/partial/failure/malformed upload.
The optional resolver seam retains the default host provider policy.

Browser QA uses `tests/web/browser-fixture.html` served separately on localhost (not shipped in Web),
the production JS/CSS from the running Web host, and actual source-only PDF upload. Synthetic fixtures
are labeled and are never presented as production authority or an accuracy benchmark.

Limits: per-protocol live call instrumentation and a reliable rejected-heading census are absent;
they remain explicitly not-recorded. This task makes no accuracy/generalization claim and changes no
prompts, frozen requests, Gold, semantic decisions, binder, provider policy, authorization or retry.
Windows/Ubuntu final verification is recorded separately after CI completes.
