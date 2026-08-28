# RFC-4 RFC TOC Test Contract Correction

## Exact Contract

The exact 092 test no longer asserts the unexplained historical `Index = 8`.
It now asserts the authoritative occurrence identity:

```text
text = "1. Introduction"
stableId = body[1]/tbl[13]/tr[1]/tc[1]/p[1]
```

This is the only test assertion changed. RFC-2 production behavior and RFC-3
identity authority remain unchanged. The exact test passes.

## C1 Reconciliation

The 092 ledger row retains its original FQN, fingerprint, expected/actual
evidence, and history, while changing classification from
`REAL_PRODUCTION_FAILURE` to `STALE_TEST_EXPECTATION` with
`RFC3_OCCURRENCE_INDEX_DIAGNOSIS` as the reclassification source.

```text
TOTAL = 35
STALE_TEST_EXPECTATION = 18
REAL_PRODUCTION_FAILURE = 0
LEGACY_ONLY_TEST = 15
DIAGNOSTIC_CONTRACT_MISMATCH = 2
```

## Verification Boundary

The exact test passes and the Release solution build passes with zero errors.
The full `RfcTocDictionaryOutlineTests` group remains pending because three
other tests still assert superseded `auto:rfc-toc-dictionary` route values;
RFC-4 does not modify those unrelated route contracts. The existing F
regression evidence remains `2/2`, and provider calls are zero.

RFC-4 is therefore not marked fully CLOSED until the focused group gate is
resolved independently.
