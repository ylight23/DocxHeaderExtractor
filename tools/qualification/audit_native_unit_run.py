"""Freeze native V14 provider artifacts, then score against the registered canonical Gold.

The freeze action never reads Gold. Scoring refuses any changed frozen file.
This utility does not author or repair model decisions.
"""
from __future__ import annotations

import hashlib
import json
import re
import sys
import unicodedata
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def save(path, value):
    if path.exists():
        raise ValueError(f"REFUSE_OVERWRITE:{path}")
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")


def packet(request):
    user = next(m for m in request["messages"] if m["role"] == "user")
    text = user["content"] if isinstance(user["content"], str) else next(c["text"] for c in user["content"] if c["type"] == "text")
    return json.loads(text)


def freeze(run):
    identity = read(run / "run-identity.json")
    native = read(run / "native-run-result.json")
    phases, pairs, targets, complete_units = Counter(), [], {}, set()
    for req in sorted((run / "luna-requests").glob("*.request.json")):
        digest = sha(req)
        if req.name != digest + ".request.json":
            raise ValueError("REQUEST_SHA_MISMATCH")
        resp = run / "luna-responses" / (digest + ".response.json")
        reply = read(resp)
        if reply["requestSha256"] != digest:
            raise ValueError("RESPONSE_BINDING_MISMATCH")
        p = packet(read(req))
        phases[p.get("stage", "PLACE")] += 1
        content = reply.get("content")
        answer = json.loads(content) if isinstance(content, str) else content
        if p.get("stage") == "UNIT_SEGMENT":
            for unit in p["targetUnits"]:
                for m in unit["members"]:
                    if m["alias"] in targets and targets[m["alias"]] != m:
                        raise ValueError("CHANGED_TARGET_IN_REPAIR")
                    targets[m["alias"]] = m
            if answer and answer.get("status") == "COMPLETE" and reply.get("finishReason") == "stop":
                if {u["unitId"] for u in p["targetUnits"]} != {d["unitId"] for d in answer["decisions"]}:
                    raise ValueError("DECISION_COVERAGE_MISMATCH")
                complete_units.add(p["unitId"])
        pairs.append({"request": req.relative_to(run).as_posix(), "requestSha256": digest,
                      "response": resp.relative_to(run).as_posix(), "responseSha256": sha(resp)})
    if len(targets) != native["Outline"]["sourceCount"]:
        raise ValueError("NATIVE_SOURCE_COVERAGE_MISMATCH")
    if len(pairs) != identity["headingProviderCalls"]:
        raise ValueError("CALL_ACCOUNTING_MISMATCH")
    files = [run / name for name in ["native-run-result.json", "run-identity.json", "selector-replies.json", "bridge-calls.json"]]
    for directory in ["luna-requests", "luna-responses", "luna-sse-responses"]:
        files.extend(sorted((run / directory).glob("*")))
    value = {"status": "FROZEN_BEFORE_GOLD", "createdUtc": datetime.now(timezone.utc).isoformat(),
             "sourceSha256": identity["sourceSha256"], "goldRead": False, "phases": dict(phases),
             "coveredAliases": len(targets), "completedSelectionScopes": len(complete_units),
             "pairs": pairs, "files": [{"path": p.relative_to(run).as_posix(), "sha256": sha(p)} for p in files]}
    save(run / "pre-gold-freeze.json", value)
    print(json.dumps({"freezeSha256": sha(run / "pre-gold-freeze.json"), "pairs": len(pairs), "phases": dict(phases), "aliases": len(targets)}))


def alias_key(alias):
    match = re.fullmatch(r"L(\d+):S(\d+)", alias)
    if not match:
        raise ValueError("BAD_ALIAS:" + alias)
    return tuple(map(int, match.groups()))


def normalize(text):
    return re.sub(r"\s", "", unicodedata.normalize("NFKC", text))


def score(run):
    frozen = read(run / "pre-gold-freeze.json")
    if frozen["status"] != "FROZEN_BEFORE_GOLD":
        raise ValueError("FREEZE_REQUIRED")
    for item in frozen["files"]:
        if sha(run / item["path"]) != item["sha256"]:
            raise ValueError("FROZEN_FILE_CHANGED:" + item["path"])
    native = read(run / "native-run-result.json")
    members, reasons = {}, {}
    for pair in frozen["pairs"]:
        p = packet(read(run / pair["request"]))
        if p.get("stage") != "UNIT_SEGMENT":
            continue
        units = {u["unitId"]: u for u in p["targetUnits"]}
        for unit in units.values():
            for m in unit["members"]:
                members[m["alias"]] = m
        answer = read(run / pair["response"])["content"]
        answer = json.loads(answer) if isinstance(answer, str) else answer
        if answer.get("status") == "COMPLETE":
            for decision in answer["decisions"]:
                for m in units[decision["unitId"]]["members"]:
                    reasons[m["alias"]] = {"unitId": decision["unitId"], "decision": decision["decision"],
                        "reason": decision.get("reason"), "selected": [s["alias"] for s in decision["selected"]],
                        "requestSha256": pair["requestSha256"]}
    aliases = sorted(members, key=alias_key)
    catalog = sorted(native["Result"]["sourceCatalog"]["units"], key=lambda u: u["sourceOrdinal"])
    if len(catalog) != len(aliases):
        raise ValueError("SOURCE_MAP_COUNT_MISMATCH")
    ordinal_alias = {}
    for alias, unit in zip(aliases, catalog):
        if normalize(members[alias]["text"]) != normalize(unit["text"]) or members[alias]["page"] != unit["sourceAnchor"]["Page"]:
            raise ValueError("SOURCE_MAP_TEXT_PAGE_MISMATCH:" + alias)
        ordinal_alias[unit["sourceOrdinal"]] = alias
    predicted_groups = [tuple(ordinal_alias[s["sourceOrdinal"]] for s in e["sources"])
                        for e in native["Result"]["structure"]["elements"] if e["type"] == "Heading"]
    if len({a for g in predicted_groups for a in g}) != sum(map(len, predicted_groups)):
        raise ValueError("PREDICTED_ALIAS_DUPLICATION")
    # Gold is first opened only after all frozen artifacts and source mapping pass validation.
    registry_path = ROOT / "eval/a99-closed-loop/gold-current/registry.v1.json"
    registry = read(registry_path)
    authority = next(a for a in registry["authorities"] if a["sourceSha256"] == frozen["sourceSha256"])
    gold_path = ROOT / authority["canonicalGoldPath"]
    if sha(gold_path) != authority["goldSha256"]:
        raise ValueError("GOLD_HASH_MISMATCH")
    gold = read(gold_path)
    expected_groups = [tuple(p["sourceAlias"] for p in claim["sourceParts"]) for claim in gold["semantic"]["claims"]]
    expected, actual = set(a for g in expected_groups for a in g), set(a for g in predicted_groups for a in g)
    def metrics(e, a):
        return {"tp": len(e & a), "fp": len(a - e), "fn": len(e - a),
                "precision": len(e & a) / len(a) if a else 0, "recall": len(e & a) / len(e) if e else 0}
    def detail(alias):
        return {"alias": alias, "text": members[alias]["text"], "page": members[alias]["page"], "modelDecision": reasons.get(alias)}
    value = {"sourceSha256": frozen["sourceSha256"], "freezeSha256": sha(run / "pre-gold-freeze.json"),
             "goldPath": authority["canonicalGoldPath"], "goldSha256": authority["goldSha256"], "registrySha256": sha(registry_path),
             "sourceMapValidated": len(aliases), "aliasScore": metrics(expected, actual),
             "exactGroupScore": metrics(set(expected_groups), set(predicted_groups)),
             "falsePositives": [detail(a) for a in sorted(actual - expected, key=alias_key)],
             "falseNegatives": [detail(a) for a in sorted(expected - actual, key=alias_key)],
             "predictedGroups": predicted_groups,
             "limitations": ["Luna local bridge timing is not external provider latency.",
                             "A single fresh run versus an earlier run does not isolate stochastic or packing effects."]}
    save(run / "gold-score.json", value)
    print(json.dumps({"aliasScore": value["aliasScore"], "exactGroupScore": value["exactGroupScore"], "sourceMapValidated": len(aliases)}))


if __name__ == "__main__":
    if len(sys.argv) != 3 or sys.argv[1] not in ("freeze", "score"):
        raise SystemExit("usage: audit_native_unit_run.py freeze|score <run-directory>")
    globals()[sys.argv[1]](Path(sys.argv[2]).resolve())
