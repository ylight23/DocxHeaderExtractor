# C1/C2 ownership closure

Baseline: `57a2f08a7fa94c3853cb674cf8a80332c7faaedf`.

## Scope

- Generic frozen transports execute bytes only. The authorized PDF production capability owns
  its qualified request composer. Qualification explicitly supplies the same composer.
- Domain services move out of Models to Core `Semantics/Binding`, `Validation`, `Parsing`, and
  `Identity`. No compatibility facades keep these implementations in Models.
- Source file hashing moves to DocumentProcessing `Provenance` because it performs file IO.
- DTOs/value objects and their JSON names remain unchanged in Core Models. Invariant-bearing
  aggregates, contract descriptors, and neutral wire codecs may contain behavior; the guard
  rejects named domain services in Models, not every method or class.
- Relocated implementation namespaces require consumers to update imports/rebuild. Public
  implementation binary compatibility is not claimed.

No prompts, schemas, token limits, provider bytes, Gold, or historical artifacts are changed.
The lease-bound wrapper remains an execution-only transport; timeout/cancellation and late-result
quarantine are unchanged. A direct generic PDF transport now fails authorization before calls.

## Verification

Implementation verification is pending a clean tracked Release checkout. Existing H3 edits and
untracked research artifacts/tests are excluded, preserved, and not used as qualification evidence.
Provider calls: 0. Gold mutation: 0. Frozen artifact rebaseline: 0.
