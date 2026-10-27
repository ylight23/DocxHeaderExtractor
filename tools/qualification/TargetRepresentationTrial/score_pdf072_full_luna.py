"""Score a frozen whole-document PDF072 V12 Luna run against registered structured Gold."""
import hashlib
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[3]

def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))

def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()

def membership(expected, predicted):
    return {
        "tp": len(expected & predicted), "fp": len(predicted - expected), "fn": len(expected - predicted),
        "precision": len(expected & predicted) / len(predicted) if predicted else 0,
        "recall": len(expected & predicted) / len(expected) if expected else 0,
        "falsePositiveAliases": sorted(predicted - expected), "missingAliases": sorted(expected - predicted),
    }

def boundaries(expected, actual):
    return {
        "tp": len(expected & actual), "fp": len(actual - expected), "fn": len(expected - actual),
        "precision": len(expected & actual) / len(actual) if actual else 0,
        "recall": len(expected & actual) / len(expected) if expected else 0,
        "falsePositiveGroups": [list(g) for g in sorted(actual - expected)],
        "missingGroups": [list(g) for g in sorted(expected - actual)],
    }

if len(sys.argv) != 2:
    raise SystemExit("usage: score_pdf072_full_luna.py <frozen-run-directory>")
run = Path(sys.argv[1]).resolve()
registry_path = ROOT / "eval/a99-closed-loop/gold-current/registry.v1.json"
registry = read(registry_path)
identity = read(run / "run-identity.json")
entry = next(e for e in registry["authorities"] if e["sourceSha256"] == identity["SourceSha256"])
gold_path = ROOT / entry["canonicalGoldPath"]
if sha(gold_path) != entry["goldSha256"]:
    raise ValueError("GOLD_HASH_MISMATCH")
gold = read(gold_path)
if gold["source"]["sourceSha256"] != identity["SourceSha256"]:
    raise ValueError("GOLD_SOURCE_HASH_MISMATCH")

claims = gold["semantic"]["claims"]
gold_groups = {tuple(part["sourceAlias"] for part in claim["sourceParts"]) for claim in claims}
gold_aliases = {a for g in gold_groups for a in g}
selection = read(run / "selection.json")
outcomes = selection["outcomes"]
decisions = {a: d for outcome in outcomes for a, d in outcome["Decisions"].items()}
owned = set(decisions)
if len(owned) != sum(len(o["Decisions"]) for o in outcomes):
    raise ValueError("DUPLICATE_SELECTION_OWNER")
if any(not set(g).issubset(owned) for g in gold_groups):
    raise ValueError("GOLD_ALIAS_OUTSIDE_OWNED_UNIVERSE")
expected_aliases = gold_aliases & owned
expected_groups = {g for g in gold_groups if set(g).issubset(owned)}
selected = {a for a, d in decisions.items() if d == "SELECT"}
grouping = read(run / "grouping.json")["result"]
baseline_groups = {tuple(g) for g in grouping["Groups"]}
final = read(run / "final-headings.json")
verified_groups = {tuple(g) for g in final["postVerifyGroups"]}
verified_aliases = {a for g in verified_groups for a in g}
if verified_groups != {tuple(g) for g in final["productionBaselineGroups"]}:
    raise ValueError("BASELINE_VERIFY_GROUPS_DIFFER; inspect separate outputs")
if baseline_groups != {tuple(g) for g in final["productionBaselineGroups"]}:
    raise ValueError("BASELINE_GROUP_EXPORT_MISMATCH")
if len(verified_aliases) != sum(len(g) for g in verified_groups):
    raise ValueError("DUPLICATE_GROUP_ALIAS")

result = {
    "authorityId": entry["authorityId"], "goldSha256": entry["goldSha256"],
    "registrySha256": sha(registry_path), "sourceSha256": identity["SourceSha256"],
    "scope": {"ownedAliases": len(owned), "goldAliasesInScope": len(expected_aliases), "goldGroupsInScope": len(expected_groups)},
    "productionGroupMembership": membership(expected_aliases, {a for g in baseline_groups for a in g}),
    "postVerifyMembership": membership(expected_aliases, verified_aliases),
    "productionGroupBoundaries": boundaries(expected_groups, baseline_groups),
    "postVerifyBoundaries": boundaries(expected_groups, verified_groups),
    "baselineEqualsPostVerify": baseline_groups == verified_groups,
    "groupingFailures": grouping["Failures"], "verificationFailures": read(run / "verification.json")["result"]["Failures"],
    "verificationUnresolvedAliases": read(run / "verification.json")["result"]["UnresolvedAliases"],
    "placement": {"contract": read(run / "placement.json")["contract"], "requested": read(run / "placement.json")["audit"]["RequestedCount"],
        "placed": read(run / "placement.json")["audit"]["PlacedCount"], "unresolved": read(run / "placement.json")["audit"]["UnresolvedCount"],
        "logicalCalls": read(run / "placement.json")["audit"]["LogicalCalls"], "repairCalls": read(run / "placement.json")["audit"]["RepairCount"],
        "graphConflicts": read(run / "placement.json")["audit"]["GraphConflicts"]},
    "goldCapabilities": gold["capabilities"],
    "limitations": ["Gold hierarchy capability is false; hierarchy quality is unscored.",
        "This is one Luna-authored whole-document run; no provider-quality, latency-distribution, or Qwen claim is made."],
}
(run / "gold-score.json").write_text(json.dumps(result, indent=2, ensure_ascii=False), encoding="utf-8")
print(json.dumps(result, indent=2, ensure_ascii=False))
