# Release runbook

What this pipeline promises, how to produce a release candidate, and what evidence makes a run count.
Everything here is derived from the acceptance work in M11 and M12; nothing in it is a new decision.

## What is claimed, and what is not

**Claimed.** The pipeline is release-ready with respect to the product authority contract, fail-closed
behaviour, deterministic post-validation processing, OpenRouter integration, and operational
provenance.

**Not claimed.** No heading accuracy target is established. Hierarchy resolution is not shown to
generalise. The frozen scope debts are not resolved. Not every launcher selects the newest build.

A release decision made on this runbook is a decision about *safety and operability*, not about
extraction quality.

## Producing a release candidate

### 1. Build

```
dotnet build DocxHeaderExtractor.sln -c Release
```

The build embeds the source revision into the binary automatically when it can see the repository, as
`AssemblyInformationalVersion = {Version}+{SourceRevisionId}`. A build made without repository access
carries no revision and its artifacts will report `codeRevision: null`, which makes them ineligible as
release evidence.

### 2. Know which binary you are about to run

`dhx.cmd` resolves the executable in this order: `out-vulkan`, `out-cuda`, `bin/Release`,
`bin/Debug`. **It runs the first one that exists, not the newest one.** A published GPU build that
nobody refreshed will be preferred over the build you just made — at the time of writing, `out-vulkan`
in this repository was 403 commits behind `HEAD`.

Check before an acceptance run:

```powershell
[System.Diagnostics.FileVersionInfo]::GetVersionInfo("out-vulkan\dhx.dll").ProductVersion
[System.Diagnostics.FileVersionInfo]::GetVersionInfo("src\DocxHeaderExtractor.Cli\bin\Release\net9.0\dhx.dll").ProductVersion
git rev-parse HEAD
```

Either refresh the published build, or invoke the one you mean directly:

```
dotnet run --project src/DocxHeaderExtractor.Cli -c Release -- <command>
```

This is a known limitation and is deliberately not fixed in the launcher. It costs a wasted run, and
the artifact still reports the revision that actually executed, so acceptance catches it afterwards.

### 3. Configure the provider explicitly

```
set OPENROUTER_API_KEY=...        # presence is checked; the value is never logged or written
```

**`--openrouter` is required.** Without it the CLI silently selects the Local llama.cpp backend and
succeeds. The artifact will say `backend: Local`, honestly — but the run is not OpenRouter evidence
and must not be reinterpreted as such.

```
dhx pdf-hierarchy-facts <file.docx> --openrouter --openrouter-model qwen/qwen3.5-9b --out run.json
```

## Release acceptance

A run counts as release evidence only if its artifact satisfies **all four**:

| check | field |
|---|---|
| provider is the intended one | `generation.backend == "OpenRouter"` |
| model is the intended one | `generation.model == "qwen/qwen3.5-9b"` |
| the build identifies itself | `generation.codeRevision != null` |
| the revision is the approved one | `generation.codeRevision == <release revision>` |

A run failing any of these is **not eligible as release evidence**. It is not necessarily a broken
run — a null revision means incomplete provenance, not a failed execution.

### Reading an artifact

| field | what it tells you |
|---|---|
| `rows[].sourceDocumentSha256` | the exact document revision the facts came from |
| `rows[].semanticLaneStatus` | `complete`, or `partial_timeout` if the model lane degraded. **Null means the artifact predates this field — unknown, not complete.** |
| `rows[].semanticLaneTimeouts` | the thresholds that run used, in seconds. Needed to interpret `partial_timeout`: a slow provider and a tight policy look identical without them |
| `rows[].counters.validatedHeadings` | how many facts survived validation |
| `generation.routeConfigSha256` | whether two runs used the same route configuration — **not** what that configuration was |

**Zero validated headings is not by itself a failure.** Check `semanticLaneStatus` first: an empty
successful run and a degraded run produce identical counters and are distinguished only by that field.

## Replaying without the model

The artifact carries everything the product chain needs. Re-deriving output never calls the provider
again:

```
validatedStructures + items + canonicalGroundings
  -> PdfFinalStructureProjection.Project
  -> PdfOutputDecisionPolicy.Decide
  -> PdfProductOutputSerializer.Serialize
```

This is deterministic from the validated authority boundary downward. Model inference is upstream of
that boundary and is *not* claimed to be reproducible — never re-run it to test determinism.

## Writeback

Writeback acts only on a product output's canonical anchors and refuses anything else:

- the output's `sourceDocumentSha256` must match the document being written into, or the **whole**
  operation is refused before the copy is made and nothing is written;
- a heading whose level is unresolved is skipped as `level_unresolved` rather than given one;
- a stale stable id or a span whose text no longer matches is skipped;
- the source file is never modified, and writing onto the source path is refused.

Expect skips. In the M11 canary, 21 of 24 emitted headings had no resolved level and would be skipped
— that is the contract working, not a fault.

## Failure modes and what they mean

| symptom | meaning | action |
|---|---|---|
| `backend: Local` in the artifact | `--openrouter` was omitted | re-run; do not reinterpret |
| `codeRevision: null` | build had no repository access, or an old binary | rebuild from a checkout; not release evidence |
| `semanticLaneStatus: partial_timeout` | the model lane degraded | compare against `semanticLaneTimeouts` before blaming the provider |
| `validatedHeadings: 0` with `complete` | nothing validated; an honest empty result | a route and profile outcome, not a crash |
| writeback skips everything | levels unresolved, or the fingerprint did not match | check `level_unresolved` versus a refusal before the copy |

## What is deliberately absent

No pre-run revision gate, no mandatory-provider enforcement in the CLI, no release manifest, and no
change to the launcher's selection order. Each was audited and left alone because the evidence did not
support it — see the M12 sections in `TODO.md` for the reopen triggers.
