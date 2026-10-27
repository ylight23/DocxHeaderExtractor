"""Read-only replay of persisted decision provenance; no provider calls or Gold changes."""
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[4]
OUT = Path(__file__).resolve().parent
OLD = ROOT / "artifacts/qualification/pdf072-membership-audit-20261024"
NEW = OUT.parent
read = lambda p: json.loads(p.read_text(encoding="utf-8-sig"))
lines = {v["Alias"]: v for v in read(OLD / "evidence/source-facts.json")["lines"]}
runs = {}
for name, folder in [("previous", OLD / "full"), ("latest", NEW / "full")]:
    result = read(folder / "run-result.json")
    messages = [json.loads(s) for s in result["pipelineAudit"]["rawModelResponses"]]
    runs[name] = dict(result=result, messages=messages,
                      states=next(m["aliases"] for m in messages if m.get("stage") == "pre-binding-alias-state"),
                      replies=[json.loads(s) for s in (folder / "model-replies.ndjson").read_text(encoding="utf-8-sig").splitlines()])

requests = {}
system = read(OLD / "canary/02-B.first-request.json")["messages"][0]
for unit in ["SEG-000", "SEG-012"]:
    original = (OLD / "evidence" / (unit + ".request.json")).read_text(encoding="utf-8-sig")
    wire = json.loads(original)
    assert json.dumps(wire, ensure_ascii=False, separators=(",", ":")) == original
    wire["messages"][0] = system
    body = json.dumps(wire, ensure_ascii=False, separators=(",", ":"))
    sha = hashlib.sha256(body.encode("utf-8")).hexdigest()
    for data in runs.values():
        replies = [r for r in data["replies"] if r["Unit"] == unit]
        assert len(replies) == 1 and replies[0]["Turn"] == 1
        assert replies[0]["RequestSha256"] == sha
        assert replies[0]["FinishReason"] == "stop" and not replies[0]["ToolCalls"]
    payload = json.loads(wire["messages"][1]["content"][0]["text"])
    assert payload["targetCount"] == len(payload["targets"]) == len(payload["targetFacts"])
    for t in payload["targets"]:
        assert (t["text"], t["page"]) == (lines[t["alias"]]["Text"], lines[t["alias"]]["Page"])
    preview_count = 0
    for f in payload["targetFacts"]:
        expected = {**lines[f["alias"]]["facts"], "page": lines[f["alias"]]["Page"]}
        assert {k: v for k, v in f.items() if k != "repeatedText"} == {k: v for k, v in expected.items() if k != "repeatedText"}
        for item in f.get("repeatedText", {}).get("shown", []):
            v = lines[item["alias"]]
            assert (item["page"], item["readingOrder"]) == (v["Page"], v["Ordinal"])
            if "text" in item:
                assert v["Text"].startswith(item["text"])
            following = item.get("followingLine")
            if following:
                v = lines[following["alias"]]
                assert following["page"] == v["Page"] and v["Text"].startswith(following["text"])
                preview_count += following["text"] != v["Text"]
    (OUT / (unit + ".exact-request.json")).write_text(body, encoding="utf-8")
    requests[unit] = dict(requestSha256=sha, matchesBothActualRequests=True,
                          targetCount=payload["targetCount"], textPageAndPrimaryFactsCorrect=True,
                          repeatedReferencesMatchSource=True, shortenedFollowingLinePreviews=preview_count,
                          wireSettings={k: wire[k] for k in ["model", "temperature", "reasoning", "provider"]})

entry = next(e for e in read(ROOT / "eval/a99-closed-loop/gold-current/registry.v1.json")["authorities"] if e["authorityId"] == "DOC-0252")
gold_path = ROOT / entry["canonicalGoldPath"]
assert hashlib.sha256(gold_path.read_bytes()).hexdigest() == entry["goldSha256"]
gold_aliases = {p["sourceAlias"] for c in read(gold_path)["semantic"]["claims"] for p in c["sourceParts"]}
cases = []
for alias in ["L0000:S0", "L0001:S0", "L0002:S0", "L0003:S0", "L0513:S0", "L0514:S0", "L0519:S0"]:
    unit = "SEG-000" if alias.startswith("L000") else "SEG-012"
    case = dict(alias=alias, sourceText=lines[alias]["Text"], unit=unit,
                goldDecision="SELECT" if alias in gold_aliases else "NOT_SELECT", runs={})
    for name, data in runs.items():
        reply = next(r for r in data["replies"] if r["Unit"] == unit)
        row = next(d for d in json.loads(reply["Content"])["decisions"] if d["alias"] == alias)
        accepted = next(m for m in data["messages"] if m.get("unit") == unit)["decisions"][alias]
        state = data["states"][alias]
        assert row["decision"] == accepted == state["InitialSelection"] == state["FinalSelection"]
        case["runs"][name] = dict(rawLlmDecision=row["decision"], rawSourceText=row.get("sourceText"),
                                  modelReason=row.get("reason"), acceptedDecision=accepted,
                                  initialDecision=state["InitialSelection"], finalDecision=state["FinalSelection"],
                                  noCodeDecisionChange=True)
    cases.append(case)
result = dict(
    conclusion="Inspected false negatives and false positives originate in the LLM content response; no decision flip is introduced downstream in these cases.",
    scope="Two source-bound qualification runs, SEG-000 and SEG-012; not a claim that all extraction code is bug-free.",
    requests=requests, cases=cases, goldSha256=entry["goldSha256"], goldHashVerified=True,
    visibleModelAnalysis={name: {u: json.loads(next(r["Content"] for r in data["replies"] if r["Unit"] == u)).get("analysis") for u in requests} for name, data in runs.items()},
    codeLimitation="sourceText validates source binding for SELECT, not semantic membership. NOT_SELECT can omit evidence and reason. The production path has no semantic veto of these valid decisions.",
    limits=["Identical requests establish response variability, not its provider/backend cause.",
            "Model explanations are diagnostic self-report, not verified causal reasoning.",
            "Facts are checked against the recorded canonical parser evidence; original PDF pixels were not reinspected in this audit."],
    runtimeCodeChanged=False, newProviderCalls=0)
(OUT / "audit.json").write_text(json.dumps(result, indent=2, ensure_ascii=False), encoding="utf-8")
print(json.dumps(dict(exactRequestsVerified=len(requests), targetBindingsVerified=sum(v["targetCount"] for v in requests.values()),
                      casesChecked=len(cases), allRawToFinalDecisionsPreserved=True, newProviderCalls=0)))
