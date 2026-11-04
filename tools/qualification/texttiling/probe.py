"""Offline probe: do TextTiling boundaries fall on the headings that every extraction run agrees on? Writes numbers only, never paragraph text.

Run from this folder: python probe.py  (reads the qualification artifacts of 2026-11-03/04; writes ../../../artifacts/qualification/texttiling-probe-20261104/summary.json)
"""
import json
import statistics
from pathlib import Path

from texttiling import body_paragraphs, depth_scores

ROOT = Path(__file__).resolve().parents[3]
ART = ROOT / "artifacts" / "qualification"
SNAP = ART / "monthly-qwen-snapshots-20261103"
OUT = ART / "texttiling-probe-20261104" / "summary.json"


def sid(h: dict) -> str:
    return h.get("stableId") or h.get("StableId")


def index_of(stable_id: str) -> int:
    return int(stable_id.split("p[")[1].rstrip("]")) - 1   # body[1]/p[N] -> N-1


def runs_for(name: str) -> list[set[str]]:
    """Heading locations of every extraction run available for one report, each run a set of stable ids."""
    runs = []
    if name == "09":   # five fresh extractions of the same file (variance trial, original variant)
        v = json.loads((ART / "gather-variance-monthly-20261104" / "variance.json").read_text(encoding="utf-8"))
        runs += [{sid(h) for h in r["headings"]} for r in v["runs"] if r.get("variant") == "original"]
    else:
        for stored in ["gather-e2e-monthly-20261104/outline-store", "gather-e2e-monthly-20261104b/outline-store",
                       "gather-e2e-monthly-20261104c/outline-store"]:
            folder = {"08": "9a24e9b790a324ae", "10": "587141dff42b9f98"}[name]
            for f in (ART / stored / folder).glob("*.json"):
                runs.append({sid(h) for h in json.loads(f.read_text(encoding="utf-8"))["Headings"]})
        base = ART / "docx-vn-aug-sep-oct-baseline-20261103" / f"{name}.json"
        runs.append({sid(h) for h in json.loads(base.read_text(encoding="utf-8"))["headings"]})
    return runs


def evaluate(name: str, k: int = 3) -> dict:
    paragraphs = body_paragraphs(str(SNAP / f"{name}.docx"))
    depths = depth_scores(paragraphs, k=k)            # depths[i] is the gap before paragraph i+1 (0-based index i+1)
    gap_depth = {i: depths[i - 1] for i in range(1, len(paragraphs))}
    ranked = sorted(gap_depth, key=lambda i: -gap_depth[i])
    runs = runs_for(name)
    n_runs = len(runs)
    stable = set.intersection(*runs)
    varying = set.union(*runs) - stable
    stable_idx = {index_of(s) for s in stable if index_of(s) in gap_depth}
    varying_idx = {index_of(s) for s in varying if index_of(s) in gap_depth}
    rank = {i: r for r, i in enumerate(ranked)}   # 0 = deepest gap
    # Boundaries: the M deepest gaps, M = number of stable headings. A hit is a boundary within one paragraph of a stable heading.
    m = len(stable_idx)
    top = set(ranked[:m])
    hits = sum(1 for b in top if any(abs(b - s) <= 1 for s in stable_idx))
    chance = min(1.0, m * 3 / max(1, len(gap_depth)))
    pct = lambda xs: round(100 * statistics.median(xs) / len(gap_depth), 1) if xs else None
    return {
        "report": name, "paragraphs": len(paragraphs), "runs": n_runs, "stableHeadings": len(stable_idx),
        "varyingHeadings": len(varying_idx), "medianRankPercentStable": pct([rank[i] for i in stable_idx]),
        "medianRankPercentVarying": pct([rank[i] for i in varying_idx]),
        "topBoundaryHitRate": round(hits / m, 2) if m else None, "chanceHitRate": round(chance, 2),
        "depthStableMedian": round(statistics.median([gap_depth[i] for i in stable_idx]), 3) if stable_idx else None,
        "depthVaryingMedian": round(statistics.median([gap_depth[i] for i in varying_idx]), 3) if varying_idx else None,
    }


if __name__ == "__main__":
    results = [evaluate(n) for n in ["08", "09", "10"]]
    # Sensitivity to the block size: the direction of the result must hold for every k before any use is considered.
    sensitivity = {k: [evaluate(n, k) for n in ["08", "09", "10"]] for k in (2, 3, 4, 6)}
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(json.dumps({"k3": results, "sensitivity": sensitivity}, indent=2, ensure_ascii=False), encoding="utf-8")
    for r in results:
        print(r)
