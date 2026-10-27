# PDF application default: V12 — 2026-10-25

At the user's explicit request, the application default `PdfMembershipInstructionVersion` is now **12**. Route construction resolves the actual selection instruction to `PdfMembershipInstructionV12.Instruction`: shared `AgentProtocol.InstructionStructuralV1`, formatting-evidence paragraph, and the existing PDF headingExtent requirement. Its inherited generic examples (such as Chapter I / GENERAL PROVISIONS) are preserved verbatim; no PDF072/agenda/participant-specific rule is appended.

OpenCV raster evidence remains enabled. Grouping retains `PdfGeneralBoundaryInstruction` and the generic pairwise JOIN/SPLIT prompt. The source-bound alias output contract, extent validation, exact ownership and budget planning remain unchanged. Semantic verification is still qualification-only; DOCX/scan prompts and defaults were not edited.

This change selects the V12 **instruction** in the production packet path. It does not restore the old trial's RepresentationTask wrapper, contextFacts or expanded block-evidence additions. Accordingly, the earlier Luna V12 0-FP observation must not be relabeled as a successful live test of this current profile. No new semantic-accuracy or stability claim is made, and no provider call is required to change the application default.

Resolved production prompts and PDF072 first packets are exported under `artifacts/qualification/pdf-v12-production-20261025/01/`. Historical V7/V6 artifacts and their scores remain unchanged. This is a source/default-profile update, not a running-service deployment.

Validation: test and qualification harness builds pass; 22/22 focused tests pass for default route resolution, V12 prompt/contract preservation, source ownership, geometry scoping, GROUP evidence and first-request planning/runtime parity. Exported PDF072 packets retain 15 full-page scopes and 650 aliases; geometry is AVAILABLE on all 15 pages and all requests fit with continuation reserve. Page 15 has 76 targets and 157,356 request bytes. No new model inference was run.

The production-mode Luna harness is updated to use V12 for fresh runs. Resume mode reads the instruction version from the frozen run identity, preserving the meaning of existing V7 continuations.
