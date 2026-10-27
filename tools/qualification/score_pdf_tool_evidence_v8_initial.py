"""Score the bounded required-page-layout-evidence trial against current Gold and the frozen V6 scopes."""
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
BASE = ROOT / "artifacts/qualification/pdf-tool-evidence-v8-20261025/live/initial-postcap4"


def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def page_layout_aliases(replies):
    evidence = {}
    tool_calls = 0
    successful_results = 0
    for reply in replies:
        unit = reply["Unit"]
        evidence.setdefault(unit, set())
        tool_calls += sum(call.get("Name") == "get_page_layout" for call in reply.get("ToolCalls", []))
        for tool_result in reply.get("ToolResults", []):
            try:
                result = json.loads(tool_result["Content"])
            except (KeyError, TypeError, json.JSONDecodeError):
                continue
            if result.get("tool") != "get_page_layout" or result.get("status") != "OK":
                continue
            successful_results += 1
            evidence[unit].update(line["alias"] for line in result.get("result", {}).get("lines", []))
    return evidence, tool_calls, successful_results


def main():
    registry = read(ROOT / "eval/a99-closed-loop/gold-current/registry.v1.json")
    docs = {"pdf072": "DOC-0252", "pdf076": "DOC-0256"}
    scores = []
    for name, authority in docs.items():
        entry = next(r for r in registry["authorities"] if r["authorityId"] == authority)
        gold_path = ROOT / entry["canonicalGoldPath"]
        assert hashlib.sha256(gold_path.read_bytes()).hexdigest() == entry["goldSha256"]
        gold = read(gold_path)
        expected_all = {p["sourceAlias"] for c in gold["semantic"]["claims"] for p in c["sourceParts"]}
        assert all(p["selectionMode"] == "WHOLE_ALIAS" for c in gold["semantic"]["claims"] for p in c["sourceParts"])
        fixture = read(ROOT / f"artifacts/qualification/pdf-tool-evidence-v8-20261025/{name}-scope-fixture.json")
        source_hash = fixture["input"]["sha256"]
        folder = BASE / name
        start = read(folder / "trial-start.json")
        assert start["sourceSha256"] == source_hash
        assert start["initial"] and start["repetitions"] == 2 and start["instructionVersion"] == 8
        assert start["maxPostsPerUnit"] == 4
        frozen = {}
        for raw in fixture["pipelineAudit"]["rawModelResponses"]:
            msg = json.loads(raw)
            if "unit" in msg and "decisions" in msg:
                frozen[msg["unit"]] = msg["decisions"]
        for repetition in (1, 2):
            outcome = read(folder / f"{repetition:02}.outcome.json")
            assert outcome["sourceSha256"] == source_hash
            events = [json.loads(e) if isinstance(e, str) else e for e in outcome["events"]]
            assert len(events) == len(frozen) and not outcome["failures"]
            before = {}
            after = {}
            unit_targets = {}
            for event in events:
                unit = event["unit"]
                assert event["applied"] and event["Status"] == "COMPLETE"
                assert event["before"] == frozen[unit]
                assert set(event["before"]) == set(event["after"])
                before.update(event["before"])
                after.update(event["after"])
                unit_targets[unit] = set(event["after"])

            replies = read(folder / f"{repetition:02}.replies.json")
            evidence, tool_calls, successful_results = page_layout_aliases(replies)
            covered = {unit: targets <= evidence.get(unit, set()) for unit, targets in unit_targets.items()}
            targets = set(before)
            expected = expected_all & targets
            before_selected = {a for a, d in before.items() if d == "SELECT"}
            after_selected = {a for a, d in after.items() if d == "SELECT"}
            rows = {}
            for label, actual in [("before", before_selected), ("after", after_selected)]:
                tp = len(expected & actual)
                rows[label] = {"tp": tp, "fp": len(actual - expected), "fn": len(expected - actual),
                               "missing": sorted(expected - actual), "extra": sorted(actual - expected)}
            row = {"document": name, "authorityId": authority, "goldSha256": entry["goldSha256"],
                   "repetition": repetition, "scopeCount": len(events), "targetCount": len(targets),
                   "completeTargetCoverage": all(len(e["after"]) == len(e["before"]) for e in events),
                   "allTargetsHavePageLayoutEvidence": all(covered.values()), "pageLayoutCoverageByUnit": covered,
                   "pageLayoutToolCalls": tool_calls, "successfulPageLayoutResults": successful_results,
                   "seconds": outcome["seconds"], "membership": rows,
                   "requestHashes": [h for e in events for h in e["RequestSha256s"]],
                   "modelReplies": len(replies), "resources": outcome["resources"]}
            row["pass"] = (rows["after"]["fn"] == 0 and rows["after"]["fp"] <= rows["before"]["fp"]
                           and row["completeTargetCoverage"] and row["allTargetsHavePageLayoutEvidence"])
            scores.append(row)
    summary = {"candidate": "general-structural-v8-required-page-layout-evidence",
               "stage": "bounded-initial-selection", "perUnitPostCap": 4,
               "newModelCalls": sum(r["modelReplies"] for r in scores), "rows": scores,
               "passToFullExtraction": len(scores) == 4 and all(r["pass"] for r in scores)}
    (BASE / "score.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(json.dumps({"passToFullExtraction": summary["passToFullExtraction"],
                      "rows": [{k: r[k] for k in ["document", "repetition", "seconds", "pageLayoutToolCalls",
                               "allTargetsHavePageLayoutEvidence", "membership", "pass"]} for r in scores]}, indent=2))


if __name__ == "__main__":
    main()
