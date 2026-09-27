# Semantic decision contract: redesign proposal

**Status: proposal only. Nothing here is implemented, and no provider call was made to produce it.** Phase C of the follow-up to the reachability audit of 2026-09-27.

## The question

Should canonical heading membership be represented by **one coherent semantic decision** instead of two independent truths (`isHeading` and `semanticRole`)?

## The evidence it rests on

| Source | Finding |
|---|---|
| Phase A (`model-visible-provenance-v1`) | 37,015 model-visible strings, 0 unclassified. No derived prior reaches the model. The input side is clean, so the defect is not in what the model is told. |
| Phase B (`semantic-authority-consistency-v1`) | 16 of 16 constructed contradictions are accepted as valid by the production validator and decoder. Nothing detects a proposal that contradicts itself. |
| Phase B, committed runs | `isHeading` was **true on every entry of both runs** (300 and 301). It carried no information at all. |
| Phase B, committed runs | Every occurrence the model itself named page furniture, a note, an index entry, a caption, a table header or a signature line was **also claimed as a heading**: 56 in the baseline, 53 in the arm. |
| The V3 arm (`8ada89f`, `6a05502`) | Telling the model the exclusion in prose moved which label it wrote, not which occurrences it claimed. SRC-089's contradictions fell 4 → 0 while its false claims rose 10 → 28. |

Two things follow, and they point the same way.

1. **The boolean is the redundant axis.** It never varied. Membership was decided in practice by whether the model put an entry in the array.
2. **The role is the informative axis.** It varied across 18 values in the baseline and 16 in the arm, and it was usually *right* — the model named running headers, footnotes, index entries and signature lines correctly while claiming them as headings.

The pipeline throws the informative axis away: no layer reads `semanticRole`. The binder validates coordinates, the identity resolver merges by model-provided keys, the hierarchy resolver reads relations. Meaning reaches the artifacts and the reviewer, and nothing else.

## Why "add an invariant" is not enough

The obvious repair is to have the validator refuse `isHeading: true` beside an excluded role. The V3 arm already showed how that fails: `semanticRole` is an unconstrained string, so a rule stated over role names is satisfied by writing a different name. In the arm, four SRC-089 contradictions disappeared with **no claim withdrawn** — the same occurrences came back as a generic `heading`. A check over a free-text field measures the field, not the decision.

So any invariant needs a closed vocabulary first. And once the vocabulary is closed and membership is derivable from it, the boolean has nothing left to say.

## The proposal

**One required decision field, from a closed vocabulary, naming the occurrence's primary function. Membership becomes a harness projection of that function, not a second thing the model asserts.**

```
function: <closed vocabulary>     required, exactly one value
note:     <free text>             optional, never read as authority
```

- The harness derives membership: a function in the structural set is a heading; one in the non-structural set is not. The derivation lives with the contract, is versioned with it, and is testable without a provider.
- `isHeading` disappears from the request. It cannot contradict the function, because it no longer exists.
- The contradiction becomes **unrepresentable** rather than merely rarer. That is the property the V3 arm failed to buy with prose.

The vocabulary has to carry the distinctions the corpus already forced, and the occurrence ontology (`OCCURRENCE_SEMANTIC_AXES_V3`) already names them, with `C = 0` across the whole deterministic study — no held-out residual since V1.1 needed a new semantic category. That is the strongest available evidence that a closed set is feasible here.

Two requirements the evidence makes non-negotiable:

- **A region opener stays a heading wherever it sits.** The user's S095_Q2 decision (index group letters) and the TOC opener are headings inside navigation regions. The vocabulary must separate "an ordinary entry of a navigation region" from "the opener of one", or it will re-create the error it is meant to remove.
- **An unknown function must be sayable.** One explicit `other` member with a mandatory `note`, projecting to *not a heading*, so an unfamiliar document degrades to a miss rather than to a silent false claim. `other` must never project to membership.

## What this does not fix

- **Hierarchy** stays a separate transaction and stays unmeasured.
- **Claim extent** — SRC-089's wrapped titles, the footnote mark in the decree title — is a source-parts question, untouched by this.
- **The model can still pick the wrong member** of a closed vocabulary. The failure moves from "contradicts itself" to "chooses wrong", which is at least a failure the scorer can see and a reviewer can adjudicate.

## Migration constraints

- V2 is frozen and stays the production default. This would be a new request version, and every existing artifact keeps replaying under the version it was sent in.
- `semanticRole` is read today by reviewers and by the scorer's axis comparison, so the new field must supply that, not remove it.
- The three `LIVE_SUSPICIOUS` producers are unaffected: Phase A proves they are not model-visible.

## How it would be tested, and what to do first

Testing the redesign needs one authorized arm against the pilot baseline, with the same cohort, model and caps, and success criteria stricter than the last arm's:

1. precision up substantially;
2. recall held;
3. SRC-089 assembly not regressed (23/26 articles, 5/5 chapters);
4. the contradiction **unrepresentable**, shown by construction as in Phase B — not merely counted lower.

**But the cheaper experiment should come first, and it needs no provider call at all.** The responses of both arms are committed in this repo, with every `semanticRole` the model wrote. A deterministic post-filter over them — dropping claims whose role is an ordinary navigation, furniture, note, caption or table-header function, keeping region openers — measures the ceiling of this idea offline, on data already paid for. If that filter does not recover SRC-095's precision while holding SRC-089, the representation is not the whole defect and the redesign should not be built yet.

## Recommendation

Adopt the single-decision representation **after** the offline post-filter measurement, not before. The measurement is free, uses committed data, and its result decides whether the contract change is worth a new request version.
