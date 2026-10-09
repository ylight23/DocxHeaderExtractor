# Frozen F1 Control/B delta audit

Provider-free, qualification-only audit of the seven frozen F1 pairs. Reads hash-pinned
provider bodies, source stores and issued maps; writes a new sanitized receipt. Refuses
to overwrite a receipt. Does not regenerate requests, read predictions or mutate Gold.

```powershell
dotnet run --project scripts/P7FrozenF1DeltaAudit -- <private-frozen-directory> <new-receipt.json>
```

The registered B delta is composite: parser-owned evidence, versioned envelope,
references/optional copied assertions, short interpretation record, output instructions
and schema, and qualification response limits. It is not a references-only or
geometry-only treatment. Local response byte caps differ (49,152 vs 262,144); provider
completion ceiling remains 32,768 for both arms.

The audit checks exact embedded Control user bytes, provider carrier equality,
prompt hashes, issued identity/order/text/page, and raw parser evidence. It rejects
unknown/duplicate fields, forged facts and semantic predicates injected into projection
rows. These checks certify request integrity, not semantic correctness or model accuracy.

D2.3 remains open: actual downstream request bytes depend on frozen Control upstream
capture. Token usage remains unmeasured and exact tokenizer mapping blocked. Provider
execution and production promotion remain locked; this tool grants no authorization.
