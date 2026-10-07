"""Rewrites the source aliases of one source-review item file from the v1 atoms to their v2 atoms.

Only aliases are replaced; approved wording, verdicts, patterns, reasons and axes are untouched. It refuses to
write when any alias used by an item has no v2 atom with the same page and non-space text, or when a
VERBATIM_TEXT part's text is no longer inside its target atom.

usage: apply_review_items.py <repo> <doc> <v2-facts-tsv> <old-layout-tsv>
"""
import json
import re
import sys

import build_proposal as bp


def main():
    repo, doc, new_tsv, old_tsv = sys.argv[1:5]
    old = [{"alias": f[0], "page": int(f[1]), "text": f[10]} for f in
           (l.split("\t") for l in open(old_tsv, encoding="utf-8").read().split("\n") if l)]
    new = [{"alias": f[0], "page": int(f[1]), "text": f[10]} for f in
           (l.split("\t") for l in open(new_tsv, encoding="utf-8").read().split("\n") if l)]
    mapping = bp.align(old, new)
    new_text = {a["alias"]: a["text"] for a in new}
    path = f"{repo}/eval/a99-closed-loop/source-review-v1/{doc}/review-items.json"
    raw = open(path, encoding="utf-8", newline="").read()
    items = json.loads(raw)["items"]
    bad = []
    for item in items:
        for part in item["parts"]:
            target = mapping.get(part["sourceAlias"])
            if target is None:
                bad.append((part["sourceAlias"], "no v2 atom"))
            elif part["verbatimText"] is not None and part["verbatimText"] not in new_text[target]:
                bad.append((part["sourceAlias"], "verbatim text no longer inside target"))
    if bad:
        print("REFUSED", bad)
        sys.exit(1)
    out = re.sub(r'"sourceAlias":\s*"(L\d+:S\d+)"',
                 lambda m: m.group(0).replace(m.group(1), mapping[m.group(1)]), raw)
    open(path, "w", encoding="utf-8", newline="").write(out)
    changed = sum(1 for a, b in mapping.items() if a != b)
    print("rewrote", path, "aliases remapped in the table:", changed)
    json.dump({a: b for a, b in mapping.items() if a != b},
              open(f"{repo}/eval/a99-closed-loop/gold-migration-proposals/pdf-universe-v2/{doc}.alias-map.v1.json", "w", encoding="utf-8", newline="\n"),
              indent=1)


if __name__ == "__main__":
    main()
