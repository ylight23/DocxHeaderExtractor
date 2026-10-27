"""Hand a bundle's answer.json to the bridge. Transport check only: the reply must be one JSON object.
Protocol and content checks are the pipeline's job, exactly as with the real provider.

usage: python finalize.py <bundle-folder>
"""
import json, os, sys

out = sys.argv[1]
meta = json.load(open(os.path.join(out, "meta.json"), encoding="utf-8"))
path = os.path.join(out, "answer.json")
if not os.path.exists(path):
    print("ERROR: answer.json is missing"); sys.exit(1)
raw = open(path, encoding="utf-8-sig").read().strip()
try:
    value = json.loads(raw)
except Exception as e:
    print(f"ERROR: answer.json is not valid JSON: {e}"); sys.exit(1)
if not isinstance(value, dict):
    print("ERROR: the reply must be one JSON object"); sys.exit(1)
target = meta["responsePath"]
if os.path.exists(target):
    print("SUBMITTED (already)"); sys.exit(0)
tmp = target + ".tmp"
with open(tmp, "w", encoding="utf-8") as f:
    json.dump({"requestSha256": meta["requestSha256"], "content": json.dumps(value, ensure_ascii=False), "finishReason": "stop"}, f, ensure_ascii=False)
os.replace(tmp, target)
print("SUBMITTED")
