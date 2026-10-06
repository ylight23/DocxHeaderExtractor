"""Deterministic Gold alias-migration PROPOSAL for the font-independent PDF source universe (v2).

Read-only: it never writes Gold. It aligns the frozen v1 atom table of a document with the v2 atoms
(taken from the cross-platform determinism ledger) and reports, per Gold claim, whether every source
part maps to a v2 atom with byte-identical text. A claim is only EXACT_REMAP when all of its parts do and
the aliases keep their reading order; anything else is NEEDS_REVIEW and stays with the user.

No model call, no provider call, no Gold mutation.

usage: build_proposal.py <repo> <ledger-dir> <out-dir> SRC-089 SRC-029
"""
import difflib
import json
import os
import sys
from collections import Counter


def load_v1_atoms(repo, doc):
    path = os.path.join(repo, "eval/a99-closed-loop/source-review-v1", doc, "atom-layout-facts.tsv")
    atoms = []
    with open(path, encoding="utf-8") as handle:
        for line in handle.read().split("\n"):
            if not line:
                continue
            f = line.split("\t")
            atoms.append({"alias": f[0], "page": int(f[1]), "text": f[10]})
    return atoms


def load_v2_atoms(ledger, doc):
    ledger_doc = json.load(open(os.path.join(ledger, "windows", doc + ".ledger.json"), encoding="utf-8"))
    atoms = []
    for page in ledger_doc["pages"]:
        number = page["page"]["page"]
        for row in page["E"]["rows"]:
            alias, page_no, ordinal, r, seg, text = row.split("\x1f", 5)
            atoms.append({"alias": alias, "page": number, "text": text.replace("\t", " ")})
    return atoms


def squeeze(text):
    """Text with every whitespace removed: the v1 geometry dropped word spaces, v2 restores them."""
    return "".join(text.split())


def align(old, new):
    key_old = [(a["page"], squeeze(a["text"])) for a in old]
    key_new = [(a["page"], squeeze(a["text"])) for a in new]
    matcher = difflib.SequenceMatcher(None, key_old, key_new, autojunk=False)
    mapping = {}
    for tag, i1, i2, j1, j2 in matcher.get_opcodes():
        if tag == "equal":
            for offset in range(i2 - i1):
                mapping[old[i1 + offset]["alias"]] = new[j1 + offset]["alias"]
    return mapping


def main():
    repo, ledger, out, *docs = sys.argv[1:]
    os.makedirs(out, exist_ok=True)
    for doc in docs:
        old = load_v1_atoms(repo, doc)
        new = load_v2_atoms(ledger, doc)
        mapping = align(old, new)
        old_text = {a["alias"]: a["text"] for a in old}
        new_text = {a["alias"]: a["text"] for a in new}
        new_order = {a["alias"]: i for i, a in enumerate(new)}
        gold = json.load(open(os.path.join(repo, "eval/a99-closed-loop/gold", doc + ".gold.json"), encoding="utf-8"))
        claims = gold["occurrence"]["claims"]
        rows = []
        counts = Counter()
        for index, claim in enumerate(claims):
            parts = claim["sourceParts"]
            entries, problems, spacing = [], [], []
            for part in parts:
                alias = part["sourceAlias"]
                target = mapping.get(alias)
                entry = {"oldAlias": alias, "selectionMode": part["selectionMode"], "newAlias": target,
                         "aliasChanged": target is not None and target != alias}
                if target is None:
                    problems.append(f"{alias}: no v2 atom with the same page and non-space text")
                else:
                    entry["oldText"] = old_text.get(alias)
                    entry["newText"] = new_text.get(target)
                    if old_text.get(alias) != new_text.get(target):
                        spacing.append(alias)
                        if part["selectionMode"] != "WHOLE_ALIAS":
                            problems.append(f"{alias}: partial selection on an atom whose spacing changed")
                entries.append(entry)
            targets = [e["newAlias"] for e in entries if e["newAlias"]]
            if len(targets) == len(entries) and [new_order[t] for t in targets] != sorted(new_order[t] for t in targets):
                problems.append("mapped aliases are out of reading order")
            moved = any(e["aliasChanged"] for e in entries)
            if problems:
                status = "STRUCTURAL_CHANGE"
            elif spacing:
                status = "SPACING_ONLY" if not moved else "SPACING_AND_ALIAS_SHIFT"
            else:
                status = "UNCHANGED_TEXT" if not moved else "ALIAS_SHIFT_ONLY"
            counts[status] += 1
            rows.append({
                "claimIndex": index,
                "approvedWording": claim.get("approvedWording"),
                "status": status,
                "parts": entries,
                "problems": problems,
            })
        unmapped_old = sum(1 for a in old if a["alias"] not in mapping)
        proposal = {
            "schemaVersion": "a99-gold-alias-migration-proposal-v1",
            "authorityId": doc,
            "status": "PROPOSAL_NOT_APPLIED_AWAITING_USER_APPROVAL",
            "geometryPolicy": "a99-pdf-glyph-geometry-v2-font-independent",
            "modelCalls": 0,
            "goldMutation": "NONE",
            "universe": {"v1Atoms": len(old), "v2Atoms": len(new), "v1AtomsWithAnExactV2Match": len(mapping), "v1AtomsWithoutMatch": unmapped_old},
            "claims": {"total": len(claims), **dict(counts)},
            "rows": rows,
        }
        path = os.path.join(out, doc + ".alias-migration-proposal.v1.json")
        with open(path, "w", encoding="utf-8", newline="\n") as handle:
            json.dump(proposal, handle, ensure_ascii=False, indent=2)
            handle.write("\n")
        print(doc, proposal["universe"], proposal["claims"])


if __name__ == "__main__":
    main()
