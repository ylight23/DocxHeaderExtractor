# A99 Phase B-D

Run from the repository root:

```text
dotnet run --project src/DocxHeaderExtractor.Cli/DocxHeaderExtractor.Cli.csproj -c Release -- accuracy99 reference-campaign --root . --packet-root C:\A99-Gold\packets
dotnet run --project src/DocxHeaderExtractor.Cli/DocxHeaderExtractor.Cli.csproj -c Release -- accuracy99 early-dev-campaign --root .
dotnet run --project src/DocxHeaderExtractor.Cli/DocxHeaderExtractor.Cli.csproj -c Release -- accuracy99 review-ui --root . --reviewer-output C:\A99-Gold\reviewer\index.html
dotnet run --project src/DocxHeaderExtractor.Cli/DocxHeaderExtractor.Cli.csproj -c Release -- accuracy99 gold validate-v2 --root . --packets C:\A99-Gold\packets --gold-dir C:\A99-Gold\dev-v2
dotnet run --project src/DocxHeaderExtractor.Cli/DocxHeaderExtractor.Cli.csproj -c Release -- accuracy99 gold import-dev-v2 --root . --packets C:\A99-Gold\packets --gold-dir C:\A99-Gold\dev-v2
```

`reference-campaign` uses the frozen split and creates all source-first DEV/holdout packets.
`early-dev-campaign` then freezes a deterministic 12-20 document DEV subset by family and
occurrence-count quantiles. The v2 reviewer displays only packet content, saves progress in
browser local storage, and downloads a positive heading set plus three completeness certificates.
Body paragraphs do not require individual NO rows. The validator is fail-closed: unresolved
UNSURE blocks certification. Import accepts only selected DEV v2 gold and never reads the sealed
holdout directory.

The current repository has a complete packet campaign but no valid DEV Human Gold, so accuracy
is intentionally `NOT_MEASURED`. Do not promote `.key`, Silver, historical expected answers, or
model output into Human Gold.
