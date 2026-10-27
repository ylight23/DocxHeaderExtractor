"""Validate the user-approved title amendment and rescore saved runs; no inference."""
import hashlib
import json
from pathlib import Path

from score_serial_calls import ROOT, read, score


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    output = ROOT / "artifacts/qualification/pdf072-title-gold-correction-20261025"
    amendment = read(output / "amendment.json")
    registry = read(ROOT / "eval/a99-closed-loop/gold-current/registry.v1.json")
    before_registry = read(output / "registry-before.json")
    assert {k: v for k, v in registry.items() if k != "authorities"} == {
        k: v for k, v in before_registry.items() if k != "authorities"}
    checked = []
    for entry, previous in zip(registry["authorities"], before_registry["authorities"], strict=True):
        assert sha(ROOT / entry["canonicalGoldPath"]) == entry["goldSha256"], entry["authorityId"]
        if entry["authorityId"] != "DOC-0252":
            assert entry == previous
        else:
            assert {k: v for k, v in entry.items() if k != "goldSha256"} == {
                k: v for k, v in previous.items() if k != "goldSha256"}
            gold = read(ROOT / entry["canonicalGoldPath"])
            assert entry["goldSha256"] == amendment["newGoldSha256"]
        checked.append(entry["authorityId"])
    before = read(output / "canonical-before.json")
    assert sha(output / "canonical-before.json") == amendment["oldGoldSha256"]
    for section in ["semantic", "occurrence"]:
        assert len(gold[section]["claims"]) == len(before[section]["claims"]) == 42
        assert gold[section]["claims"][1:] == before[section]["claims"][1:]
        assert [p["sourceAlias"] for p in gold[section]["claims"][0]["sourceParts"]] == ["L0000:S0", "L0001:S0"]
    assert gold["capabilities"] == before["capabilities"]
    inherited_provenance_mismatches = []
    for item in gold["provenance"]:
        if not item["path"].startswith("predecessor-gold:"):
            actual_hash = sha(ROOT / item["path"])
            if actual_hash != item["sha256"]:
                # Preserve old historical citations; never silently re-author them.
                assert item in before["provenance"], item["path"]
                inherited_provenance_mismatches.append({"path": item["path"],
                    "historicalRecordedSha256": item["sha256"], "currentFileSha256": actual_hash})
    approval = read(ROOT / amendment["approvalRecord"])
    assert approval["userFinalApproval"] and approval["scope"] == "FIRST_DOCUMENT_TITLE_ONLY"
    assert gold["occurrence"]["claims"][0]["projectedText"] == approval["approvedWording"]
    assert " ".join(approval["sourceTexts"][p["sourceAlias"]] for p in approval["afterSourceParts"]) == approval["approvedWording"]
    structured_path = ROOT / "eval/a99-closed-loop/canonical-semantic-gold-vnext/occurrence/DOC-0252.structured-source-parts.occurrence-gold.v1.json"
    structured = read(structured_path)
    assert sha(structured_path) == amendment["newStructuredSha256"]
    assert structured["boundOccurrences"][1:] == read(output / "structured-before.json")["boundOccurrences"][1:]
    assert structured["boundOccurrences"][0] == gold["occurrence"]["claims"][0]
    results = []
    for run_name in ["full-01", "full-02"]:
        run = ROOT / "artifacts/qualification/pdf072-membership-v6-pairwise-20261024" / run_name / "run-result.json"
        result = score(run)
        assert result["aliasMembership"]["tp"] == 45
        assert result["exactAliasBoundaries"]["tp"] == 42
        for key in ["aliasMembership", "exactAliasBoundaries"]:
            assert result[key]["fp"] == result[key]["fn"] == 0
        (output / f"{run_name}.score.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
        results.append({"run": run_name, "elapsedSeconds": result["elapsedSeconds"],
                        "aliasMembership": result["aliasMembership"],
                        "exactAliasBoundaries": result["exactAliasBoundaries"],
                        "unresolvedParents": result["unresolvedParents"]})
    report = {"authorityId": "DOC-0252", "oldGoldSha256": amendment["oldGoldSha256"],
              "newGoldSha256": amendment["newGoldSha256"], "validatedRegistryAuthorities": checked,
              "other41ClaimsUnchanged": True, "capabilitiesUnchanged": True,
              "inheritedHistoricalProvenanceHashMismatches": inherited_provenance_mismatches,
              "historicalRunArtifactsPreserved": True, "newModelCalls": 0, "results": results}
    (output / "rescoring.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
