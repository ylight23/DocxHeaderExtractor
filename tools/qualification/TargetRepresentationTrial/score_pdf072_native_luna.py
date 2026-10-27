"""Score a frozen public-PdfHandler PDF072 Luna run and assemble FP evidence."""
from __future__ import annotations

import hashlib
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]


def read(path: Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def alias_key(alias: str):
    match = re.fullmatch(r"L(\d+):S(\d+)", alias)
    if not match:
        raise ValueError(f"BAD_ALIAS:{alias}")
    return int(match.group(1)), int(match.group(2))


def scores(expected: set, predicted: set, key_name: str):
    tp, fp, fn = expected & predicted, predicted - expected, expected - predicted
    return {
        "tp": len(tp), "fp": len(fp), "fn": len(fn),
        "precision": len(tp) / len(predicted) if predicted else 0.0,
        "recall": len(tp) / len(expected) if expected else 0.0,
        f"falsePositive{key_name}": sorted(fp), f"missing{key_name}": sorted(fn),
    }


def main(run: Path):
    manifest = read(run / "run-manifest.json")
    frozen = read(run / manifest["frozenDecisionFile"])
    if frozen["status"] != "FROZEN_BEFORE_GOLD":
        raise ValueError("SEMANTIC_DECISIONS_NOT_FROZEN")
    if sha(run / manifest["frozenDecisionFile"]) != manifest["frozenDecisionsSha256"]:
        raise ValueError("FROZEN_DECISION_HASH_MISMATCH")

    identity = read(run / "run-identity.json")
    registry_path = ROOT / "eval/a99-closed-loop/gold-current/registry.v1.json"
    registry = read(registry_path)
    entry = next(e for e in registry["authorities"] if e["sourceSha256"] == identity["sourceSha256"])
    gold_path = ROOT / entry["canonicalGoldPath"]
    if sha(gold_path) != entry["goldSha256"]:
        raise ValueError("GOLD_HASH_MISMATCH")
    gold = read(gold_path)
    if gold["source"]["sourceSha256"] != identity["sourceSha256"]:
        raise ValueError("GOLD_SOURCE_HASH_MISMATCH")

    result = read(run / "native-run-result.json")
    units = result["Result"]["sourceCatalog"]["units"]
    requests = []
    for request_path in (run / "luna-requests").glob("*.request.json"):
        req_sha = request_path.name.removesuffix(".request.json")
        body = read(request_path)
        payload = json.loads(body["messages"][1]["content"][0]["text"])
        if payload.get("stage") == "SEGMENT":
            requests.extend((target["alias"], target["text"], payload, request_path)
                            for target in payload["targets"])
    requests.sort(key=lambda row: alias_key(row[0]))
    if len(requests) != len(units) or len({r[0] for r in requests}) != len(units):
        raise ValueError("SOURCE_ALIAS_COVERAGE_MISMATCH")
    alias_to_unit = {row[0]: units[i] for i, row in enumerate(requests)}
    alias_to_text = {row[0]: row[1] for row in requests}
    alias_to_evidence = {}
    for alias, text, payload, request_path in requests:
        facts = next((f for f in payload.get("targetFacts", []) if f.get("alias") == alias), None)
        geom = None
        geometry = payload.get("pageGeometryEvidence", {})
        page_num = facts.get("page") if facts else None
        page = next((p for p in geometry.get("pages", []) if p.get("page") == page_num), None)
        if page is not None:
            geom = next((o for o in page.get("occurrences", []) if o.get("alias") == alias), None)
        alias_to_evidence[alias] = {"facts": facts, "geometry": geom, "geometryPage": page,
                                    "requestSha256": request_path.name.removesuffix(".request.json"),
                                    "requestPath": str(request_path.relative_to(run))}

    selected = {}
    for pair in frozen["requestReplyPairs"]:
        response = read(run / "luna-responses" / pair["responseFile"])
        semantic = json.loads(response["content"])
        if semantic.get("protocolVersion") == "PDF_SELECT_SOURCE_BOUND_V1":
            for decision in semantic.get("decisions", []):
                if decision["decision"] == "SELECT":
                    selected[decision["alias"]] = {
                        "reason": decision.get("reason"), "evidenceRefs": decision.get("evidenceRefs", []),
                        "analysis": semantic.get("analysis"), "requestSha256": pair["requestSha256"],
                    }

    source_ordinal_to_alias = {int(unit["sourceOrdinal"]): alias for alias, unit in alias_to_unit.items()}
    actual_groups = []
    for element in result["Result"]["structure"]["elements"]:
        if element.get("type") != "Heading":
            continue
        aliases = [source_ordinal_to_alias[s["sourceOrdinal"]] for s in element["sources"]]
        actual_groups.append(tuple(aliases))
    actual_aliases = {alias for group in actual_groups for alias in group}

    gold_groups = [tuple(part["sourceAlias"] for part in claim["sourceParts"])
                   for claim in gold["semantic"]["claims"]]
    expected_aliases = {alias for group in gold_groups for alias in group}
    expected_groups = set(gold_groups)
    actual_group_set = set(actual_groups)
    membership_score = scores(expected_aliases, actual_aliases, "Aliases")
    group_score = scores(expected_groups, actual_group_set, "Groups")

    fp_notes = {
        "L0522:S1": ("AGENDA_ITEM_CLASSIFIED_AS_BODY_HEADING",
                     "Page 13 is the Agenda schedule. The selected row names a timed welcome item and includes the speaker roles. Its formatting and mapped raster occurrence are present; the model rationale explicitly treats a scheduled agenda item as a document heading."),
        "L0524:S1": ("AGENDA_ITEM_CLASSIFIED_AS_BODY_HEADING",
                     "Page 13 is the Agenda schedule. This is the scheduled cycle-update item with presenter text, following a time row. Layout and raster evidence are present; the model treated the agenda entry as a document heading."),
        "L0527:S1": ("AGENDA_ITEM_CLASSIFIED_AS_BODY_HEADING",
                     "Page 13 is the Agenda schedule. This is the scheduled research-topics item, with a presenter label and a neighboring time occurrence. Formatting and raster evidence are present; the model promoted the agenda item to a document heading."),
        "L0528:S1": ("AGENDA_ITEM_CLASSIFIED_AS_BODY_HEADING",
                     "Page 13 is the Agenda schedule. This is a timed forthcoming-research item with a presenter label, parallel to other agenda rows. Formatting and raster evidence are present; the model promoted the schedule entry to a document heading."),
        "L0529:S1": ("AGENDA_ITEM_CLASSIFIED_AS_BODY_HEADING",
                     "Page 13 is the Agenda schedule. This is a timed agenda-setting item with a presenter label, aligned with the other schedule topics. Formatting and raster evidence are present; the model treated the listed event as a document heading."),
        "L0532:S0": ("TIME_AND_TOPIC_SEGMENTS_COLLAPSED",
                     "The source line combines a time range and the TAG-composition agenda item. Raster geometry reports two separate horizontal ink runs, and layout reports separated visual segments; both measurements are present. The model rationale focused on the topic and ignored the time-plus-topic schedule-row structure."),
        "L0546:S1": ("AGENDA_ITEM_CLASSIFIED_AS_BODY_HEADING",
                     "Page 14 continues the Agenda schedule. The topic is the second of two visual segments, with a measured 48.5-point gap before it and a same-baseline peer; raster and formatter facts are present. The model treated the listed schedule topic as a document heading."),
    }
    false_positive_audit = []
    for alias in sorted(actual_aliases - expected_aliases, key=alias_key):
        unit = alias_to_unit[alias]
        evidence = alias_to_evidence[alias]
        facts = evidence["facts"]
        geometry = evidence["geometry"]
        selected_decision = selected.get(alias, {})
        note = fp_notes.get(alias)
        if geometry is None:
            availability = "MISSING_RASTER_OCCURRENCE"
            assessment = "The request had no alias-specific raster occurrence to support this decision; this is a missing evidence case."
        else:
            availability = "RASTER_OCCURRENCE_PRESENT"
            assessment = note[1] if note else (
                "Raster and layout evidence were present in the request. This is a model-versus-Gold interpretation or scope mismatch, "
                "not a missing-evidence case. The rationale below shows the model's stated basis."
            )
        false_positive_audit.append({
            "alias": alias,
            "rawText": alias_to_text[alias],
            "sourceOrdinal": unit["sourceOrdinal"],
            "page": facts.get("page") if facts else None,
            "location": {
                "page": facts.get("page") if facts else None,
                "bboxPdfPoints": (facts or {}).get("layout", {}).get("bbox"),
                "leftEdge": (facts or {}).get("leftEdge"),
                "readingOrderOrdinal": unit["sourceOrdinal"],
            },
            "rasterGeometry": {
                "fieldPresentInRequest": evidence["geometryPage"] is not None,
                "aliasOccurrencePresent": geometry is not None,
                "pageStatus": (evidence["geometryPage"] or {}).get("status"),
                "version": (evidence["geometryPage"] or {}).get("version"),
                "values": geometry,
            },
            "formattingAndLayoutFacts": facts,
            "classificationRationale": selected_decision,
            "goldComparison": "Predicted structural heading occurrence absent from approved Gold claims.",
            "evidenceAssessment": {"classification": availability,
                                   "causeClass": note[0] if note else "PRESENT_EVIDENCE_INTERPRETATION_MISMATCH",
                                   "interpretationIsInference": True, "interpretation": assessment},
        })

    run_audit = read(run / "bridge-calls.json")
    score = {
        "authorityId": entry["authorityId"],
        "goldSha256": entry["goldSha256"],
        "registrySha256": sha(registry_path),
        "sourceSha256": identity["sourceSha256"],
        "frozenDecisionsSha256": manifest["frozenDecisionsSha256"],
        "scope": {"sourceAliases": len(alias_to_unit), "goldAliases": len(expected_aliases),
                  "predictedHeadingAliases": len(actual_aliases), "goldGroups": len(expected_groups),
                  "predictedGroups": len(actual_group_set)},
        "nativeProductionGroupMembership": membership_score,
        "nativeProductionExactGroups": group_score,
        "predictedGroups": [list(g) for g in actual_groups],
        "callAccounting": {"total": len(run_audit["calls"]), "phaseCounts": manifest["phases"],
                           "maxConcurrentCallsObserved": run_audit["maxConcurrentCalls"],
                           "productionRetryLimit": identity["selector"]["retryPolicy"],
                           "transportRetriesUsed": manifest["transportRetriesUsed"]},
        "placementAudit": {
            "headingCount": len(result["Outline"]["headings"]),
            "hierarchyResolutions": dict((name, sum(1 for h in result["Outline"]["headings"]
                                                      if h.get("hierarchyResolution") == name))
                                          for name in sorted({h.get("hierarchyResolution") for h in result["Outline"]["headings"]})),
            "goldHierarchyEvaluable": gold.get("capabilities", {}).get("hierarchy", False),
        },
        "falsePositiveCount": len(false_positive_audit),
        "limitations": [
            "Gold hierarchy capability is not evaluable; placement results are reported as pipeline output, not hierarchy accuracy.",
            "One local Luna-authored run does not support a general model-quality claim.",
            "Transport timing includes time waiting for Luna-authored response files; it is not model-generation latency.",
        ],
    }
    (run / "gold-score.json").write_text(json.dumps(score, indent=2, ensure_ascii=False), encoding="utf-8")
    (run / "fp-evidence-audit.json").write_text(json.dumps({
        "sourceSha256": identity["sourceSha256"], "goldSha256": entry["goldSha256"],
        "goldAuthorityId": entry["authorityId"], "classificationBasis": "approved Gold set comparison",
        "auditCount": len(false_positive_audit), "falsePositives": false_positive_audit,
    }, indent=2, ensure_ascii=False), encoding="utf-8")
    print(json.dumps({"score": score, "falsePositiveEvidenceAudit": str(run / "fp-evidence-audit.json")},
                     indent=2, ensure_ascii=False))


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("usage: score_pdf072_native_luna.py <frozen-run-directory>")
    main(Path(sys.argv[1]).resolve())
