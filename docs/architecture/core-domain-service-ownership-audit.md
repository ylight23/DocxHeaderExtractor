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

Tested implementation revision: `cb2246b9ee8f7f13e24c7c2028aa70c25c1d8d17`.

| Check | Result |
|---|---|
| Clean tracked solution Release build | PASS, 0 errors; existing warnings remain |
| Focused ownership, provider policy, lifecycle, wire/composer and semantic contract tests | 95/95, 0 failed/skipped |
| Full Windows tracked Release suite | 986/986, 0 failed/skipped, 14m36s |
| Verification checkout status and diff check after suite | clean, no artifact drift |
| Extracted implementations compared with baseline | all 13 unchanged, excluding ownership/imports |

Local TRX hashes:

- `c1-c2-focused.trx`: `e628a13e68147e465e5e8e3ccf88673491e0974af69f8e54d9078f710899e7bc`
- `c1-c2-full.trx`: `e59ede6c8a33f44f9f6bf9aea036bb9d11a4e032648162ca36e7c4874911ff3b`

TRX files remain in the isolated verification worktree's ignored `.verify-build/test-results`.
These results do not claim a new GitHub Ubuntu CI run or a full suite on a later revision.
Live-provider test gates were disabled; this is refactor verification, not model qualification.

The author's H3 diff (182 added / 17 removed lines) and 238 untracked research files are preserved
and excluded. The clean tracked suite does not include those local edits/untracked tests.
Provider calls: 0. Gold mutation: 0. Frozen artifact rebaseline: 0.
