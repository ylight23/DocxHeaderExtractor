"""Evaluate the two predeclared PDF072 V5 samples; never change Gold or select a winning sample."""
import argparse
import json
from pathlib import Path
from score_serial_calls import read, score


def evaluate(run_path, owned_targets, require_reviews=True, preserve_multiline=False):
    result = read(run_path)
    measured = score(run_path)
    messages = [json.loads(s) for s in result["pipelineAudit"]["rawModelResponses"] if s.startswith("{")]
    units = [m for m in messages if "unit" in m and "decisions" in m]
    owned = [a for m in units for a in m["decisions"]]
    reviews = [m for m in messages if m.get("stage") == "boundary-selection-review"]
    placement = result["placementExecution"]
    chain = placement.get("ChainRepair") or {}
    gates = dict(
        noMissingAliases=measured["aliasMembership"]["fn"] == 0,
        atMostThreeExtraAliases=measured["aliasMembership"]["fp"] <= 3,
        atLeast41ExactGoldGroups=measured["exactAliasBoundaries"]["tp"] >= 41,
        noMultilineTitleSplit=(not preserve_multiline or all(len(g) == 1 for g in measured["exactAliasBoundaries"]["missing"])),
        completeScopeCoverage=(len(units) == 15 and len({m["unit"] for m in units}) == 15
                               and len(owned) == len(set(owned)) and set(owned) == owned_targets),
        completeReviews=(len(reviews) == (2 if require_reviews else 0) and all(m["applied"] and m["Status"] == "COMPLETE"
                                                 and not m.get("Failure") for m in reviews)),
        noUnknownMembership=all(d in ("SELECT", "NOT_SELECT") for m in units for d in m["decisions"].values()),
        noGroupingFallback=measured["grouping"]["FallbackRuns"] == 0,
        noGroupingUnresolved=measured["grouping"]["UnresolvedAliases"] == 0,
        noPlacementFailures=not placement.get("Failures"),
        # GraphConflicts retains the before-chain-repair audit history even after successful repair.
        noResidualGraphConflicts=(not placement.get("GraphUnresolvedAliases") and
                                 not [c for c in placement.get("GraphConflicts", [])
                                      if c.get("Checkpoint") != "before-chain-repair"]),
        noResidualChainConflicts=not chain.get("FinalConflicts"),
        atMostFiveUnresolvedParents=measured["unresolvedParents"] <= 5,
        under300Seconds=measured["elapsedSeconds"] < 300,
    )
    return dict(**measured, gates=gates, gatePass=all(gates.values()), requiredReviews=2 if require_reviews else 0,
                reviewEvents=reviews, placementFailures=placement.get("Failures", []),
                chainRepair=chain,
                limits="Gold evaluates alias membership and exact groups, not hierarchy correctness. Two samples do not establish a general latency SLA.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("folder")
    parser.add_argument("--controlled-kind", choices=["membership", "grouping", "initial"], default="membership")
    parser.add_argument("--preserve-multiline", action="store_true")
    parser.add_argument("--coverage-reference")
    args = parser.parse_args()
    folder = Path(args.folder)
    start = read(folder / "controlled/pdf072/trial-start.json")
    owned = set(start["ownedTargets"])
    if args.coverage_reference:
        reference = read(args.coverage_reference)
        if reference["input"]["sha256"] != start["sourceSha256"]:
            raise ValueError("Coverage reference belongs to another source")
        ref_messages = [json.loads(s) for s in reference["pipelineAudit"]["rawModelResponses"] if s.startswith("{")]
        owned = set(next(m["aliases"] for m in ref_messages if m.get("stage") == "pre-binding-alias-state"))
    elif args.controlled_kind == "initial":
        parser.error("Initial subset trials require a full --coverage-reference")
    rows = []
    for sample in ("full-01", "full-02"):
        run = folder / sample / "run-result.json"
        if not run.exists():
            continue
        row = evaluate(run, owned, require_reviews=args.controlled_kind == "membership", preserve_multiline=args.preserve_multiline)
        (run.parent / "score.json").write_text(json.dumps(row, indent=2), encoding="utf-8")
        rows.append(row)
    complete = len(rows) == 2
    controlled = read(folder / "controlled/score.json")["passToFullExtraction"]
    report = dict(complete=complete, controlledGate=controlled, fullGatePass=complete and controlled
                  and all(r["gatePass"] for r in rows), samples=rows)
    (folder / "full-score.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(dict(complete=complete, controlledGate=controlled, fullGatePass=report["fullGatePass"],
                         samples=[{k: r[k] for k in ("run", "elapsedSeconds", "headingCount", "aliasMembership",
                                  "exactAliasBoundaries", "unresolvedParents", "gates", "gatePass")} for r in rows]), indent=2))
