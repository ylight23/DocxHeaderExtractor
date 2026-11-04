"""Compare the text of 075 DOCX (read straight from document.xml) with the text of 075 PDF (pdftotext -layout)."""
import difflib, re, subprocess, sys, unicodedata, zipfile
from pathlib import Path
from xml.etree import ElementTree as ET

ROOT = Path(sys.argv[1])
DOCX = ROOT / "todo10_8/heading_corpus_95_word/05_bien_ban_hop/075_FORTIS_GC_Minutes_Nov21_2024.docx"
PDF = ROOT / "todo10_8/heading_corpus_100/05_bien_ban_hop/075_FORTIS_GC_Minutes_Nov21_2024.pdf"
W = "{http://schemas.openxmlformats.org/wordprocessingml/2006/main}"

def docx_text(path):
    root = ET.fromstring(zipfile.ZipFile(path).read("word/document.xml"))
    lines = []
    for p in root.iter(W + "p"):
        text = "".join(t.text or "" for t in p.iter(W + "t"))
        if text.strip():
            lines.append(text)
    return "\n".join(lines)

def pdf_text(path):
    out = subprocess.run(["pdftotext", "-layout", str(path), "-"], capture_output=True, check=True).stdout
    return out.decode("utf-8")

def norm(text, keep_punct, keep_space):
    text = unicodedata.normalize("NFC", text)
    text = text.replace("’", "'").replace("‘", "'").replace("“", '"').replace("”", '"')
    text = re.sub(r"-[ \t]*\n[ \t]*", "", text)
    if not keep_space:
        text = re.sub(r"\s+", "", text)
    if not keep_punct:
        text = re.sub(r"[^0-9A-Za-zÀ-ỹ]", "", text)
    return text.lower() if not keep_punct else text

def compare(label, a, b, show=12):
    sm = difflib.SequenceMatcher(None, a, b, autojunk=False)
    ops = [o for o in sm.get_opcodes() if o[0] != "equal"]
    print(f"[{label}] lengths {len(a)} / {len(b)}  ratio {sm.ratio():.5f}  differing chunks {len(ops)}")
    for tag, i1, i2, j1, j2 in ops[:show]:
        print(f"    {tag}: DOCX {a[max(0, i1 - 15):i2 + 15]!r} | PDF {b[max(0, j1 - 15):j2 + 15]!r}")

d, p = docx_text(DOCX), pdf_text(PDF)
compare("letters and digits only, case-insensitive", norm(d, False, False), norm(p, False, False))
compare("punctuation kept, spaces removed", norm(d, True, False), norm(p, True, False))
compare("punctuation and spaces kept", norm(d, True, True), norm(p, True, True))
