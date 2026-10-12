"""For each recorded reply: the last JSON object Qwen drafted inside its reasoning versus the JSON it returned as content.
Reads recorded data only. A draft that cannot be parsed is reported as such; nothing is repaired."""
import json
import os
import sys

run_dir, out_path = sys.argv[1:3]
summary = json.load(open(os.path.join(run_dir, "run-summary.json"), encoding="utf-8"))
status = {u["UnitId"]: (u["Status"], u["Failure"]) for u in summary["units"]}


def last_draft(text):
    """The last parseable JSON object in the reasoning that starts with "protocolVersion"."""
    dec = json.JSONDecoder()
    starts = [i for i in range(len(text)) if text.startswith('{"protocolVersion"', i) or text.startswith('{ "protocolVersion"', i)]
    for i in reversed(starts):
        try:
            obj, _ = dec.raw_decode(text[i:])
            return obj
        except ValueError:
            continue
    return None


def summary_of(obj):
    if obj is None:
        return None
    ds = obj.get("decisions", [])
    return {"status": obj.get("status"), "decisions": len(ds),
            "selected": sorted(d["alias"] for d in ds if d.get("decision") == "SELECT"),
            "rootKeys": sorted(obj.keys())}


rows = []
for e in summary["ledger"]:
    m = json.load(open(os.path.join(run_dir, e["RawStoreKey"] + ".bin"), encoding="utf-8"))["choices"][0]["message"]
    out = summary_of(json.loads(m["content"]))
    draft = summary_of(last_draft(m.get("reasoning") or ""))
    agrees = None if draft is None else (draft["decisions"] == out["decisions"] and draft["selected"] == out["selected"] and draft["rootKeys"] == out["rootKeys"])
    rows.append({"unit": e["UnitId"], "round": e["Round"], "unitOutcome": status.get(e["UnitId"]), "output": out, "reasoningDraft": draft,
                 "draftMatchesOutput": agrees})

json.dump({"artifact": "p7.f1.v6.d4.8-reasoning-vs-output.v1",
           "method": "last parseable JSON object starting with protocolVersion inside the reasoning, compared with the returned content",
           "rows": rows}, open(out_path, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
for r in rows:
    o, d = r["output"], r["reasoningDraft"]
    print(r["unit"], "r" + str(r["round"]), r["unitOutcome"], "| output dec", o["decisions"], "sel", len(o["selected"]),
          "| draft", "none" if d is None else f'dec {d["decisions"]} sel {len(d["selected"])}', "| match", r["draftMatchesOutput"])
