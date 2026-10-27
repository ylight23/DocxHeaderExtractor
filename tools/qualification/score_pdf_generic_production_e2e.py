"""Score a frozen production-generic page-14 SELECT/GROUP/VERIFY artifact against registered Gold."""
import hashlib
import json
from pathlib import Path
import sys

if len(sys.argv) != 2:
    raise SystemExit("usage: score_pdf_generic_production_e2e.py <frozen-run-directory>")
run = Path(sys.argv[1]).resolve()
root = Path(__file__).resolve().parents[2]
read = lambda path: json.loads(Path(path).read_text(encoding="utf-8-sig"))
digest = lambda path: hashlib.sha256(Path(path).read_bytes()).hexdigest()
registry = read(root / "eval/a99-closed-loop/gold-current/registry.v1.json")
entry = next(e for e in registry["authorities"] if e["authorityId"] == "DOC-0252")
gold_path = root / entry["canonicalGoldPath"]
if digest(gold_path) != entry["goldSha256"]:
    raise ValueError("GOLD_HASH_MISMATCH")
gold = read(gold_path)
claims = gold["semantic"]["claims"]
gold_groups = {tuple(part["sourceAlias"] for part in claim["sourceParts"]) for claim in claims}
gold_aliases = {alias for group in gold_groups for alias in group}
selection = read(run.parent / "01/selection.json")["result"]
final = read(run / "final-headings.json")
grouping = read(run / "grouping.json")["result"]
verification = read(run / "verification.json")["result"]
owned = set(selection["Decisions"])
expected_aliases = gold_aliases & owned
selected_aliases = {alias for alias, decision in selection["Decisions"].items() if decision == "SELECT"}
final_aliases = set(final["selectedAliases"])
expected_groups = {group for group in gold_groups if all(alias in owned for alias in group)}
actual_groups = {tuple(group) for group in final["finalGroups"]}

def membership(predicted):
    return {
        "tp": len(predicted & expected_aliases),
        "fp": len(predicted - expected_aliases),
        "fn": len(expected_aliases - predicted),
        "falsePositiveAliases": sorted(predicted - expected_aliases),
        "missingAliases": sorted(expected_aliases - predicted),
    }

score = {
    "goldAuthority": entry["authorityId"],
    "goldSha256": entry["goldSha256"],
    "sourceSha256": final["SourceSha256"],
    "scope": {"page": 14, "ownedAliases": len(owned), "expectedGoldAliases": len(expected_aliases)},
    "selectionMembership": membership(selected_aliases),
    "finalMembership": membership(final_aliases),
    "exactBoundaries": {
        "tp": len(actual_groups & expected_groups),
        "fp": len(actual_groups - expected_groups),
        "fn": len(expected_groups - actual_groups),
        "falsePositiveGroups": [list(g) for g in sorted(actual_groups - expected_groups)],
        "missingGroups": [list(g) for g in sorted(expected_groups - actual_groups)],
    },
    "grouping": {"failures": grouping["Failures"], "posts": grouping["Posts"], "groups": len(grouping["Groups"])},
    "verification": {"failures": verification["Failures"], "unresolvedAliases": verification["UnresolvedAliases"],
        "membershipJudgements": verification["Memberships"]},
    "verifierRetainedInitialFalsePositives": sorted(final_aliases - expected_aliases),
    "limitation": "Single Luna-authored session on page 14. This does not establish provider/model quality, whole-document hierarchy, or recall for headings outside the selected page scope.",
}
(run / "gold-score.json").write_text(json.dumps(score, indent=2, ensure_ascii=False), encoding="utf-8")
print(json.dumps({k: score[k] for k in ("scope", "selectionMembership", "finalMembership", "exactBoundaries", "grouping", "verification", "verifierRetainedInitialFalsePositives")}, indent=2, ensure_ascii=False))
