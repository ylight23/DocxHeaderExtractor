# GIT-PUBLISH-20260828

The clean-architecture and verification work is published on `integration/final-authority-clean-architecture`, without merging or force-pushing `main`.

The branch preserves the architecture lineage through ARCH-4R (`71ede77`), the canonical verification checkpoint (`92cd2d6`), and the VERIFY-6B artifact. The target tree contains no `bin/`, `obj/`, `TestResults/`, or `*.trx` files.

Retained local TRX files are intentionally excluded from the branch; they remain available in the original worktree for local inspection.

Canonical VERIFY-6B evidence: `1326 total / 1296 passed / 30 failed / 0 skipped`, `new failures = 0`, `changed fingerprints = 0`, `unjoined = 0`, and failure universe frozen at 30.
