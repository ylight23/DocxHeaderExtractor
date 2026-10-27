"""Score a finished bridge run against canonical Gold (read only after the run has finished).
usage: python score.py <run-dir>"""
import hashlib, json, re, sys
from pathlib import Path

ROOT = Path("C:/DocxHeaderExtractor-a99-closed-loop")
run = Path(sys.argv[1])
read = lambda p: json.loads(Path(p).read_text(encoding="utf-8-sig"))
key = lambda a: tuple(int(x) for x in re.fullmatch(r"L(\d+):S(\d+)", a).groups())

identity = read(run / "run-identity.json")
result = read(run / "native-run-result.json")["Result"]
units = result["sourceCatalog"]["units"]

members = {}
for req in (run / "luna-requests").glob("*.request.json"):
    body = read(req)
    content = body["messages"][1]["content"]
    text = content[0]["text"] if isinstance(content, list) else content
    try:
        packet = json.loads(text)
    except Exception:
        continue
    for unit in packet.get("targetUnits", []):
        for m in unit["members"]:
            members[m["alias"]] = m["text"]
    for t in packet.get("targets", []):
        if isinstance(t, dict) and "alias" in t and "text" in t:
            members[t["alias"]] = t["text"]
aliases = sorted(members, key=key)
if len(aliases) != len(units):
    sys.exit(f"ALIAS_COVERAGE_MISMATCH aliases={len(aliases)} units={len(units)}")
by_ordinal = {}
for alias, unit in zip(aliases, units):
    if members[alias].strip() != unit["text"].strip():
        sys.exit(f"ALIAS_TEXT_MISMATCH {alias}: {members[alias]!r} vs {unit['text']!r}")
    by_ordinal[int(unit["sourceOrdinal"])] = alias

registry = read(ROOT / "eval/a99-closed-loop/gold-current/registry.v1.json")
entry = next(e for e in registry["authorities"] if e["sourceSha256"] == identity["sourceSha256"])
gold_path = ROOT / entry["canonicalGoldPath"]
assert hashlib.sha256(gold_path.read_bytes()).hexdigest() == entry["goldSha256"], "GOLD_HASH_MISMATCH"
gold = read(gold_path)

pred_groups = [tuple(by_ordinal[s["sourceOrdinal"]] for s in e["sources"])
               for e in result["structure"]["elements"] if e.get("type") == "Heading"]
gold_groups = [tuple(p["sourceAlias"] for p in c["sourceParts"]) for c in gold["semantic"]["claims"]]
P = {a for g in pred_groups for a in g}; G = {a for g in gold_groups for a in g}
tp, fp, fn = P & G, P - G, G - P
exact = set(pred_groups) & set(gold_groups)
wrong_groups = [g for g in pred_groups if g not in set(gold_groups)]
text = {a: members[a] for a in aliases}
score = {
    "run": run.name, "gold": entry["canonicalGoldPath"], "goldSha256": entry["goldSha256"],
    "aliases": {"tp": len(tp), "fp": len(fp), "fn": len(fn),
                "precision": round(len(tp) / len(P), 4) if P else 0, "recall": round(len(tp) / len(G), 4) if G else 0},
    "groups": {"gold": len(gold_groups), "predicted": len(pred_groups), "exact": len(exact), "wrong": len(wrong_groups)},
    "falsePositives": [[a, text[a]] for a in sorted(fp, key=key)],
    "misses": [[a, text.get(a, "?")] for a in sorted(fn, key=key)],
    "wrongGroups": [[list(g), " | ".join(text[a] for a in g)] for g in wrong_groups],
}
(run / "gold-score.json").write_text(json.dumps(score, indent=1, ensure_ascii=False), encoding="utf-8")
print(json.dumps({k: score[k] for k in ("aliases", "groups")}))
print("FP:", score["falsePositives"]); print("FN:", score["misses"]); print("WRONG GROUPS:", score["wrongGroups"])
