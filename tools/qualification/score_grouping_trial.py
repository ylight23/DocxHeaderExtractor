"""Score both frozen-input grouping repetitions on both predeclared documents."""
import hashlib
import json
import sys
from pathlib import Path
from score_serial_calls import ROOT, metrics, read

folder = Path(sys.argv[1])
preserve_multiline = "--preserve-multiline" in sys.argv[2:]
registry = read(ROOT / "eval/a99-closed-loop/gold-current/registry.v1.json")
rows = []
complete = True
for doc in ("pdf072", "pdf076"):
    trial = folder / doc
    if not (trial / "trial-start.json").exists():
        complete = False
        continue
    start = read(trial / "trial-start.json")
    authority = next(e for e in registry["authorities"] if e["sourceSha256"] == start["sourceSha256"])
    gold_path = ROOT / authority["canonicalGoldPath"]
    assert hashlib.sha256(gold_path.read_bytes()).hexdigest() == authority["goldSha256"]
    owned = set(start["ownedTargets"])
    expected = {tuple(p["sourceAlias"] for p in c["sourceParts"]) for c in read(gold_path)["semantic"]["claims"]
                if any(p["sourceAlias"] in owned for p in c["sourceParts"])}
    assert all(set(g).issubset(owned) for g in expected)
    for repetition in (1, 2):
        output = trial / f"{repetition:02}.outcome.json"
        if not output.exists():
            complete = False
            continue
        result = read(output)
        groups = [tuple(g) for g in result["Groups"]]
        flattened = [a for g in groups for a in g]
        coverage = len(flattened) == len(set(flattened)) and set(flattened) == set(start["selected"])
        boundaries = metrics(expected, set(groups))
        membership = metrics({a for g in expected for a in g}, set(flattened))
        # PDF072's frozen membership has one known extra; its title group can therefore differ once.
        quality = boundaries["tp"] >= (41 if doc == "pdf072" else len(expected))
        multiline = all(g in set(groups) for g in expected if len(g) > 1)
        gate = coverage and quality and membership["fn"] == 0 and not result["Failures"] and result["Observation"]["FallbackRuns"] == 0 and (not preserve_multiline or multiline)
        rows.append(dict(document=doc, repetition=repetition, seconds=result["seconds"], headingCount=len(groups),
                         membership=membership, boundaries=boundaries, exactCoverage=coverage,
                         failures=result["Failures"], allExpectedMultilineGroupsPreserved=multiline, gatePass=gate, goldSha256=authority["goldSha256"]))
    complete = complete and (trial / "trial-complete.json").exists()
report = dict(complete=complete, passToFullExtraction=complete and len(rows) == 4 and all(r["gatePass"] for r in rows), rows=rows,
              limits="Live grouping only, frozen membership/extents; no end-to-end latency or parent accuracy claim.")
(folder / "score.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print(json.dumps(report, indent=2))
