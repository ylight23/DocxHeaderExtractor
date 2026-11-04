import fs from 'node:fs/promises';
import { execFileSync } from 'node:child_process';
import pptxgen from 'file:///C:/Users/btdba/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/pptxgenjs/dist/pptxgen.es.js';

const root = 'C:/DocxHeaderExtractor-a99-closed-loop';
const outDir = `${root}/artifacts/qualification/production-pipeline-diagram`;
const outputFile = `${outDir}/production-extraction-pipeline-v2.pptx`;
await fs.mkdir(outDir, { recursive: true });

const pptx = new pptxgen();
pptx.layout = 'LAYOUT_WIDE';
pptx.author = 'OpenAI';
pptx.subject = 'Production extraction pipeline';
pptx.title = 'Production Extraction Pipeline';
pptx.company = 'OpenAI';
pptx.lang = 'vi-VN';
pptx.theme = {
  headFontFace: 'Aptos Display', bodyFontFace: 'Aptos', lang: 'vi-VN'
};
pptx.defineSlideMaster({
  title: 'MASTER', background: { color: 'F6F8F7' },
  objects: [
    { rect: { x: 0, y: 0, w: 13.333, h: 0.13, line: { color: '136B5B', transparency: 100 }, fill: { color: '136B5B' } } },
    { text: { text: 'PRODUCTION EXTRACTION  /  PIPELINE', options: { x: 0.48, y: 7.18, w: 6.8, h: 0.18, fontFace: 'Aptos', fontSize: 8, color: '60716C', charSpacing: 0.5, margin: 0 } } },
    { text: { text: 'Source: production code paths; semantic outcomes remain model decisions.', options: { x: 7.0, y: 7.18, w: 5.85, h: 0.18, fontFace: 'Aptos', fontSize: 8, color: '60716C', align: 'right', margin: 0 } } },
  ],
  slideNumber: { x: 12.30, y: 7.16, color: '60716C', fontFace: 'Aptos', fontSize: 8 }
});

const C = {
  dark: '132F2C', green: '136B5B', mint: 'E4F1EC', ink: '213A35', muted: '526760',
  line: '8BA49B', white: 'FFFFFF', pdf: 'EDF4F1', docx: 'EDF3FA', scan: 'F5F0E9',
  orange: 'B9632D', paleOrange: 'F8EEE7', border: 'D5E1DC', bg: 'F6F8F7',
  blue: '4679B8', paleBlue: 'EAF1FB', teal: '17877D', paleTeal: 'E7F4F2',
  purple: '7354A3', palePurple: 'F0EBF8', amber: 'A96F0C', paleAmber: 'FBF1DE',
  forest: '347B4B', paleGreen: 'E8F3EA', neutral: 'F0F3F2', neutralStripe: '83938D'
};
let slide = pptx.addSlide('MASTER');
slide.addText('Production extraction pipeline', {
  x: 0.48, y: 0.37, w: 12.2, h: 0.42, fontFace: 'Aptos Display', fontSize: 24,
  bold: true, color: C.dark, margin: 0, breakLine: false
});
slide.addText('Mỗi route tạo evidence có thể truy về nguồn; model quyết định semantic, C# điều phối và kiểm tra contract.', {
  x: 0.5, y: 0.84, w: 12.2, h: 0.31, fontFace: 'Aptos', fontSize: 11.5,
  color: C.muted, margin: 0
});

function card(x, y, w, h, title, body, fill, accent, titleSize = 12, bodySize = 9) {
  slide.addShape(pptx.ShapeType.roundRect, {
    x, y, w, h, rectRadius: 0.08,
    fill: { color: fill }, line: { color: C.border, width: 0.9 }, radius: 0.08,
    shadow: { type: 'outer', color: '17342D', opacity: 0.08, blur: 1.5, angle: 45, distance: 1 }
  });
  slide.addShape(pptx.ShapeType.rect, { x, y: y + 0.04, w: 0.07, h: h - 0.08, fill: { color: accent }, line: { color: accent, transparency: 100 } });
  slide.addText(title, { x: x + 0.18, y: y + 0.12, w: w - 0.28, h: 0.28, fontFace: 'Aptos Display', fontSize: titleSize, bold: true, color: C.ink, margin: 0, valign: 'mid', fit: 'shrink' });
  slide.addText(body, { x: x + 0.18, y: y + 0.46, w: w - 0.3, h: h - 0.54, fontFace: 'Aptos', fontSize: bodySize, color: C.muted, margin: 0, valign: 'top', breakLine: false, fit: 'shrink', paraSpaceAfterPt: 2, lineSpacingMultiple: 1.03 });
}
function arrow(x1, y1, x2, y2, color = C.line, width = 1.7) {
  slide.addShape(pptx.ShapeType.line, { x: x1, y: y1, w: x2 - x1, h: y2 - y1, line: { color, width, endArrowType: 'triangle' } });
}
function label(text, x, y, w, color = C.muted) {
  slide.addText(text, { x, y, w, h: 0.2, fontFace: 'Aptos', fontSize: 8, color, italic: true, margin: 0, align: 'center', fit: 'shrink' });
}

// Three source routes converge on one shared, fully connected pipeline.
slide.addText('1  /  SOURCE EVIDENCE', { x: 0.5, y: 1.35, w: 3.1, h: 0.22, fontFace: 'Aptos', fontSize: 9, bold: true, charSpacing: 0.8, color: C.green, margin: 0 });
card(0.5, 1.68, 3.1, 0.78, 'DOCX  ·  OpenXML', 'Text, paragraph/style, numbering, table and alias', C.neutral, C.neutralStripe, 11.5, 8.7);
card(0.5, 2.58, 3.1, 0.94, 'PDF text  ·  PdfPig + OpenCV', 'Glyph boxes and layout; ink, frames, whitespace. Optional page image.', C.neutral, C.neutralStripe, 10.9, 8.5);
card(0.5, 3.64, 3.1, 0.78, 'Scan  ·  Image + OCR', 'Page image, recognized text, OCR boxes and alias', C.neutral, C.neutralStripe, 11.5, 8.7);

slide.addText('2  /  SHARED PRODUCTION FLOW', { x: 4.06, y: 1.35, w: 8.8, h: 0.22, fontFace: 'Aptos', fontSize: 9, bold: true, charSpacing: 0.8, color: C.green, margin: 0 });
const xs = [4.06, 5.54, 7.02, 8.50, 9.98, 11.46], bw = 1.30, by = 2.00, bh = 2.42;
card(xs[0], by, bw, bh, 'Units +\naliases', 'Keep source\nidentity and\nownership.\n\nPDF: paragraph /\ntable-region\nunits.', C.paleAmber, C.amber, 10.3, 8.5);
card(xs[1], by, bw, bh, 'Packet +\nscheduler', 'Target units,\nfacts, context,\nstyles, geometry\nand optional image.\n\nParallel calls,\nbudget, retry.', C.paleAmber, C.amber, 10.3, 8.4);
card(xs[2], by, bw, bh, 'SELECT\nLLM', 'Chooses source\naliases that are\nheadings from the\nprovided text and\nevidence.', C.palePurple, C.purple, 10.6, 8.5);
card(xs[3], by, bw, bh, 'GROUP +\nPLACE LLM', 'GROUP proposes\nboundaries.\n\nPLACE proposes\noutline level and\nparent.', C.palePurple, C.purple, 10.3, 8.5);
card(xs[4], by, bw, bh, 'C# checks +\nassembly', 'Checks response\ncontract, coverage\nand source binding.\n\nAssembles the\noutline.', C.paleAmber, C.amber, 10.1, 8.3);
card(xs[5], by, bw, bh, 'Outline\noutput', 'Heading tree\nwith source\nreferences.', C.neutral, C.neutralStripe, 10.6, 8.5);
arrow(3.62, 2.07, xs[0] - 0.04, 2.20); arrow(3.62, 3.05, xs[0] - 0.04, 3.20); arrow(3.62, 4.03, xs[0] - 0.04, 4.15);
for (let i = 0; i < xs.length - 1; i++) arrow(xs[i] + bw + 0.015, 3.20, xs[i + 1] - 0.045, 3.20, i === 1 || i === 2 || i === 3 ? C.green : C.line, 1.6);
label('source evidence', 3.58, 1.78, 0.51);
function rolePill(text, x, y, w, fill, color) {
  slide.addShape(pptx.ShapeType.roundRect, { x, y, w, h: 0.22, rectRadius: 0.06, fill: { color: fill }, line: { color: fill, transparency: 100 } });
  slide.addText(text, { x, y: y + 0.015, w, h: 0.17, fontFace: 'Aptos', fontSize: 7.5, bold: true, color, margin: 0, align: 'center', valign: 'mid' });
}
rolePill('LLM', xs[2] + 0.40, 4.48, 0.50, C.palePurple, C.purple);
rolePill('LLM', xs[3] + 0.40, 4.48, 0.50, C.palePurple, C.purple);
rolePill('C# CODE', xs[4] + 0.29, 4.48, 0.73, C.paleAmber, C.amber);

slide.addText('3  /  ROLE BOUNDARIES', { x: 0.5, y: 4.94, w: 4.3, h: 0.22, fontFace: 'Aptos', fontSize: 9, bold: true, charSpacing: 0.8, color: C.green, margin: 0 });
card(0.5, 5.25, 3.88, 1.18, 'C#  ·  Parser / OpenCV / OCR', 'Measures or extracts source evidence. Geometry describes layout; it does not label semantic headings.', C.paleAmber, C.amber, 10.8, 8.7);
card(4.72, 5.25, 3.88, 1.18, 'LLM', 'Makes SELECT, GROUP and PLACE proposals from packet evidence. A valid response may still be semantically wrong.', C.palePurple, C.purple, 10.8, 8.7);
card(8.94, 5.25, 3.90, 1.18, 'C# coordinator', 'Builds packets, runs calls, handles budgets / retries, validates contracts and binds outputs to source aliases.', C.paleAmber, C.amber, 10.8, 8.7);
slide.addText('Contract validity does not prove extraction accuracy. Measure TP / FP / FN and wall time against reviewed Gold.', {
  x: 0.52, y: 6.65, w: 12.1, h: 0.25, fontFace: 'Aptos', fontSize: 9, color: C.muted, margin: 0
});
slide.addNotes(`Production source anchors:\n- DOCX: src/DocxHeaderExtractor.HeadingAgent/Formats/DocxAgentSource.cs; Selectors/AgentSelector.cs; Structure/StructureAssembler.cs.\n- PDF: Formats/AgentSource.cs; Runtime/PdfSelectionUnits.cs; Runtime/PdfUnitSelectionTask.cs; Runtime/PdfRasterEvidence.cs; Sources/Pdf/PdfGeometryMeasurements.cs.\n- GROUP / PLACE: HeadingAgent/Grouping/AgentGrouping.cs; HeadingAgent/Placement/HeadingPlacer.cs.\n- Contract boundary: PdfUnitSelectionProtocol.cs and PdfUnitSelectionTask.Judge. Contract validity does not ensure semantic correctness.\nNote: route configuration can vary; this diagram reflects the source responsibilities, not a claim that all optional phases run identically on every route.`);

// Slide 2: compare both techniques as native editable icon flows with short Vietnamese labels.
slide = pptx.addSlide();
slide.background = { color: C.bg };
slide.addShape(pptx.ShapeType.rect, { x: 0, y: 0, w: 13.333, h: 0.13, fill: { color: C.green }, line: { color: C.green, transparency: 100 } });
slide.addText('PRODUCTION EXTRACTION  /  COMPARISON', { x: 0.48, y: 7.18, w: 6.8, h: 0.18, fontFace: 'Aptos', fontSize: 8, color: '60716C', charSpacing: 0.5, margin: 0 });
slide.addText('Luồng minh họa; tốc độ và độ chính xác cần đo riêng.', { x: 7.0, y: 7.18, w: 5.85, h: 0.18, fontFace: 'Aptos', fontSize: 8, color: '60716C', align: 'right', margin: 0 });
slide.addText('Prompt trực tiếp và kiến trúc harness + LLM', { x: 0.48, y: 0.37, w: 12.2, h: 0.42, fontFace: 'Aptos Display', fontSize: 23, bold: true, color: C.dark, margin: 0 });
slide.addText('Khác biệt chính: các bước C# chuẩn bị và kiểm soát quy trình quanh quyết định semantic của LLM.', { x: 0.5, y: 0.84, w: 12.2, h: 0.31, fontFace: 'Aptos', fontSize: 11.5, color: C.muted, margin: 0 });

function flowCard(x, y, w, h, title, desc, kind = 'neutral') {
  const style = kind === 'llm' ? [C.palePurple, C.purple] : kind === 'code' ? [C.paleAmber, C.amber] : [C.neutral, C.neutralStripe];
  slide.addShape(pptx.ShapeType.roundRect, { x, y, w, h, rectRadius: 0.07, fill: { color: style[0] }, line: { color: C.border, width: 0.9 } });
  slide.addShape(pptx.ShapeType.rect, { x, y: y + 0.04, w: 0.065, h: h - 0.08, fill: { color: style[1] }, line: { color: style[1], transparency: 100 } });
  slide.addText(title, { x: x + 0.15, y: y + 0.67, w: w - 0.25, h: 0.26, fontFace: 'Aptos Display', fontSize: 11.3, bold: true, color: C.ink, margin: 0, fit: 'shrink', align: 'center' });
  slide.addText(desc, { x: x + 0.12, y: y + 0.98, w: w - 0.20, h: h - 1.06, fontFace: 'Aptos', fontSize: 8.4, color: C.muted, margin: 0, fit: 'shrink', align: 'center', valign: 'top' });
  return { x, y, w, h, color: style[1] };
}
function iconDocument(cx, cy, color) {
  slide.addShape(pptx.ShapeType.rect, { x: cx - 0.19, y: cy - 0.24, w: 0.38, h: 0.48, fill: { color: C.white }, line: { color, width: 1.5 } });
  for (let i = 0; i < 3; i++) slide.addShape(pptx.ShapeType.line, { x: cx - 0.12, y: cy - 0.10 + i * 0.09, w: 0.24, h: 0, line: { color, width: 1.15 } });
}
function iconPrompt(cx, cy, color) {
  slide.addShape(pptx.ShapeType.roundRect, { x: cx - 0.25, y: cy - 0.19, w: 0.50, h: 0.38, rectRadius: 0.04, fill: { color: C.white }, line: { color, width: 1.4 } });
  slide.addShape(pptx.ShapeType.line, { x: cx - 0.15, y: cy - 0.06, w: 0.30, h: 0, line: { color, width: 1.2 } });
  slide.addShape(pptx.ShapeType.line, { x: cx - 0.15, y: cy + 0.035, w: 0.22, h: 0, line: { color, width: 1.2 } });
}
function iconNodes(cx, cy, color) {
  const pts = [[cx - 0.16, cy - 0.12], [cx + 0.15, cy - 0.12], [cx, cy + 0.15]];
  slide.addShape(pptx.ShapeType.line, { x: pts[0][0], y: pts[0][1], w: pts[1][0] - pts[0][0], h: 0, line: { color, width: 1.4 } });
  slide.addShape(pptx.ShapeType.line, { x: pts[0][0], y: pts[0][1], w: pts[2][0] - pts[0][0], h: pts[2][1] - pts[0][1], line: { color, width: 1.4 } });
  slide.addShape(pptx.ShapeType.line, { x: pts[2][0], y: pts[2][1], w: pts[1][0] - pts[2][0], h: pts[1][1] - pts[2][1], line: { color, width: 1.4 } });
  for (const [x, y] of pts) slide.addShape(pptx.ShapeType.ellipse, { x: x - 0.07, y: y - 0.07, w: 0.14, h: 0.14, fill: { color: C.white }, line: { color, width: 1.4 } });
  slide.addShape(pptx.ShapeType.ellipse, { x: cx - 0.055, y: cy - 0.055, w: 0.11, h: 0.11, fill: { color }, line: { color, width: 1 } });
}
function iconLayers(cx, cy, color) {
  for (let i = 0; i < 3; i++) slide.addShape(pptx.ShapeType.rect, { x: cx - 0.22 + i * 0.06, y: cy - 0.18 + i * 0.07, w: 0.36, h: 0.23, fill: { color: C.white }, line: { color, width: 1.2 } });
}
function iconCheck(cx, cy, color) {
  slide.addShape(pptx.ShapeType.roundRect, { x: cx - 0.21, y: cy - 0.21, w: 0.42, h: 0.42, rectRadius: 0.05, fill: { color: C.white }, line: { color, width: 1.4 } });
  slide.addShape(pptx.ShapeType.line, { x: cx - 0.11, y: cy, w: 0.08, h: 0.09, line: { color, width: 2 } });
  slide.addShape(pptx.ShapeType.line, { x: cx - 0.03, y: cy + 0.09, w: 0.17, h: -0.19, line: { color, width: 2 } });
}
function iconTree(cx, cy, color) {
  slide.addShape(pptx.ShapeType.line, { x: cx, y: cy - 0.11, w: 0, h: 0.21, line: { color, width: 1.4 } });
  slide.addShape(pptx.ShapeType.line, { x: cx - 0.17, y: cy + 0.02, w: 0.34, h: 0, line: { color, width: 1.4 } });
  for (const dx of [-0.17, 0, 0.17]) {
    slide.addShape(pptx.ShapeType.line, { x: cx + dx, y: cy + 0.02, w: 0, h: 0.13, line: { color, width: 1.3 } });
    slide.addShape(pptx.ShapeType.ellipse, { x: cx + dx - 0.045, y: cy + 0.13, w: 0.09, h: 0.09, fill: { color: C.white }, line: { color, width: 1.3 } });
  }
  slide.addShape(pptx.ShapeType.ellipse, { x: cx - 0.05, y: cy - 0.16, w: 0.10, h: 0.10, fill: { color: C.white }, line: { color, width: 1.3 } });
}

slide.addText('GỌI TRỰC TIẾP', { x: 0.62, y: 1.42, w: 2.4, h: 0.22, fontFace: 'Aptos', fontSize: 9, bold: true, charSpacing: 0.7, color: C.neutralStripe, margin: 0 });
const directY = 1.77, directH = 1.60;
const d1 = flowCard(1.40, directY, 2.20, directH, 'Tài liệu + prompt', 'Văn bản và hướng dẫn', 'neutral'); iconPrompt(2.50, directY + 0.38, C.neutralStripe);
const d2 = flowCard(5.55, directY, 2.20, directH, 'LLM', 'Trả quyết định theo prompt', 'llm'); iconNodes(6.65, directY + 0.38, C.purple);
const d3 = flowCard(9.70, directY, 2.20, directH, 'Kết quả', 'Text hoặc JSON cho ứng dụng', 'neutral'); iconDocument(10.80, directY + 0.38, C.neutralStripe);
arrow(d1.x + d1.w + 0.08, directY + 0.80, d2.x - 0.08, directY + 0.80, C.line, 1.7);
arrow(d2.x + d2.w + 0.08, directY + 0.80, d3.x - 0.08, directY + 0.80, C.line, 1.7);

slide.addText('HARNESS + LLM', { x: 0.62, y: 3.77, w: 2.8, h: 0.22, fontFace: 'Aptos', fontSize: 9, bold: true, charSpacing: 0.7, color: C.green, margin: 0 });
const hy = 4.10, hh = 1.68, hx = [0.54, 2.68, 4.82, 6.96, 9.10, 11.24], hw = 1.58;
const h1 = flowCard(hx[0], hy, hw, hh, 'Tệp nguồn', 'PDF · DOCX · scan', 'neutral'); iconDocument(hx[0] + hw / 2, hy + 0.40, C.neutralStripe);
const h2 = flowCard(hx[1], hy, hw, hh, 'Parser + evidence', 'Text, tọa độ, ảnh', 'code'); iconPrompt(hx[1] + hw / 2, hy + 0.40, C.amber);
const h3 = flowCard(hx[2], hy, hw, hh, 'Packet C#', 'Unit, facts, context', 'code'); iconLayers(hx[2] + hw / 2, hy + 0.40, C.amber);
const h4 = flowCard(hx[3], hy, hw, hh, 'LLM', 'SELECT · GROUP · PLACE', 'llm'); iconNodes(hx[3] + hw / 2, hy + 0.40, C.purple);
const h5 = flowCard(hx[4], hy, hw, hh, 'Kiểm tra C#', 'Contract và alias nguồn', 'code'); iconCheck(hx[4] + hw / 2, hy + 0.40, C.amber);
const h6 = flowCard(hx[5], hy, hw, hh, 'Outline', 'Cây heading + nguồn', 'neutral'); iconTree(hx[5] + hw / 2, hy + 0.40, C.neutralStripe);
const hCards = [h1, h2, h3, h4, h5, h6];
for (let i = 0; i < hCards.length - 1; i++) arrow(hCards[i].x + hw + 0.025, hy + 0.84, hCards[i + 1].x - 0.025, hy + 0.84, C.line, 1.35);

slide.addShape(pptx.ShapeType.roundRect, { x: 0.72, y: 6.20, w: 11.90, h: 0.58, rectRadius: 0.05, fill: { color: C.paleTeal }, line: { color: 'C9DED7', width: 0.8 } });
slide.addText('C# chuẩn bị, điều phối và kiểm tra. LLM vẫn đưa ra quyết định semantic. Harness tăng khả năng truy vết; không tự bảo đảm nhanh hoặc chính xác hơn.', {
  x: 0.93, y: 6.31, w: 11.48, h: 0.32, fontFace: 'Aptos', fontSize: 10, bold: true, color: C.dark, margin: 0, align: 'center', fit: 'shrink'
});
slide.addNotes(`So sánh minh họa, không tuyệt đối hóa direct-call apps: ứng dụng gọi prompt trực tiếp vẫn có thể thêm parser, retry, validator hoặc coordinator. Harness production bổ sung các thành phần quanh model. Nhãn tiếng Việt là tóm tắt; contracts và các route tùy chọn khác nhau theo cấu hình. Không suy ra accuracy hoặc SLA từ sơ đồ.`);

await pptx.writeFile({ fileName: outputFile });
execFileSync('C:/Users/btdba/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe', [
  `${outDir}/normalize_pptx.py`, outputFile
]);
console.log(JSON.stringify({ file: outputFile, slides: 2 }));
