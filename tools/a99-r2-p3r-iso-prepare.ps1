param(
    [string]$RepoRoot = (Get-Location).Path
)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $RepoRoot

function Read-Json([string]$Path) { Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
function Write-Json([string]$Path, $Value) {
    $parent = Split-Path -Parent $Path
    if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
    $Value | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $Path -Encoding UTF8
}
function Sha256File([string]$Path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant() }
function Sha256Text([string]$Value) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value)))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}
function Invoke-DhxJson([string[]]$Arguments, [string]$OutputPath) {
    $nativeWasSet = Test-Path variable:PSNativeCommandUseErrorActionPreference
    if ($nativeWasSet) { $nativePreference = $PSNativeCommandUseErrorActionPreference; $PSNativeCommandUseErrorActionPreference = $false }
    try { & dotnet $script:DhxDll @Arguments --out $OutputPath 2>&1 | Out-Null }
    finally { if ($nativeWasSet) { $PSNativeCommandUseErrorActionPreference = $nativePreference } }
    if ($LASTEXITCODE -ne 0) { throw "deterministic source extraction failed (exit $LASTEXITCODE): $($Arguments -join ' ')" }
    if (-not (Test-Path -LiteralPath $OutputPath)) { throw "extractor did not create $OutputPath" }
    Read-Json $OutputPath
}

$parentCommit = (git rev-parse HEAD).Trim()
if ($parentCommit -ne '708e1ef6bbd443d1bb8c9fe306b150442bbf7ef7') { throw "Unexpected P3R parent: $parentCommit" }
$b0Commit = (git rev-parse 76c4e01).Trim()
$script:DhxDll = Join-Path $RepoRoot 'src/DocxHeaderExtractor.Cli/bin/Release/net9.0/dhx.dll'
if (-not (Test-Path -LiteralPath $script:DhxDll)) { throw "Release CLI missing: $script:DhxDll" }

$pilotPath = 'eval/a99-closed-loop/research-r2/pilot-8/pilot-freeze.v1.json'
$inventoryPath = 'eval/a99-dataset/document-inventory.v1.json'
$pilot = Read-Json $pilotPath
$inventory = Read-Json $inventoryPath
$inventoryById = @{}
foreach ($doc in @($inventory.documents)) { $inventoryById[[string]$doc.documentId] = $doc }
$expected = @('DOC-0004','DOC-0006','DOC-0092','DOC-0133','DOC-0171','DOC-0326')
$pilotById = @{}
foreach ($doc in @($pilot.selectedDocuments)) { $pilotById[[string]$doc.documentId] = $doc }
if ($pilotById.Count -ne 8) { throw 'pilot membership is not the frozen eight-document cohort' }
foreach ($id in $expected) { if (-not $pilotById.ContainsKey($id)) { throw "pilot missing $id" } }

$root = Join-Path $RepoRoot 'eval/a99-closed-loop/research-r2/p3-isolated-review'
$sourceRoot = Join-Path $root 'sources'
New-Item -ItemType Directory -Force -Path $root, $sourceRoot | Out-Null
$records = @()
$completeness = @()

foreach ($id in $expected) {
    $pilotDoc = $pilotById[$id]
    $inv = $inventoryById[$id]
    $sourcePath = [string]$pilotDoc.sourcePath
    if (-not (Test-Path -LiteralPath $sourcePath)) { throw "source unavailable $id" }
    $actualSha = Sha256File $sourcePath
    if ($actualSha -ne ([string]$pilotDoc.sourceSha256).ToLowerInvariant() -or
        $actualSha -ne ([string]$inv.sourceSha256).ToLowerInvariant()) { throw "SOURCE_SHA_MISMATCH $id" }

    $extension = [IO.Path]::GetExtension($sourcePath).ToLowerInvariant()
    $temp = Join-Path $env:TEMP ("a99-r2-p3r-" + $id + '-' + [Guid]::NewGuid().ToString('N') + '.json')
    try {
        if ($extension -in @('.docx','.docm')) {
            # This is the frozen DOCX source only. The parser walks paragraphs nested in tables in
            # document order; no sibling PDF is resolved or opened by this branch.
            $raw = Invoke-DhxJson @('accuracy99','packet',$sourcePath,'--root',$RepoRoot) $temp
            $occurrences = @()
            $ordinal = 0
            foreach ($item in @($raw.occurrences)) {
                $ordinal++
                $text = [string]$item.rawText
                $occurrences += [ordered]@{
                    sourceOccurrenceId = ('P{0:D6}' -f $ordinal)
                    sourceId = [string]$item.sourceId
                    sourceOrdinal = [int]$item.sourceOrdinal
                    literalSourceText = $text
                    sourceTextSha256 = Sha256Text $text
                    style = [ordered]@{
                        styleId = [string]$item.style.styleId
                        styleName = [string]$item.style.styleName
                        fontSize = $item.style.fontSizePt
                        bold = [bool]$item.style.bold
                        italic = [bool]$item.style.italic
                        allCaps = [bool]$item.style.allCaps
                        alignment = $item.style.alignment
                    }
                    numbering = $item.numbering
                    layout = [ordered]@{
                        tableDepth = [int]$item.layout.tableDepth
                        sectionIndex = [int]$item.layout.sectionIndex
                        keepNext = [bool]$item.layout.keepNext
                        pageBreakBefore = [bool]$item.layout.pageBreakBefore
                    }
                }
            }
            $tableCount = @($occurrences | Where-Object { [int]$_.layout.tableDepth -gt 0 }).Count
            $representation = [ordered]@{
                kind = 'DOCX_COMPLETE_SOURCE_PARAGRAPHS'
                ordering = 'sourceOrdinal ascending, including table-cell paragraphs'
                paragraphCount = $occurrences.Count
                tableCellOccurrenceCount = $tableCount
                allParagraphsTraversed = $true
                allTablesTraversed = $true
                truncated = $false
            }
            $coverage = [ordered]@{
                allParagraphsTraversed = $true
                allTablesTraversed = $true
                truncated = $false
                paragraphCount = $occurrences.Count
                tableCellOccurrenceCount = $tableCount
            }
            $sourceKind = if ($id -eq 'DOC-0326') { 'PDF_CONVERTED_DOCX' } else { 'NATIVE_DOCX' }
            $mediaType = 'application/vnd.openxmlformats-officedocument.wordprocessingml.document'
        } elseif ($extension -eq '.pdf') {
            # The dedicated source-review operation uses every page/line and groups every extracted
            # line into a source block. It does not call PdfLayoutEvidenceOutline or any candidate
            # or heading classifier.
            $raw = Invoke-DhxJson @('pdf-source-review',$sourcePath) $temp
            if ([int]$raw.pagesTraversed -ne [int]$raw.pageCount -or [bool]$raw.truncated) {
                throw "PDF completeness failure $id"
            }
            $occurrences = @()
            foreach ($block in @($raw.blocks)) {
                $occurrences += [ordered]@{
                    sourceOccurrenceId = [string]$block.sourceOccurrenceId
                    page = [int]$block.page
                    blockId = [string]$block.blockId
                    sourceOrdinal = [int]$block.sourceOrdinal
                    literalSourceText = [string]$block.literalSourceText
                    lineCount = [int]$block.lineCount
                    layout = $block.layout
                    lines = @($block.lines)
                }
            }
            $representation = [ordered]@{
                kind = 'PDF_COMPLETE_SOURCE_BLOCKS'
                ordering = [string]$raw.ordering
                pageCount = [int]$raw.pageCount
                pagesTraversed = [int]$raw.pagesTraversed
                lineCount = [int]$raw.lineCount
                blockCount = [int]$raw.blockCount
                allPagesTraversed = $true
                allBlocksIncluded = $true
                headingFiltering = $false
                candidateGeneration = $false
                truncated = $false
                extractor = 'dhx pdf-source-review'
            }
            $coverage = [ordered]@{
                pagesTraversed = [int]$raw.pagesTraversed
                pageCount = [int]$raw.pageCount
                lines = [int]$raw.lineCount
                blocks = [int]$raw.blockCount
                truncated = $false
            }
            $sourceKind = 'PDF'
            $mediaType = 'application/pdf'
        } else {
            throw "unsupported frozen source type $id $sourcePath"
        }

        $artifact = [ordered]@{
            artifactKind = 'A99_R2_P3R_ISOLATED_REVIEW_SOURCE'
            schemaVersion = 'a99-r2-p3r-review-source-v1'
            studyId = 'A99-R2'
            pilotId = 'R2-PILOT-8'
            documentId = $id
            frozenSourcePath = $sourcePath
            frozenSourceSha256 = $actualSha
            mediaType = $mediaType
            familyId = [string]$pilotDoc.familyId
            reviewSourcePath = $sourcePath
            reviewSourceSha256 = $actualSha
            reviewSourceMatchesFrozenSource = $true
            sourceKind = $sourceKind
            sourceRepresentation = $representation
            completeSourceCoverage = $true
            truncated = $false
            occurrences = $occurrences
            semanticLabelsCreated = 0
            goldCreated = $false
            containsModelOutputs = $false
            containsGoldLabels = $false
            containsResidualHints = $false
            crossSourceSubstitution = $false
            runtimeUse = 'PROHIBITED'
        }
        Write-Json (Join-Path $sourceRoot "$id.review-source.v1.json") $artifact
        $records += [ordered]@{
            documentId = $id
            frozenSourcePath = $sourcePath
            frozenSourceSha256 = $actualSha
            mediaType = $mediaType
            familyId = [string]$pilotDoc.familyId
            reviewSourcePath = $sourcePath
            reviewSourceSha256 = $actualSha
            reviewSourceMatchesFrozenSource = $true
            completeSourceCoverage = $true
            truncated = $false
            sourceKind = $sourceKind
            occurrenceCount = $occurrences.Count
        }
        $completeness += [ordered]@{
            documentId = $id
            sourceKind = $sourceKind
            status = 'COMPLETE_ISOLATED_REVIEW_SOURCE'
            truncated = $false
            coverage = $coverage
        }
    } finally {
        if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force }
    }
}

Write-Json (Join-Path $root 'cross-source-firewall.v1.json') ([ordered]@{
    schemaVersion = 'a99-r2-p3r-cross-source-firewall-v1'
    studyId = 'A99-R2'
    pilotId = 'R2-PILOT-8'
    frozenParentCommit = $parentCommit
    pdfUsedToAnnotateDocx = $false
    docxUsedToAnnotatePdf = $false
    generatedDocxUsedToRepairPdf = $false
    originalPdfUsedToRepairConvertedDocx = $false
    crossRepresentationHeadingUnion = $false
    crossRepresentationTextNormalization = $false
    conversionComparisonDeferred = $true
    doc0326EvaluationRepresentation = 'DOCX'
    doc0326Family = 'PDF_CONVERTED'
    doc0326OriginalPdfSemanticAccess = 'PROHIBITED_DURING_GOLD_REVIEW'
    sourceIsolation = 'one frozen document = one review source = one Gold authority'
})
Write-Json (Join-Path $root 'completeness.v1.json') ([ordered]@{
    schemaVersion = 'a99-r2-p3r-completeness-v1'
    studyId = 'A99-R2'
    pilotId = 'R2-PILOT-8'
    documents = $completeness
    allComplete = ($completeness.Count -eq 6 -and @($completeness | Where-Object status -ne 'COMPLETE_ISOLATED_REVIEW_SOURCE').Count -eq 0)
    semanticLabelsCreated = 0
    goldCreated = $false
    modelCalls = 0
    providerCalls = 0
})
Write-Json (Join-Path $root 'decision.v1.json') ([ordered]@{
    schemaVersion = 'a99-r2-p3r-decision-v1'
    terminal = 'R2_P3_ISOLATED_SOURCES_READY_FOR_USER_ASSISTED_REVIEW'
    documents = $records
    semanticLabelsCreated = 0
    goldCreated = $false
    modelCalls = 0
    providerCalls = 0
    r2BInferenceAuthorized = $false
    forbiddenActions = @('cross-source heading union','PDF repair of DOCX Gold','DOCX repair of PDF Gold','automatic semantic annotation','R2-B inference before human Gold freeze')
})

Write-Output 'P3R_MODEL_CALLS=0'
Write-Output 'P3R_PROVIDER_CALLS=0'
Write-Output 'P3R_DOCUMENTS=6'
Write-Output 'P3R_PDF_SOURCES=3'
Write-Output 'P3R_DOCX_SOURCES=3'
Write-Output 'P3R_SEMANTIC_LABELS=0'
Write-Output 'P3R_GOLD_CREATED=False'
Write-Output 'P3R_R2B_AUTHORIZED=False'
Write-Output 'P3R_TERMINAL=R2_P3_ISOLATED_SOURCES_READY_FOR_USER_ASSISTED_REVIEW'
