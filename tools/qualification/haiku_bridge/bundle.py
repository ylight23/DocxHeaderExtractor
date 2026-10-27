"""Turn one bridge request into a self-contained folder a fresh Haiku agent can answer.

usage: python bundle.py <request.json> <responses-dir> <bundle-root>
Prints the bundle folder. The folder holds only this request: system text, the user packet(s), page images,
any earlier turns of this same conversation, and the reply contract. Nothing from other requests.
"""
import base64, json, os, sys

req_path, responses_dir, root = sys.argv[1:4]
sha = os.path.basename(req_path).split(".")[0]
body = json.load(open(req_path, encoding="utf-8"))
out = os.path.join(root, sha[:16])
os.makedirs(out, exist_ok=True)


def text_of(content):
    if isinstance(content, str):
        return content, []
    texts, images = [], []
    for part in content:
        if part.get("type") == "text":
            texts.append(part["text"])
        elif part.get("type") == "image_url":
            images.append(part["image_url"]["url"])
    return "\n".join(texts), images


def dump(value, depth, limit):
    # Top levels one entry per line; anything deeper stays compact on its line. Same JSON value either way.
    compact = json.dumps(value, ensure_ascii=False, separators=(",", ":"))
    if not isinstance(value, (dict, list)) or not value or (depth >= limit and len(compact) <= 1500):
        return compact
    pad = " " * (depth + 1)
    if isinstance(value, dict):
        items = [pad + json.dumps(k, ensure_ascii=False) + ":" + dump(v, depth + 1, limit) for k, v in value.items()]
        return "{\n" + ",\n".join(items) + "\n" + " " * depth + "}"
    return "[\n" + ",\n".join(pad + dump(v, depth + 1, limit) for v in value) + "\n" + " " * depth + "]"


def pretty(text):
    try:
        value = json.loads(text)
    except Exception:
        return text
    for limit in (4, 3, 2):
        out = dump(value, 0, limit)
        if out.count("\n") < 3000 or limit == 2:
            return out


files, image_no = [], 0
for i, msg in enumerate(body["messages"]):
    text, images = text_of(msg.get("content") or "")
    name = f"{i:02d}-{msg['role']}.txt"
    if msg["role"] in ("user", "assistant") and text.lstrip().startswith("{"):
        name = f"{i:02d}-user-packet.json" if msg["role"] == "user" else f"{i:02d}-assistant-reply.json"
        text = pretty(text)
    if msg.get("tool_calls"):
        text += "\n[tool calls]\n" + json.dumps(msg["tool_calls"], ensure_ascii=False, indent=1)
    open(os.path.join(out, name), "w", encoding="utf-8").write(text)
    files.append((name, msg["role"], text.count("\n") + 1))
    long_lines = [line for line in text.split("\n") if len(line) > 1900]
    if long_lines:
        # The file reader cuts lines near 2000 characters; repeat those lines wrapped so nothing is lost.
        import textwrap
        wrapped = "\n\n".join(f"--- line {text.split(chr(10)).index(line) + 1} of {name}, wrapped ---\n" + "\n".join(textwrap.wrap(line, 1000))
                              for line in long_lines)
        extra = name.rsplit(".", 1)[0] + "-long-lines.txt"
        open(os.path.join(out, extra), "w", encoding="utf-8").write(wrapped)
        files.append((extra, f"full text of {len(long_lines)} long line(s) of {name}", wrapped.count("\n") + 1))
    for url in images:
        image_no += 1
        data = base64.b64decode(url.split(",", 1)[1])
        img = f"{i:02d}-image-{image_no}.jpg"
        open(os.path.join(out, img), "wb").write(data)
        files.append((img, "image attached to message " + str(i), 0))

stage = "UNKNOWN"
for name, role, _ in files:
    if name.endswith("packet.json"):
        try:
            p = json.load(open(os.path.join(out, name), encoding="utf-8"))
            stage = p.get("stage") or p.get("protocolVersion") or "UNKNOWN"
        except Exception:
            pass

json.dump({"requestSha256": sha, "responsePath": os.path.join(responses_dir, sha + ".response.json"), "stage": stage,
           "responseFormat": body.get("response_format")}, open(os.path.join(out, "meta.json"), "w", encoding="utf-8"), indent=1)

listing = "\n".join(f"- `{n}` ({r}{', ' + str(l) + ' lines' if l else ''})" for n, r, l in files)
readme = f"""# One model call

You are standing in for the language model behind one API call. Answer it exactly as that model would:
read the conversation below in order and write the reply to the **last** message.

Use only the files in this folder. Do not open any other folder, repository file, earlier answer, score or reference
output. Do not use anything you remember from other tasks.

## The conversation (in order)
{listing}

`*-system.txt` is the system message. Read every text file completely (page through long files with offset/limit until
the end; a decision about an item must come from reading it). Look at every image.
The request offered optional tools; they are not available in this run, so answer directly without calling any.
The reply must be one JSON object (response_format {json.dumps(body.get('response_format'))}), with no markdown fence
and no text around it.

## Submit
1. Write the JSON reply to `answer.json` in this folder.
2. Run: `python "{os.path.join(os.path.dirname(os.path.abspath(__file__)), 'finalize.py')}" "{os.path.abspath(out)}"`
3. If it prints `ERROR`, fix `answer.json` and run it again. Stop when it prints `SUBMITTED`.
"""
open(os.path.join(out, "README.md"), "w", encoding="utf-8").write(readme)
print(os.path.abspath(out))
