# P7-D2.3 offline closure (V2 acceptance)

This checkpoint separates offline preparation from execution. It does not rewrite
the V1 receipts, their `D2.3 OPEN` state, or `CONDITIONALLY_FROZEN` downstream bodies.
Offline PASS means the frozen inputs and generation contracts have passed protocol
regression. It is not evidence of semantic accuracy or execution freeze.

Only synthetic protocol fixtures exercise deterministic G2A/H2-C generation here.
They are never adopted as real pilot upstream, written into the pilot request set,
or used to report model accuracy. The two old Giấy mời captures remain historical
diagnostics, not experiment baselines. No fallback or historical adoption is added.

Controlled downstream uses actual frozen Control upstream for both arms during
future execution. Natural end-to-end uses each arm's own actual upstream. Their
manifest identity includes the mode and parent capture hashes even when provider
body bytes happen to coincide; metrics must not be pooled.

The verifier reads pinned receipts, the 56 existing F1 freeze files, generation
source hashes, and a passing P7 regression TRX. It does not read Gold decisions,
reviewer sidecars, provider responses, API credentials, or PDFs. A new receipt is
created with `CreateNew`; previous receipts are never rewritten.

After offline closure, actual downstream materialization is deferred until valid
upstream capture. Tokenizer mapping remains blocked, endpoint usage calibration
pending, D3 execution and P7-E promotion locked. Provider authorization and budget
approval remain separate and cannot be granted by this verifier.
