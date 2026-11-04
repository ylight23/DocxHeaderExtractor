"""Offline comparison of two DOCX segmentations: fixed 48-paragraph windows and page-break segments.

usage: python compare_segments.py <docx> <gold.json> <out.json>
No provider call. Paragraph ids follow the Gold scheme: body[1]/p[n] for top-level paragraphs and
body[1]/tbl[k]/tr[r]/tc[c]/p[m] for paragraphs inside tables, numbered in document order.
"""
import json, re, sys, zipfile
import xml.etree.ElementTree as ET

W = "{http://schemas.openxmlformats.org/wordprocessingml/2006/main}"
WINDOW = 48  # DocxSelector: fixed windows of 48 paragraphs


def paragraphs(docx):
    """Yield (id, has_page_break_after) in document order."""
    with zipfile.ZipFile(docx) as z:
        root = ET.fromstring(z.read("word/document.xml"))
    body = root.find(W + "body")
    top_p = 0
    tbl_k = 0
    out = []

    def para_flags(p):
        brk = any(br.get(W + "type") == "page" for br in p.iter(W + "br"))
        ppr = p.find(W + "pPr")
        pbb = ppr is not None and ppr.find(W + "pageBreakBefore") is not None
        sect = ppr is not None and ppr.find(W + "sectPr") is not None
        return brk, pbb, sect

    for child in body:
        if child.tag == W + "p":
            top_p += 1
            brk, pbb, sect = para_flags(child)
            out.append((f"body[1]/p[{top_p}]", brk, pbb, sect))
        elif child.tag == W + "tbl":
            tbl_k += 1
            for r, tr in enumerate([t for t in child if t.tag == W + "tr"], 1):
                for c, tc in enumerate([t for t in tr if t.tag == W + "tc"], 1):
                    for m, p in enumerate([t for t in tc.iter(W + "p")], 1):
                        brk, pbb, sect = para_flags(p)
                        out.append((f"body[1]/tbl[{tbl_k}]/tr[{r}]/tc[{c}]/p[{m}]", brk, pbb, sect))
    return out


def cut_points(paras, kind):
    """Index after which a segment ends (exclusive end indices)."""
    n = len(paras)
    if kind == "fixed":
        return [min(i + WINDOW, n) for i in range(0, n, WINDOW)]
    ends = []
    for i, (_, brk, pbb, sect) in enumerate(paras):
        if pbb and i > 0:
            ends.append(i)
        if brk or sect:
            ends.append(i + 1)
    ends = sorted(set(e for e in ends if 0 < e <= n))
    if not ends or ends[-1] != n:
        ends.append(n)
    return ends


def segments(paras, ends):
    start, out = 0, []
    for e in ends:
        out.append((start, e))
        start = e
    return out


def main(docx, gold_path, out_path):
    paras = paragraphs(docx)
    gold = json.load(open(gold_path, encoding="utf-8"))
    headings = {c["sourceId"] for c in gold["occurrence"]["claims"]}
    index = {pid: i for i, (pid, *_ ) in enumerate(paras)}
    result = {"docx": docx, "paragraphs": len(paras), "goldHeadings": len(headings), "schemes": {}}
    for kind in ("fixed", "pagebreak"):
        ends = cut_points(paras, kind)
        segs = segments(paras, ends)
        sizes = [b - a for a, b in segs]
        # A boundary that lands between a Gold heading and the paragraph that follows it.
        split_heading = 0
        for a, b in segs[:-1]:
            last = paras[b - 1][0]
            if last in headings:
                split_heading += 1
        result["schemes"][kind] = {
            "segments": len(segs),
            "maxSegment": max(sizes), "minSegment": min(sizes),
            "meanSegment": round(sum(sizes) / len(sizes), 1),
            "segmentsOverWindow": sum(1 for s in sizes if s > WINDOW),
            "headingEndsSegment": split_heading,
        }
    json.dump(result, open(out_path, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
    print(json.dumps(result["schemes"], ensure_ascii=False, indent=2))
    print("paragraphs", len(paras), "gold headings", len(headings))


if __name__ == "__main__":
    main(*sys.argv[1:4])
