# Web source inspection

2026-10-16: the Web upload inspects file contents before enabling extraction. `POST /api/inspect` is a local-only
read of a temporary upload: no OCR, VLM, heading selection or provider call. The temporary copy is deleted afterwards.
`POST /api/extract` repeats the inspection on its own upload before building provider options; client route fields
are not trusted.

- OOXML uses the same `DocxSourceIntegrity` measurement as the production pipeline. `ScannedImageOnly` selects the
  existing scan route; native text stays on DOCX. Mixed Word files retain the existing native route, with an explicit
  notice that embedded page images are not OCR'd by that route.
- PDF pages with a page-sized image and fewer than 200 extracted non-whitespace characters require OCR. The image
  must cover at least 60% of both page dimensions, like the Word measurement; a small logo alone does not trigger it.
  PDF scan/mixed OCR execution is not implemented in the current scan processor (which consumes DOCX page images),
  so the Web explains this instead of silently extracting only a footer or a few text pages. Native PDF stays on PDF.
- Legacy office formats still use the runner's existing conversion to DOCX and its post-conversion integrity check.
  The initial inspection describes this as conversion rather than claiming an extension proves a native-text source.

The UI shows the scan prompt from `PromptCatalog.Scan`, selects OpenRouter for model-backed scan runs, and disables
Word outline writeback for scanned images. With no server key it explains the configuration and leaves the explicit
offline OCR mode available. Merely choosing a file never starts a provider call. File changes cancel pending
inspection and stale responses cannot replace the latest selection.

Verification: HTTP integration tests cover source detection despite misleading names, native Word/PDF, PDF scan
with/without a footer, server-side rechecking, and a Web scan run through the production pipeline with a fake local
OCR preparation and no provider. `node --test tests/web-source-detection.test.cjs` exercises the actual UI functions,
including out-of-order inspection responses and missing-key/offline behavior.
