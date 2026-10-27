"""Score the fixed membership/scheduler study; never writes Gold or sends labels to a provider."""
import hashlib
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(Path(__file__).resolve().parent))
from score_serial_calls import metrics, read

folder = Path(sys.argv[1])
start = read(folder / "study-start.json")
registry = read(ROOT / "eval/a99-closed-loop/gold-current/registry.v1.json")
golds = {}
for j in start["jobs"]:
    entry = next(e for e in registry["authorities"] if e["sourceSha256"] == j["sourceSha256"])
    p = ROOT / entry["canonicalGoldPath"]
    assert hashlib.sha256(p.read_bytes()).hexdigest() == entry["goldSha256"]
    gold = read(p)
    assert gold["capabilities"].get("structuredSourcePartsEvaluable")
    assert all(p["selectionMode"] == "WHOLE_ALIAS" for c in gold["semantic"]["claims"] for p in c["sourceParts"])
    golds[j["Id"]] = (entry, {p["sourceAlias"] for c in gold["semantic"]["claims"] for p in c["sourceParts"]})
rows = []
for p in sorted(folder.glob("*.outcome.json")):
    r = read(p)
    outcome = r["outcome"]
    entry, expected = golds[r["job"]]
    expected = expected & set(r["targets"])
    actual = {a for a, v in outcome["Decisions"].items() if v == "SELECT"}
    coverage = set(outcome["Decisions"]) == set(r["targets"])
    score = metrics(expected, actual)
    rows.append(dict(phase=r["phase"], job=r["job"], candidate=r["candidate"],
                     configuredParallelism=r["configuredParallelism"], seconds=r["seconds"],
                     sourceSha256=r["sourceSha256"], goldSha256=entry["goldSha256"],
                     status=outcome["Status"], failure=outcome["Failure"], repairs=outcome["RepairsUsed"],
                     coverageComplete=coverage, requestSha256=r["requestSha256"],
                     membership=score, exactMembership=coverage and outcome["Status"] == "COMPLETE" and not score["fp"] and not score["fn"]))
by_key = {(r["phase"], r["job"]): r for r in rows}
complete = len(rows) == len(start["jobs"]) * len(start["phases"]) and (folder / "study-complete.json").exists()
gate = None
paired = []
if complete:
    checks = {}
    for job in start["jobs"]:
        a, bp, bs, ap = [by_key[(p["Id"], job["Id"])] for p in start["phases"]]
        assert a["requestSha256"] == ap["requestSha256"] and bp["requestSha256"] == bs["requestSha256"]
        if job["Source"] == 0:
            checks[job["Id"]] = bp["exactMembership"] and bs["exactMembership"]
        else:
            checks[job["Id"]] = all(b["status"] == "COMPLETE" and b["coverageComplete"] and
                b["membership"]["precision"] + 1e-12 >= a0["membership"]["precision"] and
                b["membership"]["recall"] + 1e-12 >= a0["membership"]["recall"] for a0, b in [(a, bs), (ap, bp)])
        paired.append(dict(job=job["Id"], sameRequestsWithinProfile=True,
                           AserialExact=a["exactMembership"], AparallelExact=ap["exactMembership"],
                           BserialExact=bs["exactMembership"], BparallelExact=bp["exactMembership"]))
    gate = dict(passToFullExtraction=all(checks.values()), checks=checks)
result = dict(complete=complete, gate=gate, rows=rows, paired=paired,
              concurrencyInterpretation="Raw NOT_SELECT and complete ownership rule out a collector drop in the audited misses. Small live samples cannot isolate provider load from inference variation.")
(folder / "score.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
print(json.dumps(dict(complete=complete, gate=gate, results=[dict(phase=r["phase"], job=r["job"],
    tp=r["membership"]["tp"], fp=r["membership"]["fp"], fn=r["membership"]["fn"],
    missing=r["membership"]["missing"], extra=r["membership"]["extra"]) for r in rows]), indent=2))
