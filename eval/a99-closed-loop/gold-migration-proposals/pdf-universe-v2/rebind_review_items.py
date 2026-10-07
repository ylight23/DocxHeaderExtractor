"""Rebinds a source-review item file from the v1 atoms to the v2 (font-independent) atoms, fail closed.

Approved by the user 2026-10-07 for SRC-029: the 34 partial-selection claims whose atoms only changed spacing are remapped
(verbatim text and occurrence re-derived from the v2 atom), and the 7 claims whose atoms really split or fused are rebound
semantically. Nothing semantic changes: wording, verdict, pattern, reason and axes are copied untouched.

Rules
  * an alias that has an exact page+non-space-text match in v2 is remapped; a WHOLE_ALIAS part stays whole;
  * a VERBATIM_TEXT part is re-expressed as the v2 text covering the same non-space characters (occurrence recomputed);
  * an item with an alias that has no match is rebound only if its wording is in --structural. The rebinding finds the
    item's non-space wording in the non-space concatenation of the v2 atoms of the same page, in reading order; it must
    occur exactly once, else the tool refuses. Atoms fully covered become WHOLE_ALIAS parts, edge atoms VERBATIM_TEXT.
No model call, no Gold read.

usage: rebind_review_items.py <repo> <doc> <v2-layout-tsv> <old-layout-tsv> <structural-wording-json> [--write]
"""
import difflib
import json
import re
import sys


def squeeze(text):
    return "".join(text.split())


def load(path):
    rows = []
    for line in open(path, encoding="utf-8").read().split("\n"):
        if line:
            f = line.split("\t")
            rows.append({"alias": f[0], "page": int(f[1]), "text": f[10]})
    return rows


def align(old, new):
    key_old = [(a["page"], squeeze(a["text"])) for a in old]
    key_new = [(a["page"], squeeze(a["text"])) for a in new]
    matcher = difflib.SequenceMatcher(None, key_old, key_new, autojunk=False)
    mapping = {}
    for tag, i1, i2, j1, j2 in matcher.get_opcodes():
        if tag == "equal":
            for k in range(i2 - i1):
                mapping[old[i1 + k]["alias"]] = new[j1 + k]["alias"]
    return mapping


def hits(text, needle):
    out, at = [], text.find(needle)
    while at >= 0:
        out.append(at)
        at = text.find(needle, at + 1)
    return out


def old_span(text, part):
    """Start/end of a VERBATIM part in the old atom text, exactly as the production binder resolves it."""
    found = hits(text, part["verbatimText"])
    if not found:
        raise ValueError("verbatim text not in old atom")
    if len(found) > 1:
        if part["occurrence"] is None:
            raise ValueError("ambiguous old verbatim")
        found = [found[part["occurrence"] - 1]]
    return found[0], found[0] + len(part["verbatimText"])


def nonspace_index(text):
    return [i for i, c in enumerate(text) if not c.isspace()]


def verbatim_for(new_text, a, b):
    """The v2 substring covering non-space characters [a, b) of the atom, and its 1-based occurrence."""
    idx = nonspace_index(new_text)
    sub = new_text[idx[a]: idx[b - 1] + 1]
    found = hits(new_text, sub)
    return sub, (found.index(idx[a]) + 1 if len(found) > 1 else None)


def translate_verbatim(old_text, new_text, part):
    s, e = old_span(old_text, part)
    idx = nonspace_index(old_text)
    inside = [k for k, i in enumerate(idx) if s <= i < e]
    if not inside:
        raise ValueError("verbatim has no non-space characters")
    a, b = inside[0], inside[-1] + 1
    sub, occurrence = verbatim_for(new_text, a, b)
    if squeeze(sub) != squeeze(part["verbatimText"]):
        raise ValueError("translated verbatim differs in non-space text")
    return sub, occurrence


GAP = 4  # a heading's parts lie within this many atoms of one another (a margin title beside body rows)


def rebind(item, new_atoms, old_atoms):
    """Semantic rebinding on the item's page.

    The wording's non-space text is cut into the pieces the old claim selected. A v2 part is a prefix (or the whole) of
    one atom that ends where the atom ends or where an old piece ends; consecutive parts lie in reading order, at most GAP
    atoms apart. The binding must be unique, else the item is refused.
    """
    wanted = squeeze(item["text"])
    page = item["page"]
    atoms = [a for a in new_atoms if a["page"] == page]
    ns = [squeeze(a["text"]) for a in atoms]
    cuts, run = set(), 0
    for part in item["parts"]:
        old = old_atoms[part["sourceAlias"]]
        piece = squeeze(part["verbatimText"]) if part["selectionMode"] == "VERBATIM_TEXT" else squeeze(old)
        run += len(piece)
        cuts.add(run)
    cuts.add(len(wanted))
    solutions = []

    def walk(pos, previous, chosen):
        if pos == len(wanted):
            solutions.append(list(chosen))
            return
        low = 0 if previous is None else previous + 1
        high = len(atoms) if previous is None else min(len(atoms), previous + 1 + GAP)
        for k in range(low, high):
            text = ns[k]
            m = 0
            while m < len(text) and pos + m < len(wanted) and text[m] == wanted[pos + m]:
                m += 1
            for length in range(1, m + 1):
                if length == len(text) or (pos + length) in cuts:
                    chosen.append((k, length))
                    walk(pos + length, k, chosen)
                    chosen.pop()

    walk(0, None, [])
    if len(solutions) != 1:
        raise ValueError(f"{len(solutions)} candidate bindings on page {page}, need exactly one")
    parts = []
    for k, length in solutions[0]:
        atom = atoms[k]
        if length == len(ns[k]):
            parts.append({"sourceAlias": atom["alias"], "selectionMode": "WHOLE_ALIAS", "verbatimText": None, "occurrence": None})
        else:
            sub, occurrence = verbatim_for(atom["text"], 0, length)
            parts.append({"sourceAlias": atom["alias"], "selectionMode": "VERBATIM_TEXT", "verbatimText": sub, "occurrence": occurrence})
    return parts


def main():
    repo, doc, new_tsv, old_tsv, structural_json = sys.argv[1:6]
    write = "--write" in sys.argv
    old, new = load(old_tsv), load(new_tsv)
    old_text = {a["alias"]: a["text"] for a in old}
    new_text = {a["alias"]: a["text"] for a in new}
    mapping = align(old, new)
    structural = set(json.load(open(structural_json, encoding="utf-8")))
    path = f"{repo}/eval/a99-closed-loop/source-review-v1/{doc}/review-items.json"
    raw = open(path, encoding="utf-8", newline="").read()
    data = json.loads(raw)
    report = {"remapped": 0, "verbatimRewritten": 0, "structuralRebound": [], "refused": []}
    for item in data["items"]:
        parts = item["parts"]
        mappable = all(p["sourceAlias"] in mapping for p in parts)
        if mappable:
            rebuilt = []
            try:
                for p in parts:
                    target = mapping[p["sourceAlias"]]
                    q = dict(p, sourceAlias=target)
                    if p["selectionMode"] == "VERBATIM_TEXT" and old_text[p["sourceAlias"]] != new_text[target]:
                        q["verbatimText"], q["occurrence"] = translate_verbatim(old_text[p["sourceAlias"]], new_text[target], p)
                        report["verbatimRewritten"] += 1
                    rebuilt.append(q)
            except ValueError as error:
                report["refused"].append((item["text"], str(error)))
                continue
            item["parts"] = rebuilt
            report["remapped"] += 1
        elif item["text"] in structural:
            try:
                item["parts"] = rebind(item, new, old_text)
            except ValueError as error:
                report["refused"].append((item["text"], str(error)))
                continue
            report["structuralRebound"].append(item["text"])
        else:
            report["refused"].append((item["text"], "alias without a v2 match and not an approved structural claim"))
    print(json.dumps(report, ensure_ascii=False, indent=1))
    if report["refused"]:
        print("REFUSED - nothing written")
        sys.exit(1)
    if write:
        text = json.dumps(data, ensure_ascii=False, indent=1)
        open(path, "w", encoding="utf-8", newline="\n").write(text + ("\n" if raw.endswith("\n") else ""))
        json.dump({a: b for a, b in mapping.items() if a != b},
                  open(f"{repo}/eval/a99-closed-loop/gold-migration-proposals/pdf-universe-v2/{doc}.alias-map.v1.json", "w", encoding="utf-8", newline="\n"), indent=1)
        print("written", path)


if __name__ == "__main__":
    main()
