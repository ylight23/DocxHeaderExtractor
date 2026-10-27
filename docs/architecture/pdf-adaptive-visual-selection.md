# PDF adaptive visual selection

`PipelineOptions.PdfAdaptiveVisualSelection` enables a PDF-only SELECT packet shape. It leaves the V12 membership instruction unchanged.

When enabled, the PDF source renders one downsampled JPEG overview for each source page. The production page/block/row planner measures the final OpenRouter body, including that JPEG, before it accepts a scope. A page is owned by one SELECT scope when the measured body fits; otherwise the existing ordered planner descends through layout blocks and visual rows. Every alias still has one owner.

The image is appended as an OpenAI-compatible `image_url` block in the actual user message. `RequestResources` sees the resulting wire bytes and image count, so a visual packet cannot bypass the byte or context admission checks. GROUP and PLACE have no image attachment.

The adaptive path projects PDF target facts to measurement-only fields: source alias/page, parser layout block, visual-line segments, shared left edge, bbox/font layout, page and document style, font delta, and bounded repeat evidence. It carries no heading/table labels.

Since 2026-10-26 the path is the native PDF default, with V14 selection units and a PDF-only `PdfVisualSelectionMaxRequestBytes` of 786,432 bytes. Set `PdfAdaptiveVisualSelection = false` and the ceiling to null for the previous text-only profile. The generic `MaxModelRequestBytes` still applies to DOCX and scan and is not changed by this option.
