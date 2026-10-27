"""Score frozen scopes and independently check evidence existed in the first classification request."""
import hashlib
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ART = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else ROOT / "artifacts/qualification/pdf-source-preflight-v9-20261025"
BASE = ART / "live/initial"


def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def metrics(actual, expected):
    return dict(tp=len(actual & expected), fp=len(actual - expected), fn=len(expected - actual),
                missing=sorted(expected - actual), extra=sorted(actual - expected))


def main():
    registry = read(ROOT / "eval/a99-closed-loop/gold-current/registry.v1.json")
    rows = []
    selected_by_document = {}
    for document, authority in {"pdf072": "DOC-0252", "pdf076": "DOC-0256"}.items():
        entry = next(x for x in registry["authorities"] if x["authorityId"] == authority)
        gold_path = ROOT / entry["canonicalGoldPath"]
        assert hashlib.sha256(gold_path.read_bytes()).hexdigest() == entry["goldSha256"]
        gold = read(gold_path)
        expected_all = {p["sourceAlias"] for c in gold["semantic"]["claims"] for p in c["sourceParts"]}
        fixture = read(ART / f"{document}-scope-fixture.json")
        frozen = {r["unit"]: r["decisions"] for raw in fixture["pipelineAudit"]["rawModelResponses"]
                  if "unit" in (r := json.loads(raw)) and "decisions" in r}
        folder = BASE / document
        start = read(folder / "trial-start.json")
        assert start["instructionVersion"] == 9 and start["evidenceBeforeFirstClassification"]
        assert start["sourceSha256"] == fixture["input"]["sha256"]
        selections = []
        for repetition in (1, 2):
            run = read(folder / f"{repetition:02}.outcome.json")
            events = [json.loads(x) for x in run["events"]]
            before, after = {}, {}
            evidence_checks = []
            local_reads = 0
            for event in events:
                unit = event["unit"]
                assert event["before"] == frozen[unit]
                before.update(event["before"])
                after.update(event["after"])
                request = read(folder / f"{repetition:02}.{unit}.1.request.json")
                assert len(request["messages"]) == 2
                packet = json.loads(request["messages"][1]["content"][0]["text"])
                evidence = packet["sourceEvidence"]
                observed = set()
                for result in evidence["results"]:
                    assert result["status"] == "OK" and result["tool"] == "get_page_layout"
                    assert result["provenance"]["sourceSha256"] == run["sourceSha256"]
                    observed.update(l["alias"] for l in result["result"]["lines"])
                evidence_checks.append(evidence["complete"] and set(event["before"]) <= observed
                                       and set(evidence["coveredTargetAliases"]) == set(event["before"]))
                local_reads += evidence["toolCalls"]
            targets = set(before)
            expected = expected_all & targets
            actual = {a for a, d in after.items() if d == "SELECT"}
            baseline = {a for a, d in before.items() if d == "SELECT"}
            candidate_score = metrics(actual, expected)
            baseline_score = metrics(baseline, expected)
            replies = read(folder / f"{repetition:02}.replies.json")
            review_events = [json.loads(x) for x in run.get("nameReviewEvents", [])]
            review_reads = 0
            bounded_review_valid = True
            for event in review_events:
                candidates = set(event["candidateAliases"])
                changed = {a for a in event["before"] if event["before"][a] != event["after"][a]}
                bounded_review_valid &= (event["applied"] and changed <= candidates
                                         and all(event["before"][a] == "SELECT" and event["after"][a] == "NOT_SELECT" for a in changed))
                request = read(folder / f'{repetition:02}.{event["reviewUnit"]}.1.request.json')
                assert len(request["messages"]) == 2
                packet = json.loads(request["messages"][1]["content"][0]["text"])
                review_reads += packet["sourceEvidence"]["toolCalls"]
                assert packet["sourceEvidence"]["complete"]
                assert "previousDecisions" not in packet["selectionReview"]
            complete = len(events) == len(frozen) and not run["failures"] and all(e["applied"] for e in events)
            row = dict(document=document, repetition=repetition, seconds=run["seconds"], targetCount=len(targets),
                       sourcePreparationSeconds=start.get("sourcePreparationSeconds"),
                       requestBudgetPreparationSeconds=run.get("requestBudgetPreparationSeconds"),
                       complete=complete, evidenceBeforeFirstClassification=all(evidence_checks),
                       programSourceReads=local_reads, modelReplies=len(replies),
                       reviewSourceReads=review_reads, boundedReviewScopes=len(review_events), boundedReviewValid=bounded_review_valid,
                       modelToolCalls=sum(len(r["ToolCalls"]) for r in replies),
                       baseline=baseline_score, candidate=candidate_score, failures=run["failures"],
                       confirmedUsd=run["resources"]["ConfirmedUsd"])
            row["pass"] = (complete and bounded_review_valid and all(evidence_checks) and candidate_score["fn"] == 0
                           and candidate_score["fp"] <= baseline_score["fp"])
            rows.append(row)
            selections.append(actual)
        selected_by_document[document] = selections[0] == selections[1]
    result = dict(candidate="pdf-source-preflight-v9", rows=rows, identicalSelectionAcrossRepeats=selected_by_document,
                  passToFullExtraction=all(r["pass"] for r in rows) and all(selected_by_document.values()))
    (BASE / "score.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
