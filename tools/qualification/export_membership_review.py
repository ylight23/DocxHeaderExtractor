"""Export auditable per-heading results and every decision change versus a frozen failed run."""
import argparse
import json
from pathlib import Path
from score_serial_calls import boundaries, read, score


def messages(run):
    return [json.loads(s) for s in run["pipelineAudit"]["rawModelResponses"] if s.startswith("{")]


def states(run):
    return next(m["aliases"] for m in messages(run) if m.get("stage") == "pre-binding-alias-state")


def cell(value):
    return str(value).replace("|", "\\|").replace("\n", " ")


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("run")
    p.add_argument("before")
    p.add_argument("--authority", default="DOC-0252")
    args = p.parse_args()
    current, before = read(args.run), read(args.before)
    measured = score(args.run, args.authority)  # verifies source/Gold identity and exported group coverage
    gold = read(measured["goldPath"])
    expected = {tuple(part["sourceAlias"] for part in c["sourceParts"]) for c in gold["semantic"]["claims"]}
    _, old_groups = boundaries(before)
    groups = next(m["groups"] for m in messages(current) if m.get("stage") == "bound-heading-groups")
    by_first = {g[0]: tuple(g) for g in groups}
    placement = {d["SourceId"]: d for d in current["placementExecution"]["Decisions"]}
    headings = []
    for h in current["outline"]["headings"]:
        d = placement[h["sourceId"]]
        g = by_first[d["Alias"]]
        headings.append(dict(**h, aliases=g, parentAlias=d.get("ProposedParentAlias"),
                             exactGoldGroup=g in expected, sameGroupBefore=g in old_groups))
    assert len(headings) == len(groups)
    bs, cs = states(before), states(current)
    assert set(bs) == set(cs)
    changed = [dict(alias=a, before=bs[a]["FinalSelection"], after=cs[a]["FinalSelection"])
               for a in cs if bs[a]["FinalSelection"] != cs[a]["FinalSelection"]]
    result = dict(run=args.run, before=args.before, aliasMembership=measured["aliasMembership"],
                  exactGroups=measured["exactAliasBoundaries"], decisionChanges=changed, headings=headings,
                  limits="Parent/level values are model output, not independently verified hierarchy accuracy.")
    folder = Path(args.run).parent
    (folder / "headings.json").write_text(json.dumps(headings, indent=2, ensure_ascii=False), encoding="utf-8")
    (folder / "heading-review.json").write_text(json.dumps(result, indent=2, ensure_ascii=False), encoding="utf-8")
    lines = ["# Per-heading audit", "", f"Run: `{args.run}`", "", f"Compared with: `{args.before}`", "",
             "Gold evaluates membership and exact groups. Parent/level below are model output; hierarchy has no evaluable Gold.", "",
             "## All membership changes", "", "| Alias | Before | After |", "|---|---|---|"]
    lines += [f"| {x['alias']} | {x['before']} | {x['after']} |" for x in changed]
    lines += ["", "## Every output heading", "", "| # | Aliases | Text | Exact Gold group | Same group before | Parent | Level |",
              "|---|---|---|---|---|---|---|"]
    lines += [f"| {i} | {', '.join(h['aliases'])} | {cell(h['text'])} | {h['exactGoldGroup']} | {h['sameGroupBefore']} | {h['parentAlias']} | {h.get('level', '—')} |"
              for i, h in enumerate(headings, 1)]
    (folder / "heading-review.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(json.dumps(dict(headings=len(headings), changed=changed, output=str(folder / "heading-review.md"))))
