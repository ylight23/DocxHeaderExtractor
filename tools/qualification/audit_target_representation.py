"""Audit reconstructed historical requests and score the paired representation trial offline."""
import argparse
import hashlib
import json
from pathlib import Path

p = argparse.ArgumentParser()
p.add_argument("output", type=Path)
args = p.parse_args()
manifest = json.loads((args.output / "packet-parity.json").read_text(encoding="utf-8-sig"))
geometry_trial = "pageGeometryEvidence" in manifest["intervention"]
root = Path(__file__).resolve().parents[2]
def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))
def packet(wire):
    return json.loads(wire["messages"][1]["content"][0]["text"])
def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

historical = {}
for version, folder, run in [(13, "pdf-production-block-evidence-v13-20261025", "01"),
                              (14, "pdf-production-unit-selection-v14-20261025", "03")]:
    path = root / "artifacts/qualification" / folder / "live/pdf072" / run / "model-replies.ndjson"
    replies = [json.loads(line) for line in path.read_text(encoding="utf-8-sig").splitlines()]
    historical[version] = {r["Unit"]: r["RequestSha256"] for r in replies if r["Turn"] == 1}

audit = []
for segment in range(15):
    files = {v: args.output / f"historical-v{v}-SEG-{segment:03}.request.json" for v in (13, 14)}
    wires = {v: read(f) for v, f in files.items()}
    packets = {v: packet(w) for v, w in wires.items()}
    a, b = packets[13], packets[14]
    targets_a = a["targets"]
    targets_b = [m for u in b["targetUnits"] for m in u["members"]]
    strip = lambda t: {k: t[k] for k in ("alias", "page", "text")}
    facts_b = [t["facts"] for t in targets_b]
    facts_a = [{k: value for k, value in f.items() if k != "layoutBlockEvidence"} for f in a["targetFacts"]]
    settings = lambda w: {k: v for k, v in w.items() if k != "messages"}
    audit.append(dict(segment=segment, page=segment + 1,
        reconstructionMatchesActualFirstRequest={str(v): sha(files[v]) == historical[v].get(f"SEG-{segment:03}") for v in (13, 14)},
        targetTextOrderEqual=list(map(strip, targets_a)) == list(map(strip, targets_b)),
        baseTargetFactsEqual=facts_a == facts_b,
        contextEqual=a["context"] == b["context"],
        stylesEqual=a["documentStyles"] == b["documentStyles"],
        generationSettingsAndToolsEqual=settings(wires[13]) == settings(wires[14]),
        contextFactsPresent={str(v): "contextFacts" in packets[v] for v in (13, 14)},
        v13HasExtendedBlockEvidence=all("layoutBlockEvidence" in f for f in a["targetFacts"]),
        v14HasExtendedBlockEvidence=any("layoutBlockEvidence" in f for f in facts_b),
        systemPromptEqual=wires[13]["messages"][0] == wires[14]["messages"][0],
        semanticInstructionEqual=a["instruction"] == b["instruction"]))

report = dict(historicalRequestAudit=audit,
    provenance="Only entries with reconstructionMatchesActualFirstRequest=true are byte-matched historical requests; mismatches are current-source reconstructions, not archived requests.",
    sourceAuditFindings=[
        "V13 and V14 both use whole pages on PDF072.",
        "V13 carries extended layoutBlockEvidence; V14 carries block containers but drops that expanded per-target field.",
        "Neither historical first packet contains contextFacts; tools may supply additional evidence later.",
        "AgentSource constructs PdfFormat with requirePageLayoutEvidence:true for includeLayoutBlockEvidence and unitSelection alike. SelectionTask V13 enforces that gate; PdfUnitSelectionTask V14 does not enforce it.",
        "V14 parser does not preserve production headingExtent, UNRESOLVED/CLARIFICATION_REQUIRED handling or strict extra/repeated-key validation.",
        "V14 planner measures AgentUnit.PrepareFirstRequest rather than the custom unit request; its reported ResourcePlan request hash/size is not the actual V14 selection packet.",
        "The controlled arms use one common source-bound parser and system instruction; contextFacts and extended block evidence are identical in both arms. " +
        ("Only pageGeometryEvidence is added on page 14." if geometry_trial else "Only targets nesting differs.")
    ])
report["intervention"] = manifest["intervention"]
if geometry_trial:
    geometry = read(args.output / "page-14-geometry.json")
    report["geometryValidation"] = dict(version=geometry["version"], rasterSha256=geometry["rasterSha256"],
        mappedAliases=len(geometry["occurrences"]), aliasesWithoutInk=geometry["aliasesWithoutInk"],
        frameCount=len(geometry["drawnFrames"]), whitespaceBandCount=len(geometry["blankHorizontalBandsPx"]),
        aliasesWithMultipleInkRuns=[o["alias"] for o in geometry["occurrences"] if len(o["horizontalInkRunsPx"]) > 1])
registry = read(root / "eval/a99-closed-loop/gold-current/registry.v1.json")
entry = next(e for e in registry["authorities"] if e["authorityId"] == "DOC-0252")
gold_path = root / entry["canonicalGoldPath"]
if sha(gold_path) != entry["goldSha256"]:
    raise ValueError("GOLD_HASH_MISMATCH")
gold = read(gold_path)
expected_all = {p["sourceAlias"] for c in gold["semantic"]["claims"] for p in c["sourceParts"]}
scores = {}
for arm in ("alias", "unit", "geometry"):
    outcome_path = args.output / f"{arm}-outcome.json"
    if not outcome_path.exists():
        continue
    result = read(outcome_path)
    outcome = result["outcome"]
    decisions = outcome["Decisions"]
    owned = set(decisions)
    selected = {a for a, d in decisions.items() if d == "SELECT"}
    expected = expected_all & owned
    scores[arm] = dict(status=outcome["Status"], failure=outcome["Failure"], seconds=result["seconds"],
        tp=len(selected & expected) if outcome["Status"] == "COMPLETE" else None,
        fp=len(selected - expected) if outcome["Status"] == "COMPLETE" else None,
        fn=len(expected - selected) if outcome["Status"] == "COMPLETE" else None,
        extra=sorted(selected - expected) if outcome["Status"] == "COMPLETE" else None,
        missing=sorted(expected - selected) if outcome["Status"] == "COMPLETE" else None,
        unknown=[a for a, d in decisions.items() if d not in ("SELECT", "NOT_SELECT")],
        posts=outcome["Posts"], selected=sorted(selected))
report["controlledPage14Scores"] = scores
end_to_end = {}
for arm in ("alias", "geometry"):
    path = args.output / f"{arm}-final.json"
    if not path.exists():
        continue
    final = read(path)
    expected = expected_all & set(final["finalStates"])
    selected = set(final["selectedAliases"])
    expected_groups = {tuple(p["sourceAlias"] for p in c["sourceParts"]) for c in gold["semantic"]["claims"]
                       if all(p["sourceAlias"] in final["finalStates"] for p in c["sourceParts"])}
    actual_groups = set(map(tuple, final["finalGroups"]))
    grouped = read(args.output / f"{arm}-group.json")["result"]
    end_to_end[arm] = dict(seconds=final["seconds"],
        selection=scores.get(arm),
        grouping=dict(groups=grouped["Groups"], failures=grouped["Failures"], observation=grouped["Observation"]),
        finalMembership=dict(tp=len(selected & expected), fp=len(selected - expected), fn=len(expected - selected),
                             extra=sorted(selected - expected), missing=sorted(expected - selected)),
        exactBoundaries=dict(tp=len(actual_groups & expected_groups), fp=len(actual_groups - expected_groups), fn=len(expected_groups - actual_groups)),
        verifierMembership=final["Memberships"], withheld=final["withheldAliases"], failures=final["Failures"],
        unresolved=[a for a, state in final["finalStates"].items() if state == "UNRESOLVED"],
        finalHeadings=final["finalHeadings"],
        limitation="Qualification semantic verifier explicitly enabled; this is not the current production path. Whole-document hierarchy and recall of unselected headings are not verified.")
report["endToEndPage14"] = end_to_end
report["endToEndNotCompleted"] = {arm: dict(selectionStatus=scores[arm]["status"], failure=scores[arm]["failure"],
    downstream="GROUP and VERIFY not run because SELECT did not complete")
    for arm in scores if scores[arm]["status"] != "COMPLETE" and arm not in end_to_end}
report["limit"] = "One pair on page 14 tests feasibility and yields a diagnostic observation. It cannot establish variance, whole-document accuracy, or causal effects of the old V14 output contract. No grouping or hierarchy is scored."
(args.output / "audit-and-score.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print(json.dumps(dict(historicalByteMatches={str(v): sum(r["reconstructionMatchesActualFirstRequest"][str(v)] for r in audit) for v in (13, 14)}, scores=scores, endToEnd=end_to_end), indent=2))
