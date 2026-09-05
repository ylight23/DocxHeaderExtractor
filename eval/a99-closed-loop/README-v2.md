# A99 Phase B-D

Run from the repository root:

```text
dotnet run --project src/DocxHeaderExtractor.Cli/DocxHeaderExtractor.Cli.csproj -c Release -- accuracy99 reference-campaign --root . --packet-root C:\A99-Gold\packets
dotnet run --project src/DocxHeaderExtractor.Cli/DocxHeaderExtractor.Cli.csproj -c Release -- accuracy99 review-ui --root . --reviewer-output C:\A99-Gold\reviewer\index.html
dotnet run --project src/DocxHeaderExtractor.Cli/DocxHeaderExtractor.Cli.csproj -c Release -- accuracy99 gold validate --root . --packets C:\A99-Gold\packets --gold-dir C:\A99-Gold\dev --out C:\A99-Gold\dev-validation.json
dotnet run --project src/DocxHeaderExtractor.Cli/DocxHeaderExtractor.Cli.csproj -c Release -- accuracy99 gold import-dev --root . --packets C:\A99-Gold\packets --gold-dir C:\A99-Gold\dev
```

`reference-campaign` uses the frozen split and creates all source-first DEV/holdout packets. The
reviewer displays only packet content and saves incremental progress in browser local storage;
its download is an `a99_human_gold` document. The validator is fail-closed. Import accepts only
DEV gold and never reads the sealed holdout directory.

The current repository has a complete packet campaign but no valid DEV Human Gold, so accuracy
is intentionally `NOT_MEASURED`. Do not promote `.key`, Silver, historical expected answers, or
model output into Human Gold.
