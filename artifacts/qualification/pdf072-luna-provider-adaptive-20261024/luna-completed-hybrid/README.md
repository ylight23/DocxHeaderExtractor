# PDF072 Luna-provider adaptive qualification

The current adaptive PDF pipeline ran through selection, grouping, binding, and hierarchy placement. Luna authored model-contract replies in-session through a local `HttpMessageHandler`; no external provider call or data transmission occurred. The SSE envelope and token/cost accounting are synthetic adapter fixtures, not native provider telemetry or billing.

- Final run: d6973733-4a14-4539-9153-018050df3a3f, 142.07 seconds; source PDF SHA-256: a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61; production HEAD: 8b1e09a0b8b062847580a8a893f36a2eb3bd9c6f; tracked diff SHA-256: 6eb6f0c894ab2b85541026b4ebd2e4ff299d8ffa3d77af3af8566621890ec8f8.
- Result: 61 headings selected/grouped/bound; 16 model-divided grouping runs; zero grouping fallback/dispute/unresolved aliases; hierarchy 60 placed, one `NONE` title, zero unresolved, zero placement protocol errors.
- Page 15 ran as one selector packet containing all 76 atoms; exact body size 103,604 bytes. All five selected headings bound and placed: Annex 2 at ROOT, with TAG members, experts/speakers/observers, IACG, and World Bank as H2 children.
- Adapter accounting: 21 admissions; simulated confirmed/provisional/outstanding cost all $0.00. This is not a provider bill.

The first run, `5e5bb04e-02db-42be-90f0-f11162deb3d4` (667.49 seconds), exposed a qualification-harness envelope error: placement replies 002–004 were saved as raw protocol objects without the required wrapper, so the production parser saw empty content and returned `CONTENT_NOT_TEXT`. It is preserved at `../bridge-envelope-failure.json` with the original raw reply files in `../responses/`.

The completed run reused cached Luna content only for exact request SHA-256 matches, then Luna answered the two changed-context placement packets after accepted edges changed the graph. All 21 final request bodies match recorded byte counts and SHA-256 values. No unknown hash was answered from a stale packet and no request used network. The intermediate strict replay that intentionally rejected changed request hashes is preserved in `../corrected-offline-replay/`.

See `run-result.json` for the full audit, `all-headings.csv` / `.json` / `.tsv` for every heading and parent, `packet-integrity.csv` for exact request hashes, and `build-manifest.json` for relevant assembly hashes. Two fresh changed-context replies are in `responses/`; the complete final response cache keyed by every captured request SHA is in `final-reply-map-by-request-sha256.json`.