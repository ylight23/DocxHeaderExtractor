# V4R3 FIXED120 medium matched control

Status: `PROVIDER_FREEZE_ONLY_PROVIDER_CALLS_NOT_AUTHORIZED`

This is a provider-free preflight for the causal control arm. It does not
modify P05, open Gold, change production code, or enable owner fallback.

## Single treatment delta

```text
RESOURCE_BOUNDED_SOURCE_PACKING_V1
    -> FIXED_OWNED_COUNT_120
```

The model, Alibaba route, `reasoning=medium`, prompt/schema/facts/binder,
`max_tokens=12288`, 300-second timeout, `json_object`, disabled cache control,
non-strict schema posture, lossy-transform posture, recovery policy, Gold gate,
and scorer remain pinned to P05.

## Frozen control envelope

```text
primary planned calls       = 25
primary input bytes         = 2,129,674
owned atoms per full request= 120
visible atoms maximum       = 160
halo                        = 20 (fixed policy)
observed/max request bytes  = 93,856
estimated input ceiling     = 16,000 tokens
completion reserve          = 12,288
total hard call cap         = 55
recovery attempts           = 12
adaptive child calls        = 12
providerCalls               = 0
goldRead                    = false
```

The 93,856-byte ceiling is the deterministic maximum observed in the frozen
fixed-120 request census; it is a packing-derived control envelope, not a
prompt, model, or transport change.

## Gate order

1. Run the matched control to final source coverage using the frozen recovery
   policy.
2. Freeze its lineage and only then open Gold for offline scoring.
3. Compare P05 and control on source-level F1, FP classes, extent errors,
   owner omission, calls, bytes/tokens, saturation, retries, splits, latency,
   and final coverage.
4. Keep owner fallback, semantic classification/prompt, and extent-specific
   arms separate and downstream of this comparison.

Artifact: `v4r3-fixed120-medium-matched-control-preflight.v1.json`.

The provider-free composer check passed: all 25 semantic request hashes,
input byte counts, and fixed-120 pack shapes match the historical baseline.
The old raw provider envelope was not reused; V4R3 rebuilds the envelope with
the matched medium-reasoning/Alibaba/12,288-token/300-second configuration.
Evidence: `v4r3-fixed120-semantic-pack-equivalence.v1.json`.
