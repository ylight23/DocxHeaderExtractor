"""Verifies the F1Q tool chain from raw files only (no report numbers). For every request directory with turn-* subdirectories:
SSE tool_calls -> tool files (same id/name/arguments, content hash) -> next turn request.json (assistant tool_calls + role tool messages with
the same tool_call_id and byte-identical content) -> final SSE answer (finish stop, content == response.txt). Also hashes request/response
bytes against observation.json. Usage: f1q_chain_verify.py <root> <out.json>"""
import glob
import gzip
import hashlib
import json
import os
import re
import sys
from collections import Counter

sys.stdout.reconfigure(encoding="utf-8")
root, out = sys.argv[1], sys.argv[2]


def sha(b):
    return hashlib.sha256(b).hexdigest()


def read_sse(attempt_dir):
    p = os.path.join(attempt_dir, "response.sse")
    if os.path.exists(p):
        return open(p, "rb").read()
    p += ".gz"
    if os.path.exists(p):
        return gzip.decompress(open(p, "rb").read())
    return None


def parse_sse(raw):
    content, reasoning, calls = [], [], {}
    finish = provider = None
    done = False
    usage = None
    for line in raw.decode("utf-8").split("\n"):
        line = line.rstrip("\r")
        if not line.startswith("data:"):
            continue
        data = line[5:].strip()
        if data == "[DONE]":
            done = True
            continue
        try:
            ev = json.loads(data)
        except ValueError:
            continue
        provider = provider or ev.get("provider")
        if isinstance(ev.get("usage"), dict):
            usage = ev["usage"]
        for c in ev.get("choices") or []:
            if c.get("finish_reason"):
                finish = c["finish_reason"]
            d = c.get("delta") or {}
            if isinstance(d.get("content"), str):
                content.append(d["content"])
            if isinstance(d.get("reasoning"), str):
                reasoning.append(d["reasoning"])
            for tc in d.get("tool_calls") or []:
                i = tc.get("index", len(calls))
                acc = calls.setdefault(i, {"id": None, "name": None, "args": []})
                if tc.get("id"):
                    acc["id"] = acc["id"] or tc["id"]
                f = tc.get("function") or {}
                if f.get("name"):
                    acc["name"] = acc["name"] or f["name"]
                if isinstance(f.get("arguments"), str):
                    acc["args"].append(f["arguments"])
    tool_calls = [{"index": i, "id": v["id"], "name": v["name"], "arguments": "".join(v["args"])} for i, v in sorted(calls.items())]
    return {"content": "".join(content), "reasoningChars": len("".join(reasoning)), "toolCalls": tool_calls, "finish": finish,
            "done": done, "provider": provider, "usage": usage}


def used_attempt(turn_dir):
    attempts = sorted(glob.glob(os.path.join(turn_dir, "attempt-*")), key=lambda p: int(p.rsplit("-", 1)[1]))
    return attempts[-1] if attempts else None


rows, problems = [], []
request_dirs = sorted({os.path.dirname(t) for t in glob.glob(os.path.join(root, "**", "turn-1"), recursive=True)})
for rd in request_dirs:
    turns = sorted(glob.glob(os.path.join(rd, "turn-*")), key=lambda p: int(p.rsplit("-", 1)[1]))
    rec = {"dir": os.path.relpath(rd, root).replace("\\", "/"), "turns": len(turns), "checks": [], "toolTurns": 0, "toolCalls": 0,
           "toolResultsOk": 0, "chainVerified": False}
    fail = []
    parsed = []
    for t in turns:
        rp = os.path.join(t, "request.json")
        redacted = not os.path.exists(rp)
        if redacted:
            rp = os.path.join(t, "request.redacted.json")
            rec["requestRedacted"] = True
        req_bytes = open(rp, "rb").read()
        req = json.loads(req_bytes)
        att = used_attempt(t)
        raw = read_sse(att) if att else None
        if raw is None:
            fail.append(f"{os.path.basename(t)}: no SSE")
            parsed.append((t, req, None))
            continue
        obs_p = os.path.join(att, "observation.json")
        if os.path.exists(obs_p):
            obs = json.load(open(obs_p, encoding="utf-8"))
            if not redacted and obs.get("requestSha256") and obs["requestSha256"] != sha(req_bytes):
                fail.append(f"{os.path.basename(t)}: request sha != observation")
            if obs.get("responseSha256") and obs["responseSha256"] != sha(raw):
                fail.append(f"{os.path.basename(t)}: response sha != observation")
        sse = parse_sse(raw)
        parsed.append((t, req, sse))
    for k, (t, req, sse) in enumerate(parsed):
        if sse is None:
            continue
        name = os.path.basename(t)
        rec["checks"].append({"turn": name, "stream": req.get("stream"), "reasoning": req.get("reasoning"), "tools": len(req.get("tools") or []),
                              "toolChoice": req.get("tool_choice"), "finish": sse["finish"], "done": sse["done"], "provider": sse["provider"],
                              "sseToolCalls": len(sse["toolCalls"]), "contentChars": len(sse["content"]), "reasoningChars": sse["reasoningChars"]})
        if sse["toolCalls"]:
            rec["toolTurns"] += 1
            rec["toolCalls"] += len(sse["toolCalls"])
            if sse["finish"] != "tool_calls":
                fail.append(f"{name}: tool calls with finish {sse['finish']}")
            files = sorted(glob.glob(os.path.join(t, "tool-*.json")))
            if len(files) != len(sse["toolCalls"]):
                fail.append(f"{name}: {len(sse['toolCalls'])} SSE calls but {len(files)} tool files")
            tools = [json.load(open(f, encoding="utf-8")) for f in files]
            for call, tf in zip(sse["toolCalls"], tools):
                if tf.get("providerToolCallId") != call["id"]:
                    fail.append(f"{name}: tool file id {tf.get('providerToolCallId')} != SSE id {call['id']}")
                if tf.get("name") != call["name"]:
                    fail.append(f"{name}: tool name differs")
                if tf.get("rawArguments") != call["arguments"]:
                    fail.append(f"{name}: tool arguments differ")
                content = tf.get("content") or ""
                if tf.get("contentSha256") and tf["contentSha256"] != sha(content.encode("utf-8")):
                    fail.append(f"{name}: tool content sha mismatch")
                if tf.get("status") == "OK":
                    rec["toolResultsOk"] += 1
            # The next turn's request must carry the same calls and byte-identical results.
            if k + 1 >= len(parsed):
                fail.append(f"{name}: tool calls but no next turn")
                continue
            msgs = parsed[k + 1][1]["messages"]
            asst = [i for i, m in enumerate(msgs) if m.get("role") == "assistant" and m.get("tool_calls")]
            if not asst:
                fail.append(f"{name}: next request has no assistant tool_calls")
                continue
            ai = asst[-1]
            echoed = msgs[ai]["tool_calls"]
            if [c["id"] for c in echoed] != [tf.get("toolCallId") for tf in tools]:
                fail.append(f"{name}: echoed ids != tool file ids")
            if [(c["function"]["name"], c["function"]["arguments"]) for c in echoed] != [(c["name"], c["arguments"]) for c in sse["toolCalls"]]:
                fail.append(f"{name}: echoed calls != SSE calls")
            tool_msgs = msgs[ai + 1: ai + 1 + len(tools)]
            for tm, tf in zip(tool_msgs, tools):
                if tm.get("role") != "tool" or tm.get("tool_call_id") != tf.get("toolCallId"):
                    fail.append(f"{name}: tool message id/role mismatch")
                elif tm.get("content") != tf.get("content"):
                    fail.append(f"{name}: tool message content != tool file content")
            if len(tool_msgs) != len(tools):
                fail.append(f"{name}: tool message count mismatch")
    last = parsed[-1][2] if parsed else None
    final_ok = bool(last) and not last["toolCalls"] and last["finish"] == "stop" and len(last["content"]) > 0
    rt = os.path.join(rd, "response.txt")
    if last and os.path.exists(rt) and open(rt, "rb").read().decode("utf-8") != last["content"]:
        fail.append("final response.txt != final SSE content")
    if not final_ok:
        fail.append(f"final turn not a stop answer (finish {last and last['finish']}, toolCalls {last and len(last['toolCalls'])})")
    rec["finalAnswer"] = final_ok
    rec["chainVerified"] = rec["toolTurns"] > 0 and not fail
    rec["problems"] = fail
    rows.append(rec)

by_group = {}
for r in rows:
    g = r["dir"].split("/requests/")[0] if "/requests/" in r["dir"] else r["dir"].rsplit("/", 1)[0]
    s = by_group.setdefault(g, Counter())
    s["requestDirs"] += 1
    s["withToolCalls"] += r["toolTurns"] > 0
    s["chainVerified"] += r["chainVerified"]
    s["withProblems"] += bool(r["problems"])
    s["sseToolCalls"] += r["toolCalls"]
    s["toolResultsOk"] += r["toolResultsOk"]
streams = Counter((json.dumps(c["stream"]), json.dumps(c["reasoning"]), c["toolChoice"]) for r in rows for c in r["checks"])
json.dump({"artifact": "p7.f1q.tool-chain-raw-verification.v1", "root": root, "rows": rows,
           "groups": {k: dict(v) for k, v in by_group.items()}, "requestShapes": [list(k) + [v] for k, v in streams.items()]},
          open(out, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
for g, s in by_group.items():
    print(g, dict(s))
print("request shapes (stream, reasoning, tool_choice):", dict(streams))
bad = [r for r in rows if r["toolTurns"] > 0 and r["problems"]]
print("tool-call request dirs with problems:", len(bad))
for r in bad[:10]:
    print("  ", r["dir"], r["problems"][:3])
