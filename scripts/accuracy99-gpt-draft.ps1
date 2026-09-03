[CmdletBinding()]
param(
    [string] $InputPacket = "eval\accuracy99\adjudication\development\010.review.jsonl",
    [string] $Output = "eval\accuracy99\adjudication\work\010.gpt-draft.jsonl",
    [string] $Model = $(if ($env:OPENROUTER_MODEL) { $env:OPENROUTER_MODEL } else { "qwen/qwen3.5-9b" }),
    [ValidateRange(4, 24)] [int] $BatchSize = 12
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$apiKey = $env:OPENROUTER_API_KEY
if ([string]::IsNullOrWhiteSpace($apiKey)) {
    throw "OPENROUTER_API_KEY is required for GPT draft adjudication."
}

$inputPath = [IO.Path]::GetFullPath($InputPacket)
$outputPath = [IO.Path]::GetFullPath($Output)
$outputDirectory = [IO.Path]::GetDirectoryName($outputPath)
if ($outputDirectory) { New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null }

$lines = @(Get-Content -LiteralPath $inputPath -Encoding UTF8 | Where-Object { $_.Trim() })
if ($lines.Count -lt 2) { throw "The review packet is empty or has no occurrences: $inputPath" }
$manifest = $lines[0] | ConvertFrom-Json
$occurrences = @($lines | Select-Object -Skip 1 | ForEach-Object { $_ | ConvertFrom-Json })
if ($manifest.recordType -ne "manifest" -or $manifest.predictionsIncluded) {
    throw "The input is not a source-first Accuracy-99 packet."
}
if ($occurrences.Count -ne [int]$manifest.catalogOccurrenceCount) {
    throw "Occurrence count does not match the packet manifest."
}

$systemPrompt = @"
You are producing an AI-ADJUDICATION DRAFT for an Accuracy-99 source-first review.
This is not human gold and must never be described as frozen or final.
Use only the supplied parser-owned source text and metadata. Do not use production predictions,
model confidence, historical provenance as a label, or external knowledge.

For every supplied sourceId return exactly one item. Return only JSON:
{"items":[{"sourceId":"...","label":"HEADING|NON_HEADING|UNCERTAIN|EXCLUDED",
"headingText":"exact contiguous substring copied from rawSourceText or null",
"structuralType":"Title|Subtitle|Heading|ListItem|Caption|TableTitle|FigureTitle|Figure|Table or null",
"level":1,"parentSourceId":"earlier heading sourceId or null","reason":"short reason"}]}

Rules:
- label is your draft suggestion, not a human decision;
- choose HEADING only when the source itself is an outline/title occurrence;
- headingText must be copied verbatim from rawSourceText, never rewritten;
- use null headingText for non-heading/uncertain/excluded;
- level may be null when unclear; parentSourceId may be null when unclear;
- do not omit an item, invent sourceIds, add markdown, or return offsets;
- reason must be brief and observational, not chain-of-thought.
"@

$sourceById = @{}
foreach ($row in $occurrences) { $sourceById[[string]$row.sourceId] = $row }
$drafts = [Collections.Generic.List[object]]::new()
$requestErrors = [Collections.Generic.List[object]]::new()
$validLabels = @("HEADING", "NON_HEADING", "UNCERTAIN", "EXCLUDED")
$validStructuralTypes = @("Title", "Subtitle", "Heading", "ListItem", "Caption", "TableTitle", "FigureTitle", "Figure", "Table")
$batchCount = [Math]::Ceiling($occurrences.Count / [double]$BatchSize)

for ($offset = 0; $offset -lt $occurrences.Count; $offset += $BatchSize) {
    $end = [Math]::Min($offset + $BatchSize - 1, $occurrences.Count - 1)
    $batch = @($occurrences[$offset..$end])
    $batchIds = @($batch | ForEach-Object { [string]$_.sourceId })
    $evidence = @($batch | ForEach-Object {
        [ordered]@{
            sourceId = $_.sourceId
            sourceOrdinal = $_.sourceOrdinal
            rawSourceText = $_.rawSourceText
            previousSourceText = $_.previousSourceText
            nextSourceText = $_.nextSourceText
            parserMetadata = if ($_.PSObject.Properties.Name -contains "parserMetadata") { $_.parserMetadata } else { $null }
        }
    })
    $userPrompt = $systemPrompt + "`n`nSOURCE ITEMS:`n" + ($evidence | ConvertTo-Json -Depth 12 -Compress)
    $requestBody = [ordered]@{
        model = $Model
        temperature = 0
        max_tokens = [Math]::Min(2400, 256 + ($batch.Count * 150))
        reasoning = [ordered]@{ effort = "none" }
        messages = @(
            @{ role = "system"; content = $systemPrompt },
            @{ role = "user"; content = $userPrompt }
        )
        response_format = @{ type = "json_object" }
        provider = @{ zdr = $true; data_collection = "deny"; require_parameters = $true; allow_fallbacks = $true }
    }

    Write-Host ("BATCH {0}/{1} · source ordinals {2}..{3}" -f ([int]($offset / $BatchSize) + 1), $batchCount, $batch[0].sourceOrdinal, $batch[-1].sourceOrdinal)
    $returned = @{}
    try {
        $response = Invoke-RestMethod `
            -Method Post `
            -Uri "https://openrouter.ai/api/v1/chat/completions" `
            -Headers @{ Authorization = "Bearer $apiKey"; "X-Title" = "DocxHeaderExtractor Accuracy-99 GPT Draft" } `
            -ContentType "application/json" `
            -Body ($requestBody | ConvertTo-Json -Depth 16 -Compress) `
            -TimeoutSec 300
        $content = [string]$response.choices[0].message.content
        $parsed = $content | ConvertFrom-Json
        foreach ($item in @($parsed.items)) {
            if ($null -eq $item -or [string]::IsNullOrWhiteSpace([string]$item.sourceId)) { continue }
            $id = [string]$item.sourceId
            if ($batchIds -notcontains $id -or $returned.ContainsKey($id)) { continue }
            $returned[$id] = $item
        }
    }
    catch {
        $requestErrors.Add([ordered]@{
            batchIndex = [int]($offset / $BatchSize)
            sourceIds = $batchIds
            errorType = $_.Exception.GetType().FullName
            error = $_.Exception.Message.Substring(0, [Math]::Min(500, $_.Exception.Message.Length))
        })
    }

    foreach ($row in $batch) {
        $id = [string]$row.sourceId
        $item = if ($returned.ContainsKey($id)) { $returned[$id] } else { $null }
        $label = if ($item -and $validLabels -contains ([string]$item.label).ToUpperInvariant()) { ([string]$item.label).ToUpperInvariant() } else { $null }
        $headingText = if ($item -and $null -ne $item.headingText) { [string]$item.headingText } else { $null }
        $spanStart = $null
        $spanEnd = $null
        $spanStatus = "NOT_APPLICABLE"
        if ($label -eq "HEADING") {
            $spanStatus = "UNRESOLVED"
            if (-not [string]::IsNullOrEmpty($headingText)) {
                $spanStart = $row.rawSourceText.IndexOf($headingText, [StringComparison]::Ordinal)
                if ($spanStart -ge 0) {
                    $spanEnd = $spanStart + $headingText.Length
                    $spanStatus = "EXACT_SOURCE_SUBSTRING"
                } else {
                    $spanStart = $null
                }
            }
        }
        $structuralType = if ($item -and $validStructuralTypes -contains [string]$item.structuralType) { [string]$item.structuralType } else { $null }
        $level = $null
        if ($item -and $null -ne $item.level) {
            $candidateLevel = 0
            if ([int]::TryParse([string]$item.level, [ref]$candidateLevel) -and $candidateLevel -ge 1 -and $candidateLevel -le 9) { $level = $candidateLevel }
        }
        $parentSourceId = if ($item -and $item.parentSourceId -and $sourceById.ContainsKey([string]$item.parentSourceId)) { [string]$item.parentSourceId } else { $null }
        $status = if ($item) { "DRAFT_SUGGESTION" } else { "UNRESOLVED_REQUEST" }
        $reason = if ($item -and $item.reason) { ([string]$item.reason).Trim() } else { "No provider decision returned; human review required." }
        if ($reason.Length -gt 240) { $reason = $reason.Substring(0, 240) }
        $drafts.Add([ordered]@{
            recordType = "gpt-draft-occurrence"
            datasetId = $manifest.datasetId
            sourceId = $id
            sourceOrdinal = $row.sourceOrdinal
            draftLabel = $label
            draftHeadingText = $headingText
            draftHeadingSpan = if ($null -ne $spanStart) { [ordered]@{ start = $spanStart; end = $spanEnd } } else { $null }
            spanResolution = $spanStatus
            draftStructuralType = $structuralType
            draftLevel = $level
            draftParentSourceId = $parentSourceId
            draftReason = $reason
            status = $status
            sourceCatalogHash = $manifest.sourceCatalogHash
        })
    }
}

$draftManifest = [ordered]@{
    recordType = "gpt-draft-manifest"
    artifactKind = "accuracy99_gpt_adjudication_draft"
    schemaVersion = 1
    datasetId = $manifest.datasetId
    documentId = $manifest.documentId
    sourceCatalogHash = $manifest.sourceCatalogHash
    sourceCatalogVersion = $manifest.sourceCatalogVersion
    catalogOccurrenceCount = $occurrences.Count
    model = $Model
    batchSize = $BatchSize
    provider = "OpenRouter"
    predictionsIncluded = $false
    humanGold = $false
    reviewStatus = "GPT_DRAFT_ONLY"
    requestCount = $batchCount
    requestErrorCount = $requestErrors.Count
    generatedAt = [DateTimeOffset]::UtcNow.ToString("O")
}
$outputLines = [Collections.Generic.List[string]]::new()
$outputLines.Add(($draftManifest | ConvertTo-Json -Depth 12 -Compress))
foreach ($draft in $drafts) { $outputLines.Add(($draft | ConvertTo-Json -Depth 12 -Compress)) }
$tempPath = $outputPath + ".partial"
$outputLines | Set-Content -LiteralPath $tempPath -Encoding UTF8
Move-Item -LiteralPath $tempPath -Destination $outputPath -Force

Write-Host ("GPT_DRAFT_DONE · occurrences={0} · requestErrors={1} · output={2}" -f $drafts.Count, $requestErrors.Count, $outputPath)
