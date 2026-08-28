# VERIFY-6B — Canonical Integrated Full Suite

Canonical execution used exactly one full-suite run at `92cd2d6d3cba29986858d30a91d5da0468044cff`. The clean ENV2 tree, canonical artifact manifest, storage preflight, and focused gates remained valid.

Result: `1326 total / 1296 passed / 30 failed / 0 skipped`. All 30 failures joined the VERIFY-3 authority by exact FQN; there were no new or unjoined failures and no changed fingerprints. The integrated failure universe is therefore frozen at 30.

The DocumentDiagnosticRunner failure is retained as `PREEXISTING_RELATIVE_TO_INTEGRATION`: its lineage predates architecture replay and MCP-2, with root cause still `UNKNOWN`. It is not counted as an architecture or MCP regression.

No remediation, test edit, expected-value edit, production edit, artifact regeneration, or provider call occurred. The raw TRX is recorded in the JSON artifact.
