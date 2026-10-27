"""Score registered PDF072 Gold without altering it. Hierarchy lacks a Gold capability."""
import argparse
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def metrics(expected, actual):
    tp = len(expected & actual)
    precision = tp / len(actual) if actual else 0
    recall = tp / len(expected) if expected else 0
    return dict(tp=tp, fp=len(actual - expected), fn=len(expected - actual),
                precision=precision, recall=recall,
                f1=2 * precision * recall / (precision + recall) if precision + recall else 0,
                missing=sorted(expected - actual), extra=sorted(actual - expected))


def boundaries(result):
    audit = result["pipelineAudit"]
    messages = [json.loads(s) for s in audit["rawModelResponses"] if s.startswith("{")]
    exported = next((m["groups"] for m in messages if m.get("stage") == "bound-heading-groups"), None)
    states = next(m["aliases"] for m in messages if m.get("stage") == "pre-binding-alias-state")
    selected = {a for a, s in states.items() if s["FinalSelection"] == "SELECT"}
    if exported is not None:
        groups = [tuple(g) for g in exported]
    else:
        # Frozen pre-experiment baseline: reconstruct only accepted partitions; verify against final selection and output count.
        order = {a: i for i, a in enumerate(a for m in messages if "unit" in m and "decisions" in m for a in m["decisions"])}
        ordered = sorted(selected, key=order.__getitem__)
        runs = []
        for a in ordered:
            if not runs or order[a] != order[runs[-1][-1]] + 1:
                runs.append([])
            runs[-1].append(a)
        required = {f"R{i+1}": run for i, run in enumerate(r for r in runs if len(r) > 1)}
        accepted = {}
        for m in messages:
            if "grouping" not in m:
                continue
            raw = m["grouping"]
            raw = raw.strip()
            if raw.startswith("```"):
                raw = raw[raw.index("\n") + 1:raw.rindex("```")]
            response = json.loads(raw)
            for run in response.get("runs", []):
                parts = run.get("headings", [])
                if [a for g in parts for a in g] == required.get(run["run"]):
                    accepted[run["run"]] = parts
        if set(required) != set(accepted):
            raise ValueError("Frozen baseline cannot be reconstructed completely; no approximate score")
        groups = [tuple(g) for parts in accepted.values() for g in parts] + [(r[0],) for r in runs if len(r) == 1]
    flattened = [a for g in groups for a in g]
    if len(flattened) != len(set(flattened)) or set(flattened) != selected:
        raise ValueError("Boundary export does not exactly cover final selection")
    if len(groups) != len(result["outline"]["headings"]):
        raise ValueError("Boundary count differs from final materialized heading count")
    return selected, set(groups)


def score(path, authority_id="DOC-0252"):
    registry_path = ROOT / "eval/a99-closed-loop/gold-current/registry.v1.json"
    entry = next(e for e in read(registry_path)["authorities"] if e["authorityId"] == authority_id)
    gold_path = ROOT / entry["canonicalGoldPath"]
    if hashlib.sha256(gold_path.read_bytes()).hexdigest() != entry["goldSha256"]:
        raise ValueError("Gold hash differs from registry")
    gold = read(gold_path)
    result = read(path)
    if result["input"]["sha256"] != entry["sourceSha256"]:
        raise ValueError("Source hash differs from registered Gold")
    expected = {tuple(p["sourceAlias"] for p in claim["sourceParts"]) for claim in gold["semantic"]["claims"]}
    if any(p["selectionMode"] != "WHOLE_ALIAS" for c in gold["semantic"]["claims"] for p in c["sourceParts"]):
        raise ValueError("This scorer requires whole-alias structured Gold")
    selected, groups = boundaries(result)
    placement = result["placementExecution"]
    attempts = result["selectionResources"]["Attempts"] + placement["Resources"]["Attempts"]
    return dict(run=str(path), elapsedSeconds=result["elapsedSeconds"],
                goldPath=entry["canonicalGoldPath"], goldSha256=entry["goldSha256"],
                goldHeadingCount=len(expected), headingCount=len(groups),
                aliasMembership=metrics({a for g in expected for a in g}, selected),
                exactAliasBoundaries=metrics(expected, groups),
                characterSpanScore=None, hierarchyScore=None,
                hierarchyCapability=gold["capabilities"]["hierarchyEvaluable"],
                unresolvedParents=placement["UnresolvedCount"],
                graphConflicts=placement.get("GraphConflicts", []),
                placementCalls=placement["LogicalCalls"], placementSeconds=placement["Seconds"],
                grouping=result["groupingObservation"],
                providerAttempts=len(attempts), actualGenerations=sum(a.get("PromptTokens") is not None for a in attempts),
                rateLimitedAttempts=sum(a.get("Transport", {}).get("HttpStatus") == 429 for a in attempts),
                costUsd=result["runResourceBudget"]["ConfirmedUsd"])


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("run")
    parser.add_argument("--output", required=True)
    parser.add_argument("--authority", default="DOC-0252")
    args = parser.parse_args()
    result = score(args.run, args.authority)
    Path(args.output).write_text(json.dumps(result, indent=2, ensure_ascii=False), encoding="utf-8")
    print(json.dumps({k: v for k, v in result.items() if k not in ["grouping", "aliasMembership", "exactAliasBoundaries"]}, indent=2))
