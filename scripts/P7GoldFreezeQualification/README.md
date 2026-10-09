# P7-D2.3 Gold V2 freeze qualification

Provider-free qualification only. The approved five-document / six-page pilot is
213 decisions, 15 closed heading units and 20 exact UTF-16 parts. This script does
not create or amend Gold, policies, source snapshots, requests or production code.

`P7PilotGoldReader.Load` opens only a Gold document and its pinned task policy.
Original sidecars remain hash-pinned archive evidence, but their existence,
vocabulary and interpretations are not scoring prerequisites. Source grounding
is checked separately against pinned original PDFs and complete snapshots.

For each document the runner writes new private copies and compares full scoring
bytes across: original sidecar, absent sidecar, all role/explanation/alternative
text replaced, and an additional previously unknown role. The comparison includes
readiness, TP/FP/FN, boundary outcomes, NOT_EVALUABLE, invalid references/spans,
duplicates/overlap and retained crossing-scope predictions. Only invented
predictions are scored; these results do not measure model accuracy.

The approved Gold and original scorer manifests are not regenerated. A separate
qualification harness manifest records their immutable hashes and current reader/
runner source hashes. Request manifests remain NOT_FROZEN, D2.3 remains open for
request freeze, and provider execution / production promotion remain LOCKED.

Run from repository root with .NET 9:

```powershell
dotnet build scripts/P7GoldFreezeQualification/P7GoldFreezeQualification.csproj -c Release --artifacts-path .verify-build/p7-gold-freeze
dotnet .verify-build/p7-gold-freeze/bin/P7GoldFreezeQualification/release/P7GoldFreezeQualification.dll <gold-v2-directory> <source-pack> <new-private-output-directory> <new-public-receipt>
```

Existing output destinations are rejected. Preserve private copies of the full
scores and harness manifest; publish the sanitized receipt only, without source
text. Re-run into a different new output directory to compare artifact bytes.
No provider credentials, calls, retries, repair or fallback are used.
