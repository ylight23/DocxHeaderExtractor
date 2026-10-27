"""Strict independent full-document gate against one registered whole-alias Gold authority."""
import argparse
import json
from pathlib import Path
from score_serial_calls import read, score

p = argparse.ArgumentParser()
p.add_argument("run")
p.add_argument("--authority", required=True)
args = p.parse_args()
run = read(args.run)
measured = score(args.run, args.authority)
messages = [json.loads(s) for s in run["pipelineAudit"]["rawModelResponses"] if s.startswith("{")]
units = {m["unit"]: m for m in messages if "unit" in m and "decisions" in m}
scopes = run["pipelineAudit"]["ResourcePlan"]["Scopes"]
owned = [a for m in units.values() for a in m["decisions"]]
state = next(m["aliases"] for m in messages if m.get("stage") == "pre-binding-alias-state")
lane = run["pipelineAudit"]["semanticLane"]
failures = json.loads(lane["failureClass"]) if lane.get("failureClass") else {}
placement = run["placementExecution"]
coverage = (len(units) == len(scopes) == lane["completed"] == lane["scheduled"] and
            sum(s["TargetCount"] for s in scopes) == len(owned) == len(set(owned)) == run["pipelineAudit"]["sourceBlocksAvailable"] and
            set(owned) == set(state))
for s in scopes:
    decisions = units.get(f"SEG-{s['Segment']:03}", {}).get("decisions", {})
    # Selection validates an exact target set; it does not require decisions in response order.
    coverage = coverage and (len(decisions) == s["TargetCount"] and
                             s["FirstAlias"] in decisions and s["LastAlias"] in decisions)
gates = dict(exactMembership=measured["aliasMembership"]["fn"] == measured["aliasMembership"]["fp"] == 0,
             exactGroups=measured["exactAliasBoundaries"]["fn"] == measured["exactAliasBoundaries"]["fp"] == 0,
             completeCoverage=coverage,
             noUnknownMembership=all(s["FinalSelection"] in ("SELECT", "NOT_SELECT") for s in state.values()),
             noSelectionFailures=not failures.get("unitFailures") and not failures.get("selectedAliasesNotInSource") and not failures.get("headingExtentsRejected"),
             noGroupingFallback=measured["grouping"]["FallbackRuns"] == 0 and measured["grouping"]["UnresolvedAliases"] == 0,
             noPlacementFailures=not placement.get("Failures"),
             noResidualConflicts=not placement.get("GraphUnresolvedAliases") and not (placement.get("ChainRepair") or {}).get("FinalConflicts"),
             atMostFiveUnresolvedParents=measured["unresolvedParents"] <= 5,
             under300Seconds=measured["elapsedSeconds"] < 300)
report = dict(**measured, gates=gates, gatePass=all(gates.values()),
              limits="One independent document; registered Gold does not score hierarchy and this sample does not establish a general latency SLA.")
Path(args.run).with_name("score.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print(json.dumps({k: report[k] for k in ("elapsedSeconds", "headingCount", "goldHeadingCount", "aliasMembership", "exactAliasBoundaries", "unresolvedParents", "gates", "gatePass")}, indent=2))
