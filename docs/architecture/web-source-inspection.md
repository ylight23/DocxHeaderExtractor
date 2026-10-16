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
  PDF scan/mixed documents use the shared scan processor: every page is rendered at 200 DPI, including native text,
  annotations and form appearances, then OCR/VLM reads that single raster universe in page order. Native PDF stays on PDF.
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

The production PDF handler repeats the same `PdfSourceIntegrity` check as the Web. CLI extraction and Web therefore
make the same route choice. `scan-ocr` also accepts PDF directly. `PDFtoImage` 5.4.0/PDFium is now a production source
dependency; it renders full pages rather than extracting embedded images, so it preserves overlaid text/stamps.
The raster version, DPI and renderer version enter only PDF's OCR cache key and `pageImageBasis`; existing DOCX OCR
entries keep their keys. Coordinates remain top-left image pixels of the stored raster, not PDF points. Mixed PDFs
are rendered in full so native and image-only pages are neither skipped nor entered twice. The source PDF stays intact.

The local check on PDF 011 rendered and OCR'd all 28 pages in 140.7 seconds: 1,056 primary OCR lines and 1,050 upscaled
lines, both views complete and no API calls. The diagnostic (not a gold or an accuracy score) is recorded in
[`pdf-scan-011-ocr.v1.json`](../../eval/scan-text/reports/pdf-scan-011-ocr.v1.json).
