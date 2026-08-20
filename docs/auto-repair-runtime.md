# Auto Repair Runtime Policy

`dhx repair` is the code-first entry point for documents whose outline needs analysis.
It runs the current production pipeline, writes deterministic evidence, prepares an
LLM analyst prompt, and records the next validation plan.

Production must not rewrite the loaded assembly in place. A self-fix is valid only
when it creates a new sidecar version, proves it in a shadow run, and swaps runtime
to that validated version.

## Workflow

1. Run normal extraction.
2. If diagnostics are abnormal, or headings are disputed / require review, write a
   `DocumentFailureCase`.
3. Run deterministic probes and candidate strategies before asking an LLM.
4. Ask the LLM to analyze the evidence folder only. The LLM proposes a generic rule
   or rejection filter; it is not the final judge.
5. Generate a small patch in an isolated branch or worktree.
6. Build and test a shadow artifact.
7. Validate the failing file, same-layout siblings, deterministic audit corpus, and
   full unit test suite.
8. Publish beside the current version and switch process / traffic only after gates
   pass.
9. Keep rollback to the previous version.

## Required Gates

- Patch touches only relevant extractor, probe, or test files.
- No hard-coded file name, expected heading count, or old answer key dependency.
- Heading spans validate against the source text.
- Failing document passes.
- Same-layout sibling documents pass.
- Deterministic audit corpus has no blank route regression.
- `dotnet test` passes.

## Runtime Contract

Allowed:

- write failure cases and probe reports;
- generate LLM analyst prompts;
- generate candidate patches in a sidecar branch / worktree;
- build and test shadow artifacts;
- switch to a validated artifact through a supervised deployment step.

Forbidden:

- overwrite the currently loaded production DLL / EXE;
- treat an LLM visual match as the final validation;
- use old answer keys as truth when the user has rejected them;
- special-case file names, file counts, or expected heading counts.

The practical rule is: automatic diagnosis and patch proposal are allowed; automatic
production mutation is not. Production only moves after measurable validation.

## Gate Calibration Status

The repair gate is a suspicion flag, not a merge judge. Current calibration marks
the gate branch as stopped until more route-balanced keys exist:

- gate verdict may route a document to `needs_analysis`;
- gate verdict must not approve production merge by itself;
- candidate score is not used for route selection;
- fixed-rule replay is not leave-one-out validation;
- route selection should prefer deterministic route-tree signals over score.
