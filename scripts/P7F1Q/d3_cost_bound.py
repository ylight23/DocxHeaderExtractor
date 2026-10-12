"""D3.1 cost bounds for provider canaries (offline; reads preflight artifacts; makes no provider call).

Two bounds are reported and must not be confused:
  planning  - uses the scripted preflight counts and measured input sizes. It is NOT a guarantee for Qwen.
  operating - the hard bound the runner must enforce before each call: structural call maximum with
              retrieval, MaxInputBytes for every request (tokens <= bytes), the chosen max_tokens, and the
              endpoint's tiered prices. Retries are zero by default.

Prices are read from the Gate E1 endpoint metadata artifact (raw response with SHA-256), not from memory.
Tier rule (conservative): a request whose UTF-8 byte count is >= the tier threshold is charged at that tier.
Tokens <= bytes, so a request below the threshold in bytes is certainly below it in tokens.
"""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ART = ROOT / "artifacts" / "web-pdf-semantic-diagnostic"
E1 = ART / "p7.f1.v6.d3-gate-e1-endpoints.raw.json"
PREFLIGHT = ART / "p7.f1.v6.d2-preflight.v6-retrieve-d23-pin"
OUT = ART / "p7.f1.v6.d3-cost-bound.v1.json"

PAYLOAD_CAP_BYTES = 131072        # node payload cap (user message only), unchanged
ENVELOPE_CAP_BYTES = 147456       # serialized request envelope cap (D3.1b); the whole body is what is priced
RETRIEVAL_ROUNDS = 2              # MaxRetrievalRounds in the protocol
VISIBLE_TOKENS = 16384            # visible output cap by bytes (tokens <= bytes)
REASONING_RESERVE = 16384         # assumed reasoning share; NOT verified at the endpoint
COMPLETION_CAPS = [VISIBLE_TOKENS + REASONING_RESERVE, 65536]  # the assumed cap, and the endpoint maximum


def endpoint(tag):
    raw = json.loads(E1.read_text(encoding="utf-8"))
    for e in raw["data"]["endpoints"]:
        if e["tag"] == tag:
            return e
    raise SystemExit(f"endpoint tag not found: {tag}")


def tiers(e):
    p = e["pricing"]
    base = (float(p["prompt"]), float(p["completion"]))
    over = [(o["min_prompt_tokens"], float(o["prompt"]), float(o["completion"])) for o in p.get("overrides", [])]
    return base, sorted(over)


def rate_for_bytes(base, over, request_bytes):
    """Conservative: the highest tier whose threshold the request's byte count reaches."""
    chosen = base
    for threshold, pr, co in over:
        if request_bytes >= threshold:
            chosen = (pr, co)
    return chosen


def call_cost(base, over, input_bytes, completion_tokens):
    pr, co = rate_for_bytes(base, over, input_bytes)
    return input_bytes * pr + completion_tokens * co


def structural_calls(node_counts, rounds):
    """Maximum model calls: the interpretation is one call; every local and reconcile node is one call; every synthesis or
    global node may use up to rounds+1 calls (rounds retrieval turns followed by one final response)."""
    fixed = 1 + node_counts["local"] + node_counts["reconcile"]
    retrieving = node_counts["synthesis"] + node_counts["global"]
    return fixed + retrieving * (rounds + 1)


def counts(doc, shard):
    j = json.loads((PREFLIGHT / f"{doc}.json").read_text(encoding="utf-8"))
    s = next(x for x in j["sizes"] if x["shardSize"] == shard)
    return {"local": s["local"].get("n", 0), "reconcile": s["reconcile"].get("n", 0),
            "synthesis": s["synthesis"].get("n", 0), "global": s["global"].get("n", 0),
            "scriptedRequests": s["requests"], "occurrences": s["candidates"]}


TAGS = ["alibaba", "alibaba/us-east-1"]   # both routes stay open until E2 closes; the bound is reported for each


def bound_for_tag(tag, plan):
    e = endpoint(tag)
    base, over = tiers(e)
    block = {
        "endpointTag": tag,
        "endpointName": e["name"],
        "tiers": {"base": {"promptUsdPerToken": base[0], "completionUsdPerToken": base[1]},
                  "overrides": [{"minPromptTokens": t, "promptUsdPerToken": p, "completionUsdPerToken": c} for t, p, c in over]},
        "scenarios": [],
    }
    for doc, shard, step in plan:
        nc = counts(doc, shard)
        calls_max = structural_calls(nc, RETRIEVAL_ROUNDS)
        for completion in COMPLETION_CAPS:
            # completion includes visible output and reasoning; the input is bounded by the internal budget for every call
            # the priced input is the whole request body, so the envelope cap bounds it, not the payload cap
            per_call = call_cost(base, over, ENVELOPE_CAP_BYTES, completion)
            block["scenarios"].append({
                "step": step, "document": doc, "shardSize": shard,
                "scriptedRequests": nc["scriptedRequests"], "structuralMaxCalls": calls_max,
                "completionTokensPerCall": completion, "maxRequestBytesPerCall": ENVELOPE_CAP_BYTES,
                "usdPerCallWorst": round(per_call, 6), "usdRunOperatingBound": round(per_call * calls_max, 4),
                "retriesIncluded": 0,
            })
    return block


def main():
    plan = [("073", 60, "C1"), ("094", 60, "C2"), ("024", 60, "C2"), ("028", 60, "C3")]
    result = {
        "artifact": "p7.f1.v6.d3-cost-bound.v1",
        "status": "PLANNING_AND_OPERATING_BOUNDS_NOT_A_PROVIDER_CALL",
        "model": "qwen/qwen3.7-flash",
        "endpointSource": E1.name,
        "endpointSourceSha256": __import__("hashlib").sha256(E1.read_bytes()).hexdigest(),
        "payloadCapBytes": PAYLOAD_CAP_BYTES,
        "envelopeCapBytes": ENVELOPE_CAP_BYTES,
        "retrievalRounds": RETRIEVAL_ROUNDS,
        "retries": 0,
        "routes": [bound_for_tag(tag, plan) for tag in TAGS],
        "c1Example": {"structuralMaxCalls": structural_calls(counts("073", 60), RETRIEVAL_ROUNDS),
                      "note": "interpretation + local + reconcile + global with two retrieval rounds"},
    }
    OUT.write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"wrote {OUT}")
    for block in result["routes"]:
        for s in block["scenarios"]:
            print(block["endpointTag"], s["step"], s["document"], "calls<=", s["structuralMaxCalls"],
                  "completion", s["completionTokensPerCall"], "usd/call", s["usdPerCallWorst"], "run<=", s["usdRunOperatingBound"])


if __name__ == "__main__":
    sys.exit(main())
