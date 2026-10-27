"""Score a frozen production-live run: Gold read only after the freeze manifest checks out; tokens and latency per SELECT attempt.
usage: python live_score.py <run-dir>"""
import hashlib, json, re, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
run = Path(sys.argv[1])
read = lambda p: json.loads(Path(p).read_text(encoding="utf-8-sig"))

freeze = read(run / "pre-gold-freeze.json")
assert freeze["status"] == "FROZEN_BEFORE_GOLD"
for name, sha in freeze["files"].items():
    assert hashlib.sha256((run / name).read_bytes()).hexdigest() == sha, "FROZEN_FILE_CHANGED:" + name

result = read(run / "native-run-result.json")["Result"]
units = result["sourceCatalog"]["units"]
aliases = read(run / "aliases.json")
assert len(aliases) == len(units), (len(aliases), len(units))
by_ordinal = {}
for a, u in zip(aliases, units):
    assert a["text"].strip() == u["text"].strip(), (a, u["text"])
    by_ordinal[int(u["sourceOrdinal"])] = a["alias"]

# Attempts: any object carrying PromptTokens + Transport, wherever the run result nests it.
attempts = []
def walk(node):
    if isinstance(node, dict):
        if "PromptTokens" in node and "WireBytes" in node: attempts.append(node)
        for v in node.values(): walk(v)
    elif isinstance(node, list):
        for v in node: walk(v)
walk(read(run / "native-run-result.json"))
seen, uniq = set(), []
for a in attempts:
    if a["AttemptId"] in seen: continue
    seen.add(a["AttemptId"]); uniq.append(a)
sel = [a for a in uniq if (a.get("Transport") or {}).get("UnitId", "").startswith("SEG-")]
def total(rows, k): return sum((r.get(k) or 0) for r in rows)
secs = [(a.get("Transport") or {}).get("RequestSeconds") or 0 for a in sel]

identity = read(run / "run-identity.json")
registry = read(ROOT / "eval/a99-closed-loop/gold-current/registry.v1.json")
sha = result["documentIdentity"].get("sourceSha256") or result["documentIdentity"].get("SourceSha256")
entry = next(e for e in registry["authorities"] if e["sourceSha256"] == sha)
gold_path = ROOT / entry["canonicalGoldPath"]
assert hashlib.sha256(gold_path.read_bytes()).hexdigest() == entry["goldSha256"]
gold = read(gold_path)
pred = [tuple(by_ordinal[s["sourceOrdinal"]] for s in e["sources"]) for e in result["structure"]["elements"] if e.get("type") == "Heading"]
gg = [tuple(p["sourceAlias"] for p in c["sourceParts"]) for c in gold["semantic"]["claims"]]
P = {a for g in pred for a in g}; G = {a for g in gg for a in g}
score = {
    "run": run.name, "crop": identity["crop"], "wallSeconds": round(identity["durationSeconds"], 1),
    "aliases": {"tp": len(P & G), "fp": len(P - G), "fn": len(G - P)},
    "exactGroups": f"{len(set(pred) & set(gg))}/{len(gg)}",
    "select": {"attempts": len(sel), "wireBytes": total(sel, "WireBytes"), "promptTokens": total(sel, "PromptTokens"),
               "completionTokens": total(sel, "CompletionTokens"), "reasoningTokens": total(sel, "ReasoningTokens"),
               "requestSecondsSum": round(sum(secs), 1), "requestSecondsMax": round(max(secs or [0]), 1),
               "costUsd": round(total(sel, "CostUsd"), 5)},
    "allAttempts": len(uniq), "allCostUsd": round(total(uniq, "CostUsd"), 5),
    "fp": sorted(P - G), "fn": sorted(G - P),
}
(run / "gold-score.json").write_text(json.dumps(score, indent=1), encoding="utf-8")
print(json.dumps({k: v for k, v in score.items() if k not in ("fp", "fn")}))
