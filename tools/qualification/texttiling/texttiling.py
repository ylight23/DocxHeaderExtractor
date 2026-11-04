"""TextTiling depth scores (Hearst 1997) for the body paragraphs of a DOCX. Offline: no model, no network.

Each body paragraph is one pseudo-sentence (bag of words, lower case, stop words removed). For each gap between
paragraph g-1 and g, the similarity of the k paragraphs before and after it is measured by cosine; the depth score
climbs both sides to their nearest peaks and sums how far the gap sits below them. Deep gaps are topic changes.
"""
import math
import re
import zipfile
from collections import Counter

STOP = set("""và của các là có cho với trong được những này đã sẽ một từ để theo về tại khi như hoặc ở bị đến thì cũng nên
đang rất đó đây khác còn qua do vì nếu mà đều cùng lại ra vào lên xuống nhiều ít số""".split())
WORD = re.compile(r"\w+", re.UNICODE)


def tokens(text: str) -> Counter:
    return Counter(w for w in WORD.findall(text.lower()) if w not in STOP and not w.isdigit())


def cosine(a: Counter, b: Counter) -> float:
    if not a or not b:
        return 0.0
    dot = sum(v * b.get(k, 0) for k, v in a.items())
    na = math.sqrt(sum(v * v for v in a.values()))
    nb = math.sqrt(sum(v * v for v in b.values()))
    return dot / (na * nb) if na and nb else 0.0


def depth_scores(paragraphs: list[str], k: int = 3) -> list[float]:
    """Depth score of the gap before paragraph g, for g = 1..n-1 (index 0 of the result is gap 1)."""
    bags = [tokens(p) for p in paragraphs]
    n = len(bags)

    def block(lo: int, hi: int) -> Counter:
        total = Counter()
        for b in bags[max(0, lo):min(n, hi)]:
            total.update(b)
        return total

    sims = [cosine(block(g - k, g), block(g, g + k)) for g in range(1, n)]
    depths = []
    for i, s in enumerate(sims):
        left = i
        while left > 0 and sims[left - 1] >= sims[left]:
            left -= 1
        right = i
        while right < len(sims) - 1 and sims[right + 1] >= sims[right]:
            right += 1
        depths.append((sims[left] - s) + (sims[right] - s))
    return depths


def body_paragraphs(docx_path: str) -> list[str]:
    """Text of the body-level paragraphs (not inside tables), in order: index 0 is body[1]/p[1]."""
    xml = zipfile.ZipFile(docx_path).read("word/document.xml").decode("utf-8")
    body = xml[xml.index("<w:body>"):]
    body = re.sub(r"<w:tbl>.*?</w:tbl>", "", body, flags=re.S)
    return ["".join(re.findall(r"<w:t[^>]*>([^<]*)</w:t>", p)) for p in re.findall(r"<w:p[ >].*?</w:p>", body, re.S)]
