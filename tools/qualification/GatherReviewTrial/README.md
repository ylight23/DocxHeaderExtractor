# Gather semantic review trial

Experimental one-request review of previously selected facts. The runner checks the prior source SHA and goal SHA, reconstructs the same source, and asks the provider to review the saved candidates. It currently accepts only runs recorded as `style-fallback`; it does not reproduce a pipeline outline.

```powershell
dotnet run --project tools/qualification/GatherReviewTrial -- goal.txt source.docx prior-output/run-result.json new-review-output
```

Requires `OPENROUTER_API_KEY` and makes a paid request with a one-attempt, one-dollar admission budget. Outputs contain source-derived text and need appropriate access controls. A rejected source quote from the prior run is counted but is not reviewed: the review cannot repair rejected proposals or establish recall. Verdicts are advisory and are not written to the target document.
