# S3: heading runtime envelope versus structural authority

Baseline: `2656dfb`. S2 GitHub deterministic CI completed successfully before S3 began:
[run 37658426771](https://github.com/ylight23/DocxHeaderExtractor/actions/runs/37658426771).

## Boundary

`StructuralAuthorityResult` is retired, replaced by `HeadingPipelineResult`.
Only its `Structure: ValidatedStructure` is structural authority. ProjectionContext,
SourceCatalog, Audit, EmittedElementIds and Reason carry runtime/output responsibilities.
The existing Authority folder owns audit/result contracts; the rename does not introduce
an extra namespace/folder or violate the exact Pipeline orchestration allowlist.

All DOCX/PDF producers, extraction consumers, quarantine and tracked replay/containment
fixtures use the new type. No old compatibility alias or duplicate graph is added.
ProjectionContext retains its existing C# name and JsonIgnore; SourceCatalog/Audit remain
nullable for empty/failed lanes. No additional source parse or projection reconstruction occurs.

## Compatibility

The public C# type rename is an intentional source/binary API break: callers must migrate.
Positional constructor shape, record-with/quarantine behavior, property order, default/Web
JSON names and omission of projection context remain unchanged. No serialization type
discriminator existed on this envelope.

Before the rename, an offline fixture was serialized using the actual 2656dfb envelope
(empty graph/catalog, non-null audit, emitted IDs, non-empty ignored projection context):

| Serializer | SHA256 |
| --- | --- |
| Default | `1ecae4e686e314640040d7af478b8c848871647d365c8c233e0bb34ab113b7f1` |
| Web | `f746783dbbb886c47510330a2a492cdeb9e1e9a0023c770044d21db7a699e672` |

Tests preserve those bytes and the existing S1 graph/S2 outline/grounding hashes. These new
fixtures do not rebaseline historical artifacts. S2's intentional raw-graph/API removals
remain as documented; S3 does not claim to undo them.

StructuralProposalValidator stays in Materialization. ParentId/OutlineElements compatibility
views, semantic prompts/schema, provider transport, Gold, frozen artifacts and runtime
heading decisions are unchanged. S4/S5 are not implemented in this checkpoint.

## Verification

Verified in the isolated tracked verification checkout with S1/S2 plus S3 changes.
User H3 edits and local untracked research are excluded.

- Release solution build: PASS, 0 errors; test compilation reports existing warnings.
- Focused envelope/structural/projection/materialization/replay/containment/host E2E/
  PDF lifecycle/architecture/byte-parity/product suite: 112/112 PASS, 0 failed/skipped.
- TRX SHA256:
  `0f9b8cb6302ac02666cf56e16d19c3c520689b3d895e8448638ffcf61836eff4`.
- git diff --check: PASS.
- Full Windows suite and GitHub CI have not been rerun for S3. The green CI referenced
  above belongs to S2, not this change.
- Provider calls: 0. Gold mutation: 0. Frozen artifact rebaseline: 0.
