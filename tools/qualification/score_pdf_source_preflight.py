"""Score a versioned PDF preflight trial without assuming a particular instruction revision."""
import hashlib
import json
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
ART = Path(sys.argv[1]).resolve()
FIXTURES = Path(sys.argv[2]).resolve() if len(sys.argv) > 2 else ART
BASE = ART / "live" / "initial"


def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def metrics(actual, expected):
    return {
        "tp": len(actual & expected), "fp": len(actual - expected), "fn": len(expected - actual),
        "missing": sorted(expected - actual), "extra": sorted(actual - expected),
    }


def main():
    registry = read(ROOT / "eval/a99-closed-loop/gold-current/registry.v1.json")
    rows, stable = [], {}
    for document, authority in {"pdf072": "DOC-0252", "pdf076": "DOC-0256"}.items():
        if not (BASE / document / "trial-start.json").is_file():
            continue
        entry = next(x for x in registry["authorities"] if x["authorityId"] == authority)
        gold_path = ROOT / entry["canonicalGoldPath"]
        if hashlib.sha256(gold_path.read_bytes()).hexdigest() != entry["goldSha256"]:
            raise AssertionError(f"GOLD_HASH_CHANGED:{authority}")
        expected_all = {part["sourceAlias"] for claim in read(gold_path)["semantic"]["claims"] for part in claim["sourceParts"]}
        fixture = read(FIXTURES / f"{document}-scope-fixture.json")
        frozen = {row["unit"]: row["decisions"] for raw in fixture["pipelineAudit"]["rawModelResponses"]
                  if "unit" in (row := json.loads(raw)) and "decisions" in row}
        selections = []
        for repetition in (1, 2):
            folder = BASE / document
            start, outcome = read(folder / "trial-start.json"), read(folder / f"{repetition:02}.outcome.json")
            if start["sourceSha256"] != fixture["input"]["sha256"] or not start["evidenceBeforeFirstClassification"]:
                raise AssertionError(f"TRIAL_IDENTITY_INVALID:{document}")
            events = [json.loads(value) for value in outcome["events"]]
            before, after, evidence_ok, reads = {}, {}, True, 0
            for event in events:
                if event["before"] != frozen[event["unit"]]:
                    raise AssertionError(f"FROZEN_BASELINE_CHANGED:{document}:{event['unit']}")
                before.update(event["before"]); after.update(event["after"])
                request = read(folder / f"{repetition:02}.{event['unit']}.1.request.json")
                packet = json.loads(request["messages"][1]["content"][0]["text"])
                packet_evidence = packet.get("sourceEvidence", {})
                seen = {line["alias"] for result in packet_evidence.get("results", [])
                        for line in result["result"]["lines"]
                        if result.get("status") == "OK" and result.get("tool") == "get_page_layout"}
                evidence_ok &= packet_evidence.get("complete") is True and set(event["before"]) <= seen
                reads += packet_evidence.get("toolCalls", 0)
            actual = {alias for alias, decision in after.items() if decision == "SELECT"}
            expected = expected_all & set(before)
            baseline = {alias for alias, decision in before.items() if decision == "SELECT"}
            candidate, baseline_score = metrics(actual, expected), metrics(baseline, expected)
            complete = len(events) == len(frozen) and not outcome["failures"] and all(event["applied"] for event in events)
            rows.append({
                "document": document, "repetition": repetition, "seconds": outcome["seconds"],
                "targetCount": len(before), "complete": complete, "preflightEvidenceComplete": evidence_ok,
                "programSourceReads": reads, "baseline": baseline_score, "candidate": candidate,
                "failures": outcome["failures"], "confirmedUsd": outcome["resources"]["ConfirmedUsd"],
                "pass": complete and evidence_ok and candidate["fn"] == 0 and candidate["fp"] <= baseline_score["fp"],
            })
            selections.append(actual)
        stable[document] = selections[0] == selections[1]
    expected_documents = {"pdf072", "pdf076"}
    result = {"candidate": ART.name, "rows": rows, "identicalSelectionAcrossRepeats": stable,
              "passToFullExtraction": set(stable) == expected_documents and all(row["pass"] for row in rows) and all(stable.values())}
    (BASE / "score.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
