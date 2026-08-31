# Docling Retire Audit

Revision: `40a4c915510c034bcb9e9c57419a6e7687892660`

The normal authority path does not require `DoclingLayoutOutline`. Its use is
behind the explicit `DoclingSidecarFallback`/`DoclingJsonPath` opt-in and is
not part of the native DOCX or PDF authority route.

The symbol is not yet removable. The CLI still exposes `--docling-json`, and
`DoclingLayoutOutlineTests.PipelineCanUseExplicitDoclingJsonAsDeterministicRoute`
locks that explicit evaluation route. These are compatibility/evaluation
callers, not normal authority callers.

```text
NORMAL_PRODUCTION_CALLERS       = 0
SUPPORTED_EXPLICIT_EVAL_CALLERS = 2
DOCLING_REMOVE_CANDIDATE        = false
```

Physical removal requires first retiring the explicit CLI/evaluation contract,
then deleting the test and the fallback branch together. No native overload or
new authority dependency should be added.
