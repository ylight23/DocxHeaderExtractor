# P7 pilot request freeze (qualification only)

This is a source-only, provider-free F1 preflight for the approved five-document /
six-page development pilot. It is not a full-corpus or held-out qualification and
does not authorize inference or production promotion.

Inputs are immutable full-source snapshots and the original source-only pilot
selection. Only approved page membership selects packs: build the exact current
production P05 partition for each full PDF, then retain every pack with an owned
atom on an approved page. Do not clip owned atoms, halo context, correspondences or
source facts. Atoms outside approved pages remain unknown/unscored, never negative
Gold. Approval / Gold / scorer hashes are linked as metadata outside provider
messages; Gold rows and interpretation sidecars are not read.

Arms:

- CONTROL: current production F1 protocol/composer/provider carrier.
- B: the existing `P7_B_INTERPRETATION_RECORD_V2` composite treatment (parser-owned
  evidence, source references, short interpretation envelope). No new prompt,
  ontology, page-context treatment, relation predicate or engineered feature.

Every pair shares the exact stage input and issued occurrence identities/order.
All provider envelope fields except message contents are equal. This is not a
claim of byte parity to a historical response capture; provider bodies are new
immutable preparations of current production controls.

F1 bytes can freeze before inference. G2A and H2-C cannot: their issued subjects
depend on upstream decisions. The pinned generation recipe uses **the same frozen
CONTROL upstream ledger for both arms**, making downstream comparisons stage-
isolated, not a treatment-driven end-to-end cascade:

1. Separately authorize, capture and freeze control F1 outputs, then validate the
   complete ledger. Only `ESTABLISHES_STRUCTURE` becomes a G2A primary.
2. Generate/freeze both G2A arms before any separately authorized G2A execution.
3. Capture/freeze valid control G2A outputs. Only `HAS_STRUCTURAL_EXTENT` becomes
   an H2-C anchor; generate/freeze both H2-C arms before separate authorization.

Empty valid upstream output issues no downstream calls. Missing/invalid output
is not empty, and causes fail-closed gating. No Gold filtering, retry, repair,
fallback or automatic authorization. A pinned recipe is **not** frozen downstream
request bytes. Their counts remain unknown, not asserted as zero planned calls.

Private output includes prompt/user/body bytes, issued-universe maps, raw source
stores, manifests and recipe. Public output contains hashes/counts/aliases only;
no source excerpts. Existing destinations are rejected. Re-run into new outputs
to verify determinism. Exact tokenizer mapping and usage measurement remain
blocked; UTF-8 byte counts are not token counts or a proven token upper bound.

```powershell
dotnet build scripts/P7PilotRequestFreeze/P7PilotRequestFreeze.csproj -c Release --artifacts-path .verify-build/p7-request-freeze
dotnet .verify-build/p7-request-freeze/bin/P7PilotRequestFreeze/release/dhx-v5-qualify.dll <full-source-pack> <source-only-pilot-pack> <new-private-dir> <new-public-manifest>
```

The old NOT_FROZEN placeholder, approved Gold, scorer and all existing frozen
production/research requests remain unchanged. D2.3 stays OPEN for downstream
bytes and other pending execution gates. No HTTP client/transport or credentials
are used by this script.
