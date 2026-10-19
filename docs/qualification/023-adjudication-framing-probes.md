# 023 adjudication-framing probes

These probes isolate two framing concerns observed in the first comparative
canaries. They remain qualification-only, use the same pinned PDF/OCR/evidence
snapshot, and cannot authorize headings or hierarchy edges. Local source review
remains non-Gold.

## Boundary: swap labels, remove trajectory

`boundary-label-swap-canary` sends each predeclared P026, V17, and V24 source
comparison twice. It preserves the source and physical A/B partitions, swaps
only which physical partition receives the A label, removes all repair history
and trajectory, and counterbalances which ordering is called first. Blind case
IDs avoid telling the model that one scope oscillated or is a control. The safe
report normalizes each answer back to `ORIGINAL_A` / `ORIGINAL_B` and reports
whether the physical choice stayed stable.

Validate without provider calls:

```powershell
dotnet run --project src/DocxHeaderExtractor.V6Qualification -- boundary-label-swap-canary .dhx-evidence/eval/023-static-ab-f3454ff.manifest.json .dhx-evidence/eval/023-boundary-label-swap-canary --validate
```

Run six fresh comparisons (three scopes × two label orders):

```powershell
dotnet run --project src/DocxHeaderExtractor.V6Qualification -- boundary-label-swap-canary .dhx-evidence/eval/023-static-ab-f3454ff.manifest.json .dhx-evidence/eval/023-boundary-label-swap-canary-20261019 --live
```

The call cap is six POSTs and USD 0.50. A stable physical choice tests
label-position sensitivity; it does not prove the choice is source-correct.
The V17/V24 controls must still match their preexisting local source review
before a P026 choice is interpreted.

## Hierarchy: one child per fresh call

`hierarchy-single-child-canary` makes separate calls for P008-L018 and
P008-L024. Each request contains source evidence for page 8, one child alias,
and the same candidate-parent list. It contains no existing proposed parent,
other child, graph-level consistency field, or joint consistency requirement.
Only after both responses return does the safe report compare them offline.

Validate without provider calls:

```powershell
dotnet run --project src/DocxHeaderExtractor.V6Qualification -- hierarchy-single-child-canary .dhx-evidence/eval/023-static-ab-f3454ff.manifest.json .dhx-evidence/eval/023-hierarchy-single-child-canary --validate
```

Run the two independent calls:

```powershell
dotnet run --project src/DocxHeaderExtractor.V6Qualification -- hierarchy-single-child-canary .dhx-evidence/eval/023-static-ab-f3454ff.manifest.json .dhx-evidence/eval/023-hierarchy-single-child-canary-20261019 --live
```

The call cap is two POSTs and USD 0.50. A parent that matches local source
review for both children is a qualification observation, not Gold and not
permission to change production.
