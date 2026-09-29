# Sliding-window packing — offline calibration (2026-09-28)

Status: `SLIDING_WINDOW_NOT_ADOPTED` · provider calls `0` · Gold read `false`

Source: the 31 accepted T3B reasoning-none leaves (P05 packing), per-leaf telemetry in
`openrouter-streaming-t3c-efficiency-heavy-leaf-audit.v1.json` and
`openrouter-streaming-t5a-selective-medium-regression-localization.v1.json`.

## What binds P05 today

| measure | observed over 31 leaves |
|---|---|
| owned atoms per leaf | 96 in 29 leaves (the owned cap); 94 and 6 in the two document tails |
| prompt tokens per leaf | 19,324 – 21,787 (spread about ±6% around ~20.1k) |
| serialized input bytes per leaf | 64,855 – 71,592, against a 90,000-byte ceiling |
| estimator (bytes / 6) vs real prompt tokens | ~11.7k estimated vs ~20.1k real: real bytes/token ≈ 3.5, the estimator is 1.7× low |
| halo share of visible atoms | 16 / 112 ≈ 14% |

The byte and token ceilings never bound; every full leaf stopped at the 96-owned cap. Input size is
already uniform, so there is no input-side tail to smooth.

## Where the cost actually is

Completion tokens total 46,956. Six leaves (SRC-095 ordinals 8, 10, 11, 26, 30, 31 — contents/index
regions) carry 32,131 of them (68%) at 35–47 s wall each; the other 25 leaves take roughly 4–13 s.
Completion volume follows how many decisions a region contains, not how many input tokens it has.

## The candidate, measured against the gate

The gate (user, 2026-09-28): promote only if F1 holds and there is a meaningful operational gain;
equal F1 with a few percent fewer tokens and more complexity keeps P05.

- **Larger windows** (fewer calls): with 8-atom halos, doubling the owned core cuts the halo share
  from ~14% to ~8%, i.e. at most ~6% of prompt tokens, and halves the calls. The heavy regions would
  then produce ~10–12k completion tokens per leaf, at or over the 12,288 reserved ceiling — length
  saturation, which the gate requires to be zero — and the tail latency roughly doubles.
- **Layout-boundary cuts** (no group split): the P05 boundary audit found 0 false negatives crossing
  a primary boundary and fewer false positives near boundaries (20) than inside leaves (41). Nothing
  here to recover.
- **Token-budget tiers** (preferred / soft / hard): they cannot move anything while the owned cap is
  what binds and input is already within ±6%.

An input-token-budgeted sliding window therefore has no measurable gain to offer on this cohort and
adds a packer. P05 stays.

## What would move the tail

The tail comes from decision-dense regions. The lever is splitting dense regions *before* the call,
with a density signal observed in the source itself (for example many short atoms per page), not
input tokens. That is an output-aware packing experiment — a new design with its own offline test and
provider run — not a sliding window. It is not implemented.

## Note on the current production request

After T3B the owned style facts changed to raw measurements and ratios, which makes each atom's
evidence larger. Under the same P05 budget the first SRC-089 leaf now owns 88 atoms instead of 96, so
the byte ceiling binds and production sends more, slightly smaller calls than T3B did. The
re-baseline preflight (`pdf-v4-production-rebaseline-preflight.v1.json`) records the exact plan.
