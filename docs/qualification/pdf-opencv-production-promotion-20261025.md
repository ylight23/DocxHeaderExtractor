# PDF text raster geometry: application-default promotion — 2026-10-25

Subsequent default prompt update: see `pdf-generic-production-prompt-20261025.md`. The measurements and V6 configuration below document the original geometry-only promotion; the current application default membership instruction is now generic V7.

## Authorized scope

The user explicitly requested promoting the preprocessing/evidence work before evaluating Qwen semantic verification. This change enables **OpenCV raster geometry evidence** in the application's native PDF text route. It does not replace Qwen with Luna, enable semantic GroupVerifier, change membership instruction V6, or change DOCX/scan behavior. This is a source-code/default-profile promotion; no remote deployment or running service restart is implied.

The earlier page-14 experiments did not demonstrate a Qwen accuracy gain. This promotion is based on the user's requested sequencing and the technical integration checks below, not a newly passed semantic-quality gate.

## Behavior

- `PipelineOptions.PdfRasterGeometryEvidence` defaults to true; HeadingAgentSetup passes it to PdfSelector. Setting it false restores the previous PDF packet path. Low-level direct PdfSelector and AgentSource.Load defaults remain false for existing parity/qualification callers.
- PdfGeometryMeasurements streams each page raster once at the scan renderer's 200-DPI basis. It keeps PDF parser text/aliases and measures ink, blank bands, drawn frames and separated ink runs. Peer alignment/overlap relations are derived from transformed parser boxes and marked accordingly.
- Geometry is available before OrderedScopePlanner serializes and budgets complete requests. The runtime SelectionTask and planning AgentUnit use the same attachment function; exact request hashes/bytes are tested.
- Evidence is scoped to aliases already present in the packet, including context/style evidence. Peer alias references are trimmed to that shown set. Page-wide measurements retain their page-wide provenance. The classifier retains the existing source-bound response and extent contract; geometry has no semantic membership authority.
- The same scoped geometry is attached to PDF GROUP requests. GROUP remains boundary-only; no semantic verifier is enabled by this promotion.
- Unsupported rotated/cropped coordinate transforms, missing native rendering support and measurement failures yield explicit `UNAVAILABLE` observations. Extraction continues using existing text evidence; geometry is never fabricated. Cancellation still propagates.
- Selector identity includes `pdf-raster-geometry-v1:` and actual request/plan hashes include the geometry packet.

## Offline production-packet audit

Artifacts: `artifacts/qualification/pdf-opencv-production-20261025/01/`. Generated using the new `production-geometry` option of PdfFactsAudit, without provider access or semantic decisions.

PDF072 retains 15 full-page scopes and 650 aliases with exact ownership. All 15 pages report AVAILABLE geometry. Every actual first packet fits byte, prompt/context estimate and completion limits including continuation reserve under the configured 262,144-byte profile.

| Page | Targets | Request bytes | Input token estimate | Completion cap |
|---|---:|---:|---:|---:|
| 14 | 33 | 105,718 | 97,474 | 32,768 |
| 15 | 76 | 159,234 | 144,532 | 32,768 |

The export contains all 15 actual first request bodies, source facts and `production-geometry-plan.json`. Provider calls: zero; semantic accuracy is not measured by this audit.

## Verification

Application and test builds succeed. Existing warnings elsewhere in the repository remain; this change does not claim a warning-free repository build.

The first focused run passes 44/44 tests covering the application profile, production geometry packet/runtime parity, unchanged alias/text identity, scoped evidence, adaptive budgeting, historical PDF packet parity and pairwise boundary behavior. A second focused run passes 2/2 new tests for explicit UNAVAILABLE on render failure and evidence delivery into a real pairwise GROUP invocation with a synthetic model response. Total: 46/46 focused tests, zero failures. Synthetic boundary tests validate integration and coverage, not LLM semantic quality.

## Next Qwen verifier experiment

Use the actual previously recorded erroneous selection from `artifacts/qualification/pdf-opencv-geometry-20261025/02/geometry-outcome.json` as frozen membership input, including `L0550:S0`. Run GROUP and the opt-in semantic verifier against source evidence, preserving explicit NOT_HEADING versus UNRESOLVED states. This avoids waiting for a fresh SELECT call to happen to reproduce the same error.

OpenRouter's last attempted run failed with PAYMENT_REQUIRED; this promotion makes no new provider call and does not establish that access is restored. Semantic verification stays qualification-only pending that separate test. Luna's successful clean-selection run is not evidence that Qwen VERIFY corrects a false positive.
