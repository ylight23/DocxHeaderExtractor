"""Provider-free G2A audit for SRC-089: universe v1 (frozen captures) against universe v2 (8828480).

Reads only committed files and git history. The G2A user messages are re-composed from the atom tables and the accepted F1
establishes sets, and each composition is checked against the recorded request (v2 byte for byte, v1 by semanticRequestHash),
so the per-primary request diff is of the requests actually sent. Gold is read here, after both raw captures were frozen,
only to score them. No provider call, no model call, no write to Gold.
"""
import hashlib
import json
import subprocess
import sys

REPO = "."
ART = "artifacts/v5-p6t-function-membership/"
V2 = ART + "p6t-src089-pdf-universe-v2-requalification-20261007/"
GOLD_V1_COMMIT = "e107a0c^"   # the SRC-089 Gold before the coordinate migration


def git_show(rev, path):
    return subprocess.check_output(["git", "show", f"{rev}:{path}"]).decode("utf-8")


def atoms(text):
    rows = []
    for line in text.split("\n"):
        if line:
            f = line.split("\t")
            rows.append({"alias": f[0], "page": int(f[1]), "text": f[10]})
    return rows


def net_escape(text):
    """The framework default JSON encoder: non-ASCII and the HTML-sensitive ASCII characters as upper-case hex escapes."""
    import re
    bs = chr(92)
    text = re.sub(re.escape(bs) + "u([0-9a-f]{4})", lambda m: bs + "u" + m.group(1).upper(), text)
    for ch in "'+<>&":
        text = text.replace(ch, bs + "u%04X" % ord(ch))
    return text


def compose(atom_rows, establishes):
    """O# is the 1-based index of the atom among the pack's owned atoms (PACK_001 owns the first 96)."""
    occurrences = []
    for o in sorted(establishes):
        i = o - 1
        a = atom_rows[i]
        p, n = atom_rows[i - 1], atom_rows[i + 1]
        occurrences.append({
            "primary": f"O{o}", "page": a["page"], "text": a["text"], "upstreamFunction": "ESTABLISHES_STRUCTURE",
            "previous": {"occurrence": f"O{o - 1}", "page": p["page"], "text": p["text"], "selectable": False},
            "next": {"occurrence": f"O{o + 1}", "page": n["page"], "text": n["text"], "selectable": False}})
    return net_escape(json.dumps({"protocolVersion": "v5-function-conditioned-anchor-existence-1", "occurrences": occurrences},
                                 ensure_ascii=True, separators=(",", ":")))


def f1_establishes(content):
    out = set()
    for d in json.loads(content)["decisions"]:
        fn = d.get("function")
        if fn == "ESTABLISHES_STRUCTURE":
            out.add(int(str(d["occurrence"])[1:]))
    return out


def g2a_vector(content):
    return {int(d["primary"][1:]): d["anchor"] for d in json.loads(content)["decisions"]}


def gold_starts(gold_text, atom_rows):
    index = {a["alias"]: i for i, a in enumerate(atom_rows)}
    starts = set()
    for claim in json.loads(gold_text)["occurrence"]["claims"]:
        i = index[claim["sourceParts"][0]["sourceAlias"]]
        if i < 96:
            starts.add(i + 1)
    return starts


def metrics(vector, starts):
    has = "HAS_STRUCTURAL_EXTENT"
    tp = sum(1 for o, a in vector.items() if a == has and o in starts)
    fp = sum(1 for o, a in vector.items() if a == has and o not in starts)
    fn = sum(1 for o, a in vector.items() if a != has and o in starts)
    tn = sum(1 for o, a in vector.items() if a != has and o not in starts)
    precision = tp / (tp + fp) if tp + fp else 0.0
    recall = tp / (tp + fn) if tp + fn else 0.0
    f1 = 2 * precision * recall / (precision + recall) if precision + recall else 0.0
    return {"TP": tp, "FP": fp, "FN": fn, "TN": tn, "precision": round(precision, 4), "recall": round(recall, 4),
            "f1": round(f1, 4), "accuracy": round((tp + tn) / len(vector), 4)}


def squeeze_entry(entry):
    return json.dumps(entry, ensure_ascii=False, sort_keys=True).replace(" ", "").replace(chr(92) + "u00A0", "")


def brief(entry):
    return None if entry is None else {"text": entry["text"], "previous": entry["previous"]["text"], "next": entry["next"]["text"]}


def main():
    v1_atoms = atoms(git_show(GOLD_V1_COMMIT, "eval/a99-closed-loop/source-review-v1/SRC-089/atom-layout-facts.tsv"))
    v2_atoms = atoms(open(REPO + "/eval/a99-closed-loop/source-review-v1/SRC-089/atom-layout-facts.tsv", encoding="utf-8").read())

    v1_f1 = json.load(open(ART + "p6tf1-preflight/retry-src089-result.v1.json", encoding="utf-8"))["row"]["rawResponse"]
    v2_f1 = json.load(open(V2 + "call-01-F1.raw-capture.v1.json", encoding="utf-8"))["content"]
    v1_est, v2_est = f1_establishes(v1_f1), f1_establishes(v2_f1)

    v1_capture = json.load(open(ART + "p6tg2a-full-pack-population-canary-20261005/SRC-089.raw-capture.v1.json", encoding="utf-8"))
    v1_canary = json.load(open(ART + "p6tg2a-anchor-existence-canary-20261004/SRC-089.raw-capture.v1.json", encoding="utf-8"))
    v2_capture = json.load(open(V2 + "call-02-G2A.raw-capture.v1.json", encoding="utf-8"))
    v2_body = json.load(open(V2 + "call-02-G2A.request-body.json", encoding="utf-8"))

    v1_msg, v2_msg = compose(v1_atoms, v1_est), compose(v2_atoms, v2_est)
    v2_recorded = v2_body["messages"][1]["content"]
    checks = {
        "v2_message_recomposed_byte_for_byte": v2_msg == v2_recorded,
        "v1_message_sha256_matches_recorded_semanticRequestHash": hashlib.sha256(v1_msg.encode("utf-8")).hexdigest() == v1_capture["userMessageSha256"],
        "v1_recorded_semanticRequestHash": v1_capture["userMessageSha256"],
        "v1_recomposed_sha256": hashlib.sha256(v1_msg.encode("utf-8")).hexdigest(),
    }

    v1_gold = gold_starts(git_show(GOLD_V1_COMMIT, "eval/a99-closed-loop/gold/SRC-089.gold.json"), v1_atoms)
    v2_gold = gold_starts(open(REPO + "/eval/a99-closed-loop/gold/SRC-089.gold.json", encoding="utf-8").read(), v2_atoms)

    vectors = {
        "v1_canary_20261004": g2a_vector(v1_canary["rawResponse"]),
        "v1_population_20261005": g2a_vector(v1_capture["rawResponse"]),
        "v2_8828480": g2a_vector(v2_capture["content"]),
    }
    scores = {
        "v1_canary_20261004": metrics(vectors["v1_canary_20261004"], v1_gold),
        "v1_population_20261005": metrics(vectors["v1_population_20261005"], v1_gold),
        "v2_8828480": metrics(vectors["v2_8828480"], v2_gold),
    }

    primaries = sorted(set(vectors["v1_canary_20261004"]) | set(vectors["v1_population_20261005"]) | set(vectors["v2_8828480"]))
    v1m = {o["primary"]: o for o in json.loads(v1_msg)["occurrences"]}
    v2m = {o["primary"]: o for o in json.loads(v2_msg)["occurrences"]}
    rows = []
    for o in primaries:
        a, b = v1m.get(f"O{o}"), v2m.get(f"O{o}")
        same = a is not None and b is not None and a == b
        rows.append({
            "primary": f"O{o}",
            "goldStartV1": o in v1_gold, "goldStartV2": o in v2_gold,
            "v1Canary": vectors["v1_canary_20261004"].get(o), "v1Population": vectors["v1_population_20261005"].get(o),
            "v2": vectors["v2_8828480"].get(o),
            "issuedV1": a is not None, "issuedV2": b is not None, "requestEntryIdentical": same,
            "differences": [] if same or a is None or b is None else [k for k in ("page", "text", "previous", "next") if a[k] != b[k]],
            "differencesAreWhitespaceOnly": None if same or a is None or b is None else squeeze_entry(a) == squeeze_entry(b),
            "v1Entry": brief(a), "v2Entry": brief(b),
        })

    false_positives = [r["primary"] for r in rows if r["v2"] == "HAS_STRUCTURAL_EXTENT" and not r["goldStartV2"]]
    both = [r for r in rows if r["issuedV1"] and r["issuedV2"]]
    audit = {
        "schemaVersion": "v5-p6tg2a-src089-v1-v2-audit-v1",
        "providerCalls": 0, "modelCalls": 0, "goldMutation": "NONE",
        "goldReadAfterRawCaptureFreeze": True,
        "status": "G2A_V2_QUALITY_FAILED_NOT_PROMOTABLE",
        "rawEvidence": {"v2": "8828480 (immutable)",
                        "v1Canary": ART + "p6tg2a-anchor-existence-canary-20261004/SRC-089.raw-capture.v1.json",
                        "v1Population": ART + "p6tg2a-full-pack-population-canary-20261005/SRC-089.raw-capture.v1.json"},
        "requestRecomposition": checks,
        "f1": {"v1EstablishesCount": len(v1_est), "v2EstablishesCount": len(v2_est),
               "onlyV1": sorted(f"O{o}" for o in v1_est - v2_est), "onlyV2": sorted(f"O{o}" for o in v2_est - v1_est)},
        "goldStartPrimariesAmongPack001": {"v1": sorted(f"O{o}" for o in v1_gold), "v2": sorted(f"O{o}" for o in v2_gold)},
        "scores": scores,
        "v2FalsePositiveHasAnchors": false_positives,
        "requestEntriesIdenticalForFalsePositives": {p: next(r["requestEntryIdentical"] for r in rows if r["primary"] == p) for p in false_positives},
        "requestEntriesChangedOverall": sum(1 for r in both if not r["requestEntryIdentical"]),
        "requestEntriesIssuedBoth": len(both),
        "requestEntriesChangedOnlyByWhitespace": sum(1 for r in both if r["differencesAreWhitespaceOnly"]),
        "sameV1RequestTwoRuns": {"requestSha256": v1_capture["providerBodySha256"], "canaryRequestSha256": v1_canary["providerRequestHash"],
                                 "identicalRequest": v1_capture["providerBodySha256"] == v1_canary["providerRequestHash"],
                                 "runsDifferOn": sorted(f"O{o}" for o in vectors["v1_canary_20261004"] if vectors["v1_canary_20261004"][o] != vectors["v1_population_20261005"].get(o))},
        "tokens": {"v1CanaryPrompt": v1_canary["promptTokens"], "v1PopulationPrompt": v1_capture["promptTokens"],
                   "v2Prompt": v2_capture["call"]["PromptTokens"]},
        "perPrimary": rows,
        "note": "Which part of the V2 change comes from the source delta and which from inference nondeterminism is not separated by this audit.",
    }
    path = ART + "p6tg2a-src089-v1-v2-audit/g2a-v1-v2-audit.v1.json"
    open(path, "w", encoding="utf-8", newline="\n").write(json.dumps(audit, ensure_ascii=False, indent=1) + "\n")
    keys = ("requestRecomposition", "f1", "goldStartPrimariesAmongPack001", "scores", "v2FalsePositiveHasAnchors",
            "requestEntriesIdenticalForFalsePositives", "requestEntriesChangedOverall", "requestEntriesChangedOnlyByWhitespace", "requestEntriesIssuedBoth", "sameV1RequestTwoRuns")
    print(json.dumps({k: audit[k] for k in keys}, indent=1))


if __name__ == "__main__":
    sys.exit(main())
