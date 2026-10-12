"""D4.10 canary analysis: protocol, semantic (development diagnostic on the 144 targets, not F1) and reasoning/output consistency,
per arm and per segment. Reads recorded data only; nothing is repaired and the reasoning never replaces the output."""
import json
import os
import sys

arm_dirs, universe_path, gold_path, out_path = sys.argv[1].split(";"), sys.argv[2], sys.argv[3], sys.argv[4]
aliases = json.load(open(universe_path, encoding="utf-8"))["aliases"]
gold = json.load(open(gold_path, encoding="utf-8"))
gold_atoms = {p["sourceAlias"] for c in gold["semantic"]["claims"] for p in c["sourceParts"]}
UNIT = 48


def scope(unit):
    n = int(unit.split("-")[1])
    return aliases[n * UNIT:(n + 1) * UNIT]


def selected_from(content, decide, fmt):
    """The SELECT set a reply states, read as-is. None when the reply does not state decisions."""
    if fmt == "Objects":
        ds = content.get("decisions")
        if not isinstance(ds, list):
            return None
        return {d.get("alias") for d in ds if isinstance(d, dict) and d.get("decision") == "SELECT"}
    blocks = content.get("blocks")
    if not isinstance(blocks, list):
        return None
    sel = set()
    for b in blocks:
        if not isinstance(b, dict) or not isinstance(b.get("start"), int) or not isinstance(b.get("codes"), str):
            continue
        for k, ch in enumerate(b["codes"]):
            i = b["start"] + k
            if ch == "S" and 0 <= i < len(decide):
                sel.add(decide[i])
    return sel


def protocol_facts(content, decide, fmt):
    f = {"status": content.get("status"), "rootKeys": sorted(content.keys())}
    d = set(decide)
    if fmt == "Objects":
        ds = content.get("decisions") if isinstance(content.get("decisions"), list) else []
        names = [x.get("alias") for x in ds if isinstance(x, dict)]
        f.update({"decisions": len(names), "missingTargets": len(d - set(names)), "contextOrOtherAliases": len([a for a in names if a not in d]),
                  "duplicates": len(names) - len(set(names)),
                  "selectWithoutEvidence": sum(1 for x in ds if isinstance(x, dict) and x.get("decision") == "SELECT" and not x.get("evidenceRefs"))})
    else:
        blocks = content.get("blocks") if isinstance(content.get("blocks"), list) else []
        codes = sum(len(b.get("codes", "")) for b in blocks if isinstance(b, dict))
        boundary_bad = 0
        for k, b in enumerate(blocks):
            if not isinstance(b, dict):
                continue
            s = b.get("start")
            n = len(b.get("codes", ""))
            if not isinstance(s, int) or s != 8 * k or not (0 <= s < len(decide)) or n == 0 or s + n - 1 >= len(decide) \
                    or b.get("firstAlias") != decide[s] or b.get("lastAlias") != decide[s + n - 1]:
                boundary_bad += 1
        ev = content.get("selectedEvidence") if isinstance(content.get("selectedEvidence"), list) else []
        ev_idx = {e.get("targetIndex") for e in ev if isinstance(e, dict)}
        s_idx = set()
        for b in blocks:
            if isinstance(b, dict) and isinstance(b.get("start"), int):
                for k, ch in enumerate(b.get("codes", "")):
                    if ch == "S":
                        s_idx.add(b["start"] + k)
        f.update({"targetCount": content.get("targetCount"), "blocks": len(blocks), "codes": codes, "missingCodes": max(0, len(decide) - codes),
                  "badBlocks": boundary_bad, "selectWithoutEvidence": len(s_idx - ev_idx)})
    return f


def last_draft(text):
    dec = json.JSONDecoder()
    starts = [i for i in range(len(text)) if text.startswith('{"protocolVersion"', i) or text.startswith('{ "protocolVersion"', i)]
    for i in reversed(starts):
        try:
            obj, _ = dec.raw_decode(text[i:])
            if obj.get("status") in ("COMPLETE", "INCOMPLETE"):
                return obj
        except ValueError:
            continue
    return None


report = {"artifact": "p7.f1.v6.d4.10-canary-analysis.v1",
          "label": "Development canary; segments chosen from earlier errors; semantic numbers are a diagnostic on 144 targets, not F1",
          "arms": []}
for arm_dir in arm_dirs:
    s = json.load(open(os.path.join(arm_dir, "run-summary.json"), encoding="utf-8"))
    fmt = s["Format"]
    units = {u["UnitId"]: u for u in s["units"]}
    arm = {"arm": s["Arm"], "format": fmt, "calls": s["Calls"], "reservedUsd": s["ReservedUsd"], "actualUsd": s["ActualUsd"], "halt": s["HaltReason"],
           "segments": []}
    tot = {"tp": 0, "fp": 0, "fn": 0}
    for unit_id in ["SEG-001", "SEG-008", "SEG-009"]:
        decide = scope(unit_id)
        u = units.get(unit_id, {})
        replies = [e for e in s["ledger"] if e["UnitId"] == unit_id]
        rows = []
        final_sel = None
        for e in replies:
            if not e.get("RawStoreKey"):
                rows.append({"round": e["Round"], "verdict": e["Verdict"]})
                continue
            raw = json.load(open(os.path.join(arm_dir, e["RawStoreKey"] + ".bin"), encoding="utf-8"))
            m = raw["choices"][0]["message"]
            text = m.get("content") or ""
            try:
                content = json.loads(text)
            except ValueError:
                rows.append({"round": e["Round"], "contentBytes": len(text.encode("utf-8")), "notJson": True})
                continue
            facts = protocol_facts(content, decide, fmt)
            facts.update({"round": e["Round"], "contentBytes": len(text.encode("utf-8")), "reasoningTokens": e["ReasoningTokens"],
                          "completionTokens": e["CompletionTokens"]})
            sel = selected_from(content, decide, fmt)
            draft = last_draft(m.get("reasoning") or "")
            dsel = selected_from(draft, decide, fmt) if draft else None
            facts["consistency"] = None if dsel is None or sel is None else {
                "draftSelected": len(dsel), "outputSelected": len(sel), "same": dsel == sel}
            rows.append(facts)
            if content.get("status") in ("COMPLETE", "INCOMPLETE"):
                final_sel = sel
        gold_here = gold_atoms & set(decide)
        sem = None
        if final_sel is not None:
            sel_in = final_sel & set(decide)
            tp, fp, fn = len(sel_in & gold_here), len(sel_in - gold_here), len(gold_here - sel_in)
            for k, v in (("tp", tp), ("fp", fp), ("fn", fn)):
                tot[k] += v
            sem = {"selected": len(sel_in), "goldAtoms": len(gold_here), "tp": tp, "fp": fp, "fn": fn,
                   "falsePositives": sorted(sel_in - gold_here), "falseNegatives": sorted(gold_here - sel_in)}
        arm["segments"].append({"unit": unit_id, "outcome": u.get("Status"), "failure": u.get("Failure"), "calls": u.get("Calls"),
                                "replies": rows, "semanticFromLastDecisionReply": sem})
    arm["semanticTotals"] = tot
    report["arms"].append(arm)

json.dump(report, open(out_path, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
for a in report["arms"]:
    print(f'== arm {a["arm"]} ({a["format"]}): calls {a["calls"]}, actual ${a["actualUsd"]:.5f}, halt {a["halt"]}, semantic {a["semanticTotals"]}')
    for sg in a["segments"]:
        print(f'  {sg["unit"]}: {sg["outcome"]} {sg["failure"] or ""} calls {sg["calls"]}')
        for r in sg["replies"]:
            keys = {k: r[k] for k in r if k not in ("rootKeys",)}
            print("     ", json.dumps(keys, ensure_ascii=False))
        print("     semantic:", json.dumps({k: v for k, v in (sg["semanticFromLastDecisionReply"] or {}).items() if k not in ("falsePositives", "falseNegatives")}))
