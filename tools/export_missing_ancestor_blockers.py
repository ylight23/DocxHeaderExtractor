"""Export unique, explicitly reported MISSING_ANCESTOR blockers without guessing lost provenance.

Reads a saved Web NDJSON result and hash-matched OCR evidence; never calls a provider.
Source text is kept in this local report, not added to the share-safe placement audit.
"""
import argparse
import hashlib
import json
from pathlib import Path


def get(obj, name, default=None):
    return next((v for k, v in obj.items() if k.casefold() == name.casefold()), default) if isinstance(obj, dict) else default


def export(audit_path, source_path, ocr_path, output_prefix):
    events = [json.loads(line) for line in audit_path.read_text(encoding="utf-8-sig").splitlines() if line.strip()]
    result = next((e for e in reversed(events) if get(e, "type") == "result"), None)
    if result is None:
        raise ValueError("No completed Web result event; an unfinished run is not an audit.")
    pipeline = get(result, "pipeline")
    audit = get(pipeline, "audit")
    placement = get(audit, "placementExecution")
    if not isinstance(placement, dict):
        raise ValueError("Result has no placement execution audit.")
    ocr = json.loads(ocr_path.read_text(encoding="utf-8-sig"))
    source_sha = hashlib.sha256(source_path.read_bytes()).hexdigest()
    if source_sha != get(ocr, "sourceSha256"):
        raise ValueError("OCR evidence is from another source document.")
    original = next(v for v in get(ocr, "views") if get(get(v, "spec"), "viewId") == "ocr-original")
    texts = {get(line, "id"): get(line, "text") for page in get(original, "pages") for line in get(page, "lines")}
    states = get(audit, "headingAliasStates", {})
    trace = {get(s, "alias"): s for s in get(placement, "sourceTrace", [])}
    recorded = {get(b, "alias"): b for b in get(placement, "missingAncestorBlockers", [])}
    blockers = {}
    missing = [d for d in get(placement, "decisions", []) if get(d, "verificationStatus") == "MISSING_ANCESTOR"]
    for decision in missing:
        for alias in get(decision, "blockingAliases", []):
            blockers.setdefault(alias, set()).add(get(decision, "alias"))
    rows = []
    for alias, children in sorted(blockers.items()):
        state, source, observed = states.get(alias, {}), trace.get(alias, {}), recorded.get(alias, {})
        source_id = get(source, "sourceId", get(observed, "sourceId"))
        provenance = get(observed, "provenance", get(state, "provenance", []))
        rows.append({
            "alias": alias, "sourceId": source_id, "text": texts.get(source_id),
            "textBasis": "hash-matched ocr-original; not a recovered model-selected reading",
            "blockedHeadingCount": len(children), "blockedHeadingAliases": sorted(children),
            "initialSelection": get(state, "initialSelection", get(source, "initialSelection")),
            "finalSelection": get(state, "finalSelection", get(source, "finalSelection")),
            "verificationMembership": get(state, "verificationMembership", get(source, "verificationMembership")),
            "bindingStatus": get(source, "bindingStatus", get(observed, "bindingStatus", "not-recorded")),
            "provenanceAvailability": "recorded" if provenance else "not-recorded",
            "provenance": provenance,
            "failureCodes": sorted({code for p in provenance for code in get(p, "failureCodes", [])}),
        })
    if any(r["text"] is None for r in rows):
        raise ValueError("A blocker source ID is absent from the hash-matched original OCR view.")
    counts = {}
    for d in missing:
        key = tuple(sorted(set(get(d, "blockingAliases", []))))
        counts.setdefault(key, set()).add(get(d, "alias"))
    semantic = get(audit, "semanticLane", {})
    report = {
        "executionId": get(pipeline, "executionId"), "sourceSha256": source_sha,
        "uniqueBlockerAliases": len(rows), "uniqueBlockedHeadings": len({get(d, "alias") for d in missing}),
        "blockers": rows,
        "blockerSets": [{"aliases": list(k), "blockedHeadingCount": len(v), "blockedHeadingAliases": sorted(v)} for k, v in counts.items()],
        "unitFailureContext": get(semantic, "failureClass"),
        "limitations": [
            "Counts overlap when multiple source lines block the same heading; do not sum per-alias counts.",
            "An absent per-alias history is not-recorded. Global unit failures are context, not an inferred alias-specific cause.",
            "Text comes from hash-matched primary OCR, not reconstructed model replies.",
        ],
    }
    output_prefix.parent.mkdir(parents=True, exist_ok=True)
    output_prefix.with_suffix(".json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    def cell(value):
        return str(value if value is not None else "not-recorded").replace("|", "\\|").replace("\n", " ")

    md = [f"Execution `{report['executionId']}`: {len(rows)} unique source aliases block {report['uniqueBlockedHeadings']} unique headings.",
          "", "Text: hash-matched primary OCR. Counts overlap for multi-line ancestors.", "",
          "| Blocker alias | Text | Blocked headings | Initial | Final | Verifier membership | Binding | Failure/reason provenance |",
          "|---|---|---:|---|---|---|---|---|"]
    for row in rows:
        reason = "; ".join(row["failureCodes"]) or ("history recorded; no protocol failure" if row["provenance"] else "not-recorded")
        md.append("| " + " | ".join(cell(v) for v in [row["alias"], row["text"], row["blockedHeadingCount"], row["initialSelection"],
                  row["finalSelection"], row["verificationMembership"], row["bindingStatus"], reason]) + " |")
    md.extend(["", *["- " + note for note in report["limitations"]], ""])
    output_prefix.with_suffix(".md").write_text("\n".join(md), encoding="utf-8")
    return report


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--audit", type=Path, required=True)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--ocr-evidence", type=Path, required=True)
    parser.add_argument("--output-prefix", type=Path, required=True)
    args = parser.parse_args()
    report = export(args.audit, args.source, args.ocr_evidence, args.output_prefix)
    print(json.dumps({k: report[k] for k in ("executionId", "uniqueBlockerAliases", "uniqueBlockedHeadings")}))
