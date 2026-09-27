# Open reasoning, closed commitment

Status: provider-free design proposal for `A99_SEMANTIC_CONTROL_CEILING_STUDY`.

## Invariant

The model may perform private, unrestricted reasoning internally. The production boundary accepts only a typed semantic commitment:

```text
SOURCE FACTS
    -> model semantic reasoning
    -> typed semantic commitment
    -> sourceParts + semanticFunction
    -> deterministic membership
    -> exact source-part binding
    -> identity
    -> hierarchy
```

The system must not request or persist chain-of-thought. A typed commitment is the model's externally observable decision, not a transcript of its reasoning.

## Truth-protecting constraints

- `sourceParts` remain the coordinate and verbatim-text authority.
- The response schema is closed and rejects unknown fields and unknown functions.
- Each occurrence carries exactly one `semanticFunction`.
- Source aliases must be owned by the request packet and must bind through the existing canonicalizer/binder.
- Membership is derived only from `DOCUMENT_IDENTITY` and `REGION_STRUCTURE`.
- Invalid, unowned, unbindable, or unmappable output fails closed.

These constraints protect provenance and prevent a diagnostic or a second semantic field from silently changing membership.

## Possible ceiling-lowering constraints

- The vocabulary may be too coarse for a descriptive structural kind such as an index group.
- A forced one-of commitment has no explicit abstention state.
- A 120-owned-item packet may not provide enough surrounding context for an extent decision.
- Provider/model reasoning may be explicitly disabled or constrained by the execution envelope.
- A correct semantic function does not by itself choose the correct multipart claim extent.

The frozen V4 evidence currently supports classification and extent as the active residual classes. It does not justify adding a new membership function.

## Abstention assessment

An `UNRESOLVED` or `ABSTAIN` function is not implemented or added in this study. It would change the treatment and make the reasoning experiment non-causal. It is a future design option only if a pre-registered arm demonstrates that forced commitment is itself the dominant error source. Any such state would be non-member, fail closed, and require a separate contract version.

## Experimental boundary

If explicitly authorized later, `V4R1_SEMANTIC_FUNCTION_EXPLICIT_REASONING` may change only the provider execution envelope. Prompt, schema, source facts, packing, request partitioning, binder, scorer, Gold, and membership derivation remain byte/semantic identical to frozen V4. No provider call is authorized by this document.

## Recommendation

Keep V2 as production default and V4 experimental. First measure the reasoning-envelope counterfactual. Only after that result is frozen should context/extent or a separate descriptive `structuralKind` arm be designed.
