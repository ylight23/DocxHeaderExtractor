"""Score both predeclared repetitions of frozen membership repair on PDF072 and PDF076."""
import hashlib
import json
import sys
from pathlib import Path
from score_serial_calls import ROOT, metrics, read

folder = Path(sys.argv[1])
allow_equal_extras = "--allow-equal-extras" in sys.argv[2:]
registry = read(ROOT / "eval/a99-closed-loop/gold-current/registry.v1.json")
rows = []
completed = True
for name in ["pdf072", "pdf076"]:
    trial = folder / name
    if not (trial / "trial-start.json").exists():
        completed = False
        continue
    start = read(trial / "trial-start.json")
    entry = next(e for e in registry["authorities"] if e["sourceSha256"] == start["sourceSha256"])
    gold = ROOT / entry["canonicalGoldPath"]
    assert hashlib.sha256(gold.read_bytes()).hexdigest() == entry["goldSha256"]
    expected = {p["sourceAlias"] for c in read(gold)["semantic"]["claims"] for p in c["sourceParts"]} & set(start["ownedTargets"])
    before = metrics(expected, set(start["beforeSelected"]))
    for repetition in [1, 2]:
        path = trial / f"{repetition:02}.outcome.json"
        if not path.exists():
            completed = False
            continue
        result = read(path)
        score = metrics(expected, set(result["selected"]))
        events = [json.loads(v) for v in result["events"]]
        after_coverage = {a for o in result["outcomes"] for a in o["Decisions"]}
        coverage = after_coverage == set(start["ownedTargets"])
        gate = (not result["failures"] and all(e["applied"] for e in events) and coverage and score["fn"] == 0
                and score["fp"] <= before["fp"] and (allow_equal_extras or not before["fp"] or score["fp"] < before["fp"]))
        rows.append(dict(document=name, repetition=repetition, before=before, after=score,
                         completeCoverage=coverage, failures=result["failures"], allReviewsApplied=all(e["applied"] for e in events),
                         events=events, seconds=result["seconds"], gatePass=gate, goldSha256=entry["goldSha256"]))
    completed = completed and (trial / "trial-complete.json").exists()
gate = completed and len(rows) == 4 and all(r["gatePass"] for r in rows)
report = dict(complete=completed, passToFullExtraction=gate, rows=rows,
              limits="Frozen initial selection; only membership review is live. This is not end-to-end time or grouping/hierarchy accuracy.")
(folder / "score.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print(json.dumps(dict(complete=completed, passToFullExtraction=gate, rows=[
    {k: v for k, v in r.items() if k not in ["events", "goldSha256"]} for r in rows]), indent=2))
