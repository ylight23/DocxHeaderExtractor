"""Task A vs Gold SRC-095: what are the 89 front-matter atoms Qwen selected and the Gold does not count, and how the Gold's source review
classified them. Reads only recorded data; changes nothing."""
import collections
import json
import re
import sys

universe_path, diag_path, review_path, gold_path, out_path = sys.argv[1:6]
u = json.load(open(universe_path, encoding="utf-8"))
text = {l["alias"]: l["text"] for l in u["lines"]}
page = {l["alias"]: l["page"] for l in u["lines"]}
diag = json.load(open(diag_path, encoding="utf-8"))
review = json.load(open(review_path, encoding="utf-8"))
gold = json.load(open(gold_path, encoding="utf-8"))

# Collect every alias the source review names, with its pattern, wherever it appears.
pattern_of = {}
def walk(n):
    if isinstance(n, dict):
        alias = n.get("sourceAlias") or n.get("alias")
        pat = n.get("pattern")
        if isinstance(alias, str) and isinstance(pat, str):
            pattern_of[alias] = pat
        for k, v in n.items():
            if k == "sourceParts" and isinstance(v, list) and isinstance(pat, str):
                for p in v:
                    if isinstance(p, dict) and isinstance(p.get("sourceAlias"), str):
                        pattern_of[p["sourceAlias"]] = pat
            walk(v)
    elif isinstance(n, list):
        for x in n:
            walk(x)
walk(review)

fps = [a for r in diag["rows"] for a in r["falsePositives"]]
by_pattern = collections.Counter(pattern_of.get(a, "NOT_LISTED_IN_REVIEW") for a in fps)

gold_texts = []
for c in gold["semantic"]["claims"]:
    gold_texts.append(" ".join(text.get(p["sourceAlias"], "") for p in c["sourceParts"]))
def norm(s):
    s = re.sub(r"\.{2,}.*$", "", s)           # dot leaders and the page number after them
    s = re.sub(r"\s+\d+\s*$", "", s)           # trailing page number
    return re.sub(r"\s+", " ", s).strip().lower()
gold_norm = {norm(t) for t in gold_texts}
echo = [a for a in fps if norm(text.get(a, "")) in gold_norm and norm(text.get(a, ""))]

out = {
    "artifact": "p7.f1.v6.d4.6-task-a-toc-check.v1",
    "question": "Does Task A ('Trích xuất tất cả heading') state how heading titles repeated in the table of contents are treated, and what does the SRC-095 Gold do?",
    "goldTruthDefinition": gold["approval"]["truthDefinition"],
    "goldReviewNote": "Source review: contents entries are non-headings (CONTENTS_ENTRY); the TOC opener and the index group letters are headings.",
    "falsePositivesFromRun": len(fps),
    "falsePositivesByReviewPattern": dict(by_pattern),
    "falsePositivesWhoseTextRepeatsAGoldHeading": len(echo),
    "samples": [{"alias": a, "page": page.get(a), "text": text.get(a), "reviewPattern": pattern_of.get(a, "NOT_LISTED_IN_REVIEW")} for a in fps[:25]],
}
json.dump(out, open(out_path, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
print("FP", len(fps), "by review pattern", dict(by_pattern), "| text repeats a Gold heading:", len(echo))
for s in out["samples"][:12]:
    print(" ", s["alias"], "p", s["page"], s["reviewPattern"], "|", s["text"][:90])
