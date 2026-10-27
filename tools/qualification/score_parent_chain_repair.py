"""Apply the predeclared PDF072 development gate without changing Gold or production defaults."""
import hashlib
import json
import sys
import xml.etree.ElementTree as ET
from pathlib import Path
from score_serial_calls import ROOT, read, score

folder = Path(sys.argv[1])
candidate_path = folder / "full/run-result.json"
candidate = score(candidate_path)
source_bound = read(ROOT / "artifacts/qualification/pdf072-membership-audit-20261024/full/score.json")
production = read(ROOT / "artifacts/qualification/pdf072-serial-calls-20261024/baseline/score.json")
controlled = read(folder / "controlled/score.json")
run = read(candidate_path)
placement = run["placementExecution"]
repair = placement["ChainRepair"]
identity = read(folder / "source-identity.json")
changes = [v["path"] for v in identity["files"]
           if hashlib.sha256((ROOT / v["path"]).read_bytes()).hexdigest() != v["sha256"]]
tests = ET.parse(ROOT / "tests/DocxHeaderExtractor.Tests/TestResults/parent-chain-regression.trx")
counters = next(v.attrib for v in tests.iter() if v.tag.endswith("Counters"))
gates = {
    "controlledTrial": controlled["controlledGatePass"],
    "sourceUnchanged": not changes,
    "regressionTests": counters["total"] == counters["passed"] == "239",
    "under300Seconds": candidate["elapsedSeconds"] < 300,
    "parentUnresolvedAtMost5": candidate["unresolvedParents"] <= 5,
    "noResidualChainConflicts": not repair["FinalConflicts"],
    "noPlacementFailures": not placement["Failures"],
}
for metric in ["aliasMembership", "exactAliasBoundaries"]:
    for dimension in ["precision", "recall"]:
        gates[f"{metric}.{dimension}NoRegression"] = candidate[metric][dimension] + 1e-12 >= source_bound[metric][dimension]

result = dict(
    developmentGatePass=all(gates.values()), gates=gates,
    productionPromotion=False,
    productionDecision="Keep source binding and parent-chain repair opt-in. " +
        ("Development gate passes; broader qualification and hierarchy review remain."
         if all(gates.values()) else "The predeclared full-document development gate fails."),
    retryFixedProductionBaseline=production, previousSourceBound=source_bound,
    controlledTrial=controlled, fullCandidate=candidate, chainRepair=repair,
    placementCalls=placement["Calls"], testsPassed=int(counters["passed"]),
    sourceFilesVerified=len(identity["files"]), changedSourceFiles=changes,
    parentAccuracyEvaluable=False,
    limits=["DOC-0252 Gold has no hierarchy capability.",
            "One full-document sample cannot establish a p95 or guaranteed five-minute SLA.",
            "The controlled trial holds initial model responses fixed; the full run does not."])
(folder / "full/score.json").write_text(json.dumps(candidate, indent=2), encoding="utf-8")
(folder / "qualification-result.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
print(json.dumps(dict(developmentGatePass=result["developmentGatePass"], gates=gates,
                     seconds=candidate["elapsedSeconds"], membership=candidate["aliasMembership"],
                     boundaries=candidate["exactAliasBoundaries"], unresolvedParents=candidate["unresolvedParents"],
                     repairPasses=repair["Passes"], repairCalls=repair["LogicalCalls"],
                     initialChainConflicts=len(repair["InitialConflicts"]),
                     residualChainConflicts=len(repair["FinalConflicts"])), indent=2))
