"""Every contract violation in each recorded D4.5 reply, under the V2.2 rules (NOT_SELECT may omit evidenceRefs).
The .NET parser stops at the first failure; this lists all of them so none is hidden behind another."""
import json
import os
import sys

run_dir, universe_path, out_path = sys.argv[1:4]
UNIT, OVERLAP, MAX_PARTS, MAX_REFS = 48, 20, 8, 8
aliases = json.load(open(universe_path, encoding="utf-8"))["aliases"]
pos = {a: i for i, a in enumerate(aliases)}
summary = json.load(open(os.path.join(run_dir, "run-summary.json"), encoding="utf-8"))

rows = []
retrieved = {}
for e in summary["ledger"]:
    unit, rnd = e["UnitId"], e["Round"]
    n = int(unit.split("-")[1])
    core = range(n * UNIT, min(len(aliases), (n + 1) * UNIT))
    decide = {aliases[i] for i in core}
    shown = {aliases[i] for i in range(max(0, n * UNIT - OVERLAP), min(len(aliases), (n + 1) * UNIT + OVERLAP))} | retrieved.get(unit, set())
    raw = json.load(open(os.path.join(run_dir, e["RawStoreKey"] + ".bin"), encoding="utf-8"))
    c = json.loads(raw["choices"][0]["message"]["content"])
    v = []
    if c.get("status") == "NEEDS_EVIDENCE":
        for r in c.get("retrieve", []):
            if r.get("kind") == "RANGE" and r.get("fromAlias") in pos and r.get("toAlias") in pos:
                a, b = pos[r["fromAlias"]], pos[r["toAlias"]]
                retrieved.setdefault(unit, set()).update(aliases[i] for i in range(a, min(b + 1, a + 60)))
        rows.append({"unit": unit, "round": rnd, "status": "NEEDS_EVIDENCE", "violations": []})
        continue
    decs = c.get("decisions", [])
    names = [d.get("alias") for d in decs]
    dup = sorted({a for a in names if names.count(a) > 1})
    outside = [a for a in names if a not in decide]
    outside_context = [a for a in outside if a in shown]
    outside_unknown = [a for a in outside if a not in shown]
    missing = sorted(decide - set(names), key=lambda a: pos[a])
    if outside_context: v.append(f"DECIDED_CONTEXT_ALIASES:{len(outside_context)}")
    if outside_unknown: v.append(f"DECIDED_UNSHOWN_ALIASES:{len(outside_unknown)}")
    if dup: v.append(f"DUPLICATE_ALIASES:{len(dup)}")
    if missing: v.append(f"MISSING_DECISIONS:{len(missing)}:" + ",".join(missing))
    sel = [d for d in decs if d.get("decision") == "SELECT"]
    sel_no_refs = [d["alias"] for d in sel if not (isinstance(d.get("evidenceRefs"), list) and 1 <= len(d["evidenceRefs"]) <= MAX_REFS)]
    if sel_no_refs: v.append(f"SELECT_WITHOUT_EVIDENCE:{len(sel_no_refs)}")
    refs_unshown = [r for d in decs for r in (d.get("evidenceRefs") or []) if r not in shown]
    if refs_unshown: v.append(f"EVIDENCE_NOT_SHOWN:{len(refs_unshown)}")
    neg_missing = sum(1 for d in decs if d.get("decision") == "NOT_SELECT" and "evidenceRefs" not in d)
    neg_empty = sum(1 for d in decs if d.get("decision") == "NOT_SELECT" and d.get("evidenceRefs") == [])
    heads = c.get("headings", [])
    big = [len(h.get("parts", [])) for h in heads if len(h.get("parts", [])) > MAX_PARTS]
    if big: v.append(f"HEADINGS_OVER_8_PARTS:{len(big)}:sizes={big}")
    in_heads = [p for h in heads for p in h.get("parts", [])]
    sel_decide = {d["alias"] for d in sel if d["alias"] in decide}
    not_grouped = sorted(sel_decide - set(in_heads), key=lambda a: pos[a])
    if not_grouped: v.append(f"SELECT_WITHOUT_HEADING:{len(not_grouped)}")
    order_bad = sum(1 for h in heads if [pos.get(p, -1) for p in h.get("parts", [])] != sorted(pos.get(p, -1) for p in h.get("parts", [])))
    if order_bad: v.append(f"HEADING_PARTS_OUT_OF_ORDER:{order_bad}")
    rows.append({"unit": unit, "round": rnd, "status": c.get("status"), "decisions": len(decs), "decideSize": len(decide),
                 "notSelectEvidenceOmitted": neg_missing, "notSelectEvidenceEmpty": neg_empty, "violations": v})

out = {"artifact": "p7.f1.v6.d4.5-reply-violations.v1", "rules": "V2.2 contract (NOT_SELECT may omit evidenceRefs or give []); every violation, not only the first",
       "rows": rows}
json.dump(out, open(out_path, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
for r in rows:
    print(r["unit"], "r" + str(r["round"]), r["status"], "dec", r.get("decisions"), "/", r.get("decideSize"),
          "neg-omit", r.get("notSelectEvidenceOmitted"), "neg-empty", r.get("notSelectEvidenceEmpty"), "|", "; ".join(r["violations"]) or "no violation")
