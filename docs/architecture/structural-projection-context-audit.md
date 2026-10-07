# S2: projection context outside structural authority

Baseline: `d1f6641`. No provider calls, Gold mutation or frozen artifact rebaseline.

## Ownership

- Core ValidatedStructuralElement no longer contains ProjectionMetadata.
- Core SourceReference no longer contains outline-compatible StableId.
- HeadingProjectionMetadata and HeadingProjectionContext belong to DocumentProcessing/Projection.
  Context snapshots metadata by structural element ID and stable IDs by (element ID, source ID).
  It grants no semantic/source/span/hierarchy authority and is not model-visible evidence.
- Materialization returns graph plus sidecar; the assembly and StructuralAuthorityResult carry
  both through DOCX/PDF. The context property is JsonIgnore, not a new audit/wire field.
- HeadingOutlineProjection and CanonicalGroundingProjection explicitly read the sidecar; the
  CanonicalGrounding DTO remains passive and does not import projection services. Quarantine prunes
  sidecar entries by surviving element IDs and still accounts for compatibility source IDs.
- StructuralProposalValidator does not accept or stamp output metadata. The exact source spans,
  role/type gates, parent relation authority and hierarchy behavior remain separate.

## Compatibility boundary

HeadingRecord, grounding/writeback identity and product output preserve existing values. An
offline fixture with every output override (including intentional null level, different source
and stable IDs, inline body/span, original text and style) was serialized before S2 using d1f6641:

| Payload | Default and Web serializer SHA256 |
| --- | --- |
| Heading outline | `8432cf34f1c32282ca148a2028c6d016ce78514705632ec829509efb180b7d86` |
| Canonical grounding | `7ea2c6ae1779fb3205ac6eda1cae5f71596754a5330971670000d7dbc7da12e9` |

These are new pre-change compatibility fixtures, not historical artifact rebaselines.
Existing product serialization, frozen DOC-0256 replay, host E2E and lifecycle tests are retained.
The null-metadata/null-stable-ID heading graph fixture from cb2246b retains its original hash.

Raw Core graph serialization intentionally no longer emits non-null ProjectionMetadata or
source stableId fields. Consequently the nested `structure` of DocumentExtractionResult changes
where those fields previously appeared; this is not blanket generic-envelope byte parity.
Public consumers of the removed properties, former materializer metadata parameter or
CanonicalGrounding.FromValidatedStructure must
migrate. Consumers that need outline/grounding compatibility must pass the producer's sidecar,
not reconstruct it by title matching or repopulate Core DTOs. Context is runtime-only and is not
an archive/replay format; standalone graph serialization cannot reconstruct it.

No hidden global registry, duplicate authority graph, legacy inference route, prompt change,
layout heuristic, or semantic decision is introduced. S3 ObservedEvidence retirement was already
handled by 998a371. Full-suite qualification is not implied by focused tests.

## Verification

Verified in an isolated tracked checkout with the d1f6641 implementation plus S2;
user H3 edits and untracked research files are excluded.

- Release solution build: PASS, 0 errors (incremental final invocation 0 warnings;
  preceding test compilation still reports existing warnings).
- Focused S2/structural/output/materialization/architecture/parity/lifecycle/product/
  frozen-replay/host-E2E suite: 92/92 PASS, 0 failed/skipped. Six new S2 cases cover
  baseline bytes, identity scope, snapshots, quarantine, multipart identity and ownership.
- Final focused TRX SHA256:
  `28240cc54247b32b980d3c29329eb6373a249fd374afdd2ecf595bf8bd103d71`.
- git diff --check: PASS. Full Windows suite/GitHub CI not rerun for S2.
- Provider calls: 0; Gold mutation: 0; frozen artifact rebaseline: 0.
