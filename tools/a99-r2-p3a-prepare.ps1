param(
    [string]$RepoRoot = (Get-Location).Path
)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $RepoRoot

function Read-Json([string]$Path) {
    Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Write-Json([string]$Path, $Value) {
    $parent = Split-Path -Parent $Path
    if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
    $Value | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Sha256File([string]$Path) {
    (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
}

function Sha256Text([string]$Value) {
    $hash = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value)
        return ([System.BitConverter]::ToString($hash.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    } finally { $hash.Dispose() }
}

function RelativePath([string]$Path) {
    [IO.Path]::GetRelativePath($RepoRoot, [IO.Path]::GetFullPath($Path)).Replace('/', '\')
}

function Invoke-DhxJson([string[]]$Arguments, [string]$OutputPath) {
    $nativePreferenceWasSet = Test-Path variable:PSNativeCommandUseErrorActionPreference
    if ($nativePreferenceWasSet) { $nativePreference = $PSNativeCommandUseErrorActionPreference; $PSNativeCommandUseErrorActionPreference = $false }
    try { & dotnet $script:DhxDll @Arguments --out $OutputPath 2>&1 | Out-Null }
    finally { if ($nativePreferenceWasSet) { $PSNativeCommandUseErrorActionPreference = $nativePreference } }
    if ($LASTEXITCODE -ne 0) { throw "Deterministic source extraction failed (exit $LASTEXITCODE): $($Arguments -join ' ')" }
    if (-not (Test-Path -LiteralPath $OutputPath)) { throw "Extractor did not create $OutputPath" }
    Read-Json $OutputPath
}

function Assert-NoForbiddenContent($Value, [string]$Context) {
    $forbidden = @('candidate','candidates','prediction','predictions','modelOutput','goldLabel','residuals','prompt','score')
    if ($Value -is [System.Collections.IDictionary]) {
        foreach ($key in $Value.Keys) {
            if ($forbidden -contains ([string]$key)) {
                throw "SOURCE_PACKET_FORBIDDEN_PROPERTY $Context.$key"
            }
            Assert-NoForbiddenContent $Value[$key] "$Context.$key"
        }
    } elseif ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]) {
        foreach ($item in $Value) { Assert-NoForbiddenContent $item "$Context[]" }
    } elseif ($null -ne $Value -and $Value -isnot [string] -and $Value.PSObject.Properties.Count -gt 0) {
        foreach ($property in $Value.PSObject.Properties) {
            if ($forbidden -contains $property.Name) {
                throw "SOURCE_PACKET_FORBIDDEN_PROPERTY $Context.$($property.Name)"
            }
            Assert-NoForbiddenContent $property.Value "$Context.$($property.Name)"
        }
    }
}

$root = Join-Path $RepoRoot 'eval/a99-closed-loop/research-r2/p3-human-gold'
$packetRoot = Join-Path $root 'packets'
$aRoot = Join-Path $root 'annotations/A'
$bRoot = Join-Path $root 'annotations/B'
$sixIds = @('DOC-0004','DOC-0006','DOC-0092','DOC-0133','DOC-0171','DOC-0326')
$pilotIds = @('DOC-0004','DOC-0006','DOC-0092','DOC-0133','DOC-0158','DOC-0165','DOC-0171','DOC-0326')
$p2Ids = @('DOC-0158','DOC-0165')
$b0Commit = (git rev-parse 76c4e01).Trim()
$parentCommit = (git rev-parse HEAD).Trim()
if ($parentCommit -ne 'cd41eb55fe582d91d31cc680f1186b2db0e8c844') {
    throw "Unexpected P3A parent: $parentCommit"
}
$script:DhxDll = Join-Path $RepoRoot 'src/DocxHeaderExtractor.Cli/bin/Release/net9.0/dhx.dll'
if (-not (Test-Path -LiteralPath $script:DhxDll)) { throw "Release CLI missing: $script:DhxDll" }

$pilotFreeze = Read-Json 'eval/a99-closed-loop/research-r2/pilot-8/pilot-freeze.v1.json'
$inventory = Read-Json 'eval/a99-dataset/document-inventory.v1.json'
$inventoryById = @{}
foreach ($doc in @($inventory.documents)) { $inventoryById[[string]$doc.documentId] = $doc }
$pilotById = @{}
foreach ($doc in @($pilotFreeze.selectedDocuments)) { $pilotById[[string]$doc.documentId] = $doc }
if ($pilotById.Count -ne 8 -or (@($pilotIds | Where-Object { -not $pilotById.ContainsKey($_) })).Count -gt 0) {
    throw 'Frozen pilot membership does not match the registered eight documents.'
}

$p2FreezePath = 'eval/a99-closed-loop/research-r2/p2-occurrence-closure/strict-occurrence-freeze.v1.json'
$p2Freeze = Read-Json $p2FreezePath
$p2FreezeSha = Sha256File $p2FreezePath
$p2GoldById = @{}
foreach ($row in @($p2Freeze.documents)) {
    $id = [string]$row.documentId
    $path = "eval/a99-closed-loop/research-r2/p2-occurrence-closure/strict-occurrence/$id.strict-occurrence-gold.v1.json"
    if (-not (Test-Path -LiteralPath $path)) { throw "P2B strict occurrence file missing: $path" }
    $gold = Read-Json $path
    $actualSha = Sha256File $path
    if ([int]$row.semanticCount -ne [int]$gold.semanticHeadingCount -or
        [int]$row.occurrenceResolved -ne [int]$gold.occurrenceResolved) {
        throw "P2B count mismatch for $id"
    }
    $p2GoldById[$id] = [ordered]@{
        path = $path
        sha256 = $actualSha
        sourceSha256 = [string]$gold.sourceSha256
        semanticHeadingCount = [int]$gold.semanticHeadingCount
        occurrenceResolvedCount = [int]$gold.occurrenceResolved
        strictOccurrenceFrozen = $true
    }
}
if ($p2GoldById.Count -ne 2 -or (@($p2Ids | Where-Object { -not $p2GoldById.ContainsKey($_) })).Count -gt 0) {
    throw 'P2B does not contain exactly the two expected ready pilot documents.'
}

# Fail closed if the inventory already certifies an independent authority for a missing document.
foreach ($id in $sixIds) {
    if (-not $inventoryById.ContainsKey($id)) { throw "Inventory missing $id" }
    $row = $inventoryById[$id]
    $independent = ([string]$row.independenceStatus) -match 'INDEPENDENT|R2'
    $authority = [bool]$row.semanticStrictGold -or [bool]$row.exhaustiveStrictGold -or
        [bool]$row.occurrenceGold -or [bool]$row.occurrenceEvaluable -or $independent
    if ($authority) {
        throw "EXISTING_INDEPENDENT_AUTHORITY_FAIL_CLOSED $id"
    }
}

New-Item -ItemType Directory -Force -Path $root, $packetRoot, $aRoot, $bRoot | Out-Null

$scope = @()
foreach ($id in $pilotIds) {
    $pilot = $pilotById[$id]
    $inv = $inventoryById[$id]
    $sourcePath = [string]$pilot.sourcePath
    if (-not (Test-Path -LiteralPath $sourcePath)) { throw "Source unavailable: $id $sourcePath" }
    $actualSha = Sha256File $sourcePath
    if ($actualSha -ne ([string]$pilot.sourceSha256).ToLowerInvariant() -or
        $actualSha -ne ([string]$inv.sourceSha256).ToLowerInvariant()) {
        throw "SOURCE_SHA_MISMATCH $id"
    }
    $scope += [ordered]@{
        documentId = $id
        sourcePath = $sourcePath
        sourceSha256 = $actualSha
        sourceHashVerified = $true
        documentGroupId = [string]$inv.documentGroupId
        fileType = ([IO.Path]::GetExtension($sourcePath)).TrimStart('.').ToUpperInvariant()
        familyId = [string]$pilot.familyId
        includedForHumanAnnotation = ($sixIds -contains $id)
        p2bFrozenAuthority = if ($p2GoldById.ContainsKey($id)) { $p2GoldById[$id].path } else { $null }
    }
}

$annotationInstructions = @'
# A99-R2-P3A independent human heading annotation

This package prepares source-only material for Human A and Human B. It does not contain model
predictions, residual reports, scores, or another annotator's answers. Review the complete original
document independently; do not annotate from a shortlist or from formatting alone.

## Scope

Only the six documents named in this package are awaiting annotation. DOC-0158 and DOC-0165 already
have frozen P2B authority and must not be annotated again. The reserve cohort is sealed.

For every heading that the complete-document review identifies, add one row in document order. Decide
semantic membership from the original document. Bold text, large text, standalone blocks, numbering,
and table placement may be evidence but are neither automatic acceptance nor automatic rejection.

## Row fields

`documentTitleText` is the heading text as it appears in the original document. Set
`semanticMembership` to `HEADING`, select the most appropriate `semanticFamily`, and use
`optionalRole` only when independently clear. `sourceOccurrenceHint` may identify a packet occurrence
but is not required. Keep uncertainty explicit with `confidence=CERTAIN` or `REVIEW_REQUIRED`.

Do not manually count UTF-16 offsets. Later deterministic tooling will preserve the separate
documentTitleText and source-representation binding text. If the source representation differs from
the visual/original title, record the distinction in reviewNote and leave occurrence resolution for
the human occurrence-review step.

## Independence and adjudication

Human A and Human B must work independently and must not inspect the other pass, B0/A99 artifacts,
known residual strings, R2-B output, or reserve documents. After both passes are frozen, tooling may
compare exact text and occurrence hints, but it may not choose a pass, normalize disagreements, or
silently union rows. Every semantic disagreement requires human adjudication.

These files are offline research Gold preparation only and are prohibited from runtime use.
'@
Set-Content -LiteralPath (Join-Path $root 'annotation-instructions.v1.md') -Value $annotationInstructions -Encoding UTF8

$families = @(
    'DOCUMENT_TITLE','PART_CHAPTER','SECTION','SUBSECTION','REGION_OR_GEOGRAPHIC_LABEL',
    'PROGRAM_OR_TOPIC_LABEL','TABLE_OR_LOCAL_LABEL','NAVIGATION_OR_AGENDA','ANNEX','CAPTION',
    'OTHER_STRUCTURAL_LABEL','UNKNOWN'
)
$schema = [ordered]@{
    schemaVersion = 'a99-r2-p3a-human-annotation-schema-v1'
    title = 'P3A Human A/B semantic heading annotation'
    runtimeUse = 'PROHIBITED'
    required = @('studyId','pilotId','documentId','sourceSha256','annotatorId','annotationStatus','rows')
    properties = [ordered]@{
        studyId = [ordered]@{ const = 'A99-R2' }
        pilotId = [ordered]@{ const = 'R2-PILOT-8' }
        documentId = [ordered]@{ type = 'string'; pattern = '^DOC-[0-9]{4}$' }
        sourceSha256 = [ordered]@{ type = 'string'; pattern = '^[0-9a-f]{64}$' }
        annotatorId = [ordered]@{ enum = @('A','B') }
        annotationStatus = [ordered]@{ enum = @('EMPTY_TEMPLATE_AWAITING_HUMAN','HUMAN_FROZEN') }
        rows = [ordered]@{
            type = 'array'
            items = [ordered]@{
                type = 'object'
                additionalProperties = $false
                required = @('documentId','humanHeadingOrdinal','documentTitleText','semanticMembership','semanticFamily','confidence')
                properties = [ordered]@{
                    documentId = [ordered]@{ type = 'string'; pattern = '^DOC-[0-9]{4}$' }
                    humanHeadingOrdinal = [ordered]@{ type = 'integer'; minimum = 1 }
                    documentTitleText = [ordered]@{ type = 'string'; minLength = 1 }
                    semanticMembership = [ordered]@{ const = 'HEADING' }
                    semanticFamily = [ordered]@{ enum = $families }
                    optionalRole = [ordered]@{ type = @('string','null') }
                    sourceOccurrenceHint = [ordered]@{ type = @('string','null') }
                    reviewNote = [ordered]@{ type = @('string','null') }
                    confidence = [ordered]@{ enum = @('CERTAIN','REVIEW_REQUIRED') }
                }
            }
        }
    }
}
Write-Json (Join-Path $root 'annotation-schema.v1.json') $schema

# Build normalized, source-only packets. The existing CLI is used only in deterministic/no-LLM mode.
foreach ($entry in $scope | Where-Object { $_.includedForHumanAnnotation }) {
    $id = [string]$entry.documentId
    $source = [string]$entry.sourcePath
    $extension = [IO.Path]::GetExtension($source).ToLowerInvariant()
    $temp = Join-Path $env:TEMP ("a99-r2-p3a-" + $id + '-' + [Guid]::NewGuid().ToString('N') + '.json')
    try {
        if ($extension -in @('.docx','.docm')) {
            $raw = Invoke-DhxJson @('accuracy99','packet',$source,'--root',$RepoRoot) $temp
            $rawOccurrences = @($raw.occurrences)
            $occurrences = @()
            $ordinal = 0
            foreach ($item in $rawOccurrences) {
                $ordinal++
                $occurrences += [ordered]@{
                    sourceOccurrenceId = ('P{0:D6}' -f $ordinal)
                    sourceId = [string]$item.sourceId
                    sourceOrdinal = [int]$item.sourceOrdinal
                    paragraphIndex = [int]$item.sourceOrdinal
                    pageNumber = $null
                    literalSourceText = [string]$item.rawText
                    sourceTextSha256 = Sha256Text ([string]$item.rawText)
                    sourceSpan = [ordered]@{ start = 0; end = ([string]$item.rawText).Length }
                    metadata = [ordered]@{
                        style = $item.style
                        numbering = $item.numbering
                        layout = $item.layout
                    }
                }
            }
            $representation = [ordered]@{
                kind = 'DOCX_PARSER_PARAGRAPHS'
                ordering = 'paragraph sourceOrdinal ascending'
                paragraphCount = $occurrences.Count
                pageNumbersAvailable = $false
            }
        } elseif ($extension -eq '.pdf') {
            $raw = Invoke-DhxJson @('pdf-clusters',$source,'--no-llm') $temp
            $blocks = @($raw.blocks) | Sort-Object @{Expression={[int]$_.page}}, @{Expression={
                if ([string]$_.id -match '^b([0-9]+)$') { [int]$Matches[1] } else { [int]::MaxValue }
            }}, id
            $occurrences = @()
            $ordinal = 0
            foreach ($item in $blocks) {
                $ordinal++
                $literal = if ($null -ne $item.sourceText) { [string]$item.sourceText } else { [string]$item.text }
                $occurrences += [ordered]@{
                    sourceOccurrenceId = ('B{0:D6}' -f $ordinal)
                    blockId = [string]$item.id
                    sourceOrdinal = $ordinal - 1
                    pageNumber = [int]$item.page
                    literalSourceText = $literal
                    sourceTextSha256 = Sha256Text $literal
                    lineCount = [int]$item.lineCount
                    layout = [ordered]@{
                        topY = $item.topY; bottomY = $item.bottomY
                        left = $item.left; right = $item.right
                        style = $item.style
                    }
                    extractedText = [string]$item.text
                }
            }
            $representation = [ordered]@{
                kind = 'PDF_DETERMINISTIC_BLOCKS'
                ordering = 'page ascending, numeric block id ascending'
                pageCount = [int]$raw.pages
                blockCount = $occurrences.Count
                pageNumbersAvailable = $true
                extractor = 'dhx pdf-clusters --no-llm'
            }
        } else {
            throw "Unsupported P3A source type: $source"
        }

        $packet = [ordered]@{
            artifactKind = 'A99_R2_P3A_HUMAN_SOURCE_ONLY_PACKET'
            schemaVersion = 'a99-r2-p3a-source-packet-v1'
            studyId = 'A99-R2'
            pilotId = 'R2-PILOT-8'
            documentId = $id
            documentGroupId = [string]$entry.documentGroupId
            familyId = [string]$entry.familyId
            sourcePath = $source
            sourceSha256 = [string]$entry.sourceSha256
            sourceHashVerified = $true
            mediaType = [string]$entry.fileType
            sourceRepresentation = $representation
            occurrences = $occurrences
            contentPurpose = 'complete deterministic source evidence for independent human annotation only'
            containsModelPredictions = $false
            containsGoldLabels = $false
            containsResidualHints = $false
            runtimeUse = 'PROHIBITED'
        }
        Assert-NoForbiddenContent $packet $id
        Write-Json (Join-Path $packetRoot "$id.source.v1.json") $packet
    } finally {
        if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force }
    }
}

# Empty templates contain no semantic rows or inferred labels.
foreach ($annotator in @('A','B')) {
    $targetRoot = if ($annotator -eq 'A') { $aRoot } else { $bRoot }
    foreach ($entry in $scope | Where-Object { $_.includedForHumanAnnotation }) {
        $template = [ordered]@{
            schemaVersion = 'a99-r2-p3a-human-annotation-v1'
            studyId = 'A99-R2'
            pilotId = 'R2-PILOT-8'
            documentId = [string]$entry.documentId
            sourceSha256 = [string]$entry.sourceSha256
            annotatorId = $annotator
            annotationStatus = 'EMPTY_TEMPLATE_AWAITING_HUMAN'
            reviewedEntireDocument = $false
            rows = @()
            semanticLabelsCreatedByAutomation = $false
            modelOutputsVisibleToAnnotator = $false
            otherAnnotatorVisible = $false
        }
        Write-Json (Join-Path $targetRoot "$($entry.documentId).annotation.v1.json") $template
    }
}

$pilotAuthority = @()
foreach ($entry in $scope) {
    $id = [string]$entry.documentId
    if ($p2GoldById.ContainsKey($id)) {
        $ready = $p2GoldById[$id]
        $pilotAuthority += [ordered]@{
            documentId = $id
            sourcePath = [string]$entry.sourcePath
            sourceSha256 = [string]$entry.sourceSha256
            sourceHashVerified = [bool]$entry.sourceHashVerified
            semanticGoldStatus = 'READY'
            occurrenceGoldStatus = 'READY'
            authorityPath = $ready.path
            authoritySha256 = $ready.sha256
            semanticHeadingCount = $ready.semanticHeadingCount
            occurrenceResolvedCount = $ready.occurrenceResolvedCount
            independenceStatus = 'P2B_USER_AUTHORITY_PLUS_DETERMINISTIC_OCCURRENCE_FREEZE'
            readyForR2Scoring = $true
        }
    } else {
        $pilotAuthority += [ordered]@{
            documentId = $id
            sourcePath = [string]$entry.sourcePath
            sourceSha256 = [string]$entry.sourceSha256
            sourceHashVerified = [bool]$entry.sourceHashVerified
            semanticGoldStatus = 'MISSING'
            occurrenceGoldStatus = 'MISSING'
            authorityPath = $null
            authoritySha256 = $null
            semanticHeadingCount = $null
            occurrenceResolvedCount = 0
            independenceStatus = 'AWAITING_INDEPENDENT_HUMAN_A_B'
            readyForR2Scoring = $false
        }
    }
}
Write-Json (Join-Path $root 'pilot-authority.v1.json') ([ordered]@{
    schemaVersion = 'a99-r2-p3a-pilot-authority-v1'
    studyId = 'A99-R2'
    pilotId = 'R2-PILOT-8'
    frozenParentCommit = $parentCommit
    behavioralParent = "B0@$b0Commit"
    pilotDocuments = $pilotAuthority
    pilotMembershipChanged = $false
    reserveDocumentsTouched = $false
    labelsInspectedBeforePacketFreeze = $false
    modelOutputsInspected = $false
    knownResidualStringsUsedForSelection = $false
    modelCalls = 0
    providerCalls = 0
})

Write-Json (Join-Path $root 'annotation-scope.v1.json') ([ordered]@{
    schemaVersion = 'a99-r2-p3a-annotation-scope-v1'
    studyId = 'A99-R2'
    pilotId = 'R2-PILOT-8'
    frozenParentCommit = $parentCommit
    annotationDocuments = @($scope | Where-Object { $_.includedForHumanAnnotation })
    forbiddenDocuments = @($p2Ids)
    reserveDocumentsTouched = $false
    labelsInspectedBeforePacketFreeze = $false
    modelOutputsInspected = $false
    knownResidualStringsUsedForSelection = $false
    semanticLabelsCreatedByAutomation = $false
})

$p2After = @{}
foreach ($id in $p2Ids) { $p2After[$id] = Sha256File $p2GoldById[$id].path }
$p2Unchanged = $true
foreach ($id in $p2Ids) { if ($p2After[$id] -ne $p2GoldById[$id].sha256) { $p2Unchanged = $false } }
$readiness = [ordered]@{
    schemaVersion = 'a99-r2-p3a-readiness-v1'
    terminal = 'R2_P3_AWAITING_SIX_DOCUMENT_HUMAN_GOLD'
    pilotDocuments = 8
    strictReadyDocuments = 2
    awaitingIndependentHumanGold = 6
    humanATemplates = 6
    humanBTemplates = 6
    p2bAuthorityUnchanged = $p2Unchanged
    p2bFreezeSha256 = $p2FreezeSha
    p2bGoldSha256 = $p2After
    reserveUntouched = $true
    modelCalls = 0
    providerCalls = 0
    r2BInferenceAuthorized = $false
    sourcePacketDocuments = 6
    sourcePacketsContainPredictions = $false
    sourcePacketsContainGold = $false
    semanticLabelsGeneratedByAutomation = $false
}
Write-Json (Join-Path $root 'readiness.v1.json') $readiness

Write-Json (Join-Path $root 'decision.v1.json') ([ordered]@{
    schemaVersion = 'a99-r2-p3a-decision-v1'
    decision = 'R2_P3_AWAITING_SIX_DOCUMENT_HUMAN_GOLD'
    rationale = 'P2B authority is reusable for exactly two pilot documents; six documents have source-only packets and empty independent A/B templates.'
    r2BInferenceAuthorized = $false
    authorizationGate = @(
        'all 8 pilot documents have frozen semantic Gold',
        'all Gold source SHA values match frozen pilot source SHA',
        'all semantic disagreements are human-adjudicated',
        'required occurrence identities are frozen',
        'unresolved semantic count is 0',
        'Gold freeze commit predates first R2-B inference'
    )
    laterR2BContract = [ordered]@{
        behavioralSystem = "canonical B0@$b0Commit"
        model = 'canonical B0 model/config'
        prompt = 'canonical B0'
        runtime = 'canonical B0'
        inferenceCount = 'one fresh inference per pilot document; 8 total'
        automaticRepeats = $false
        postGoldTuning = $false
    }
    modelCalls = 0
    providerCalls = 0
})

Write-Output 'P3A_MODEL_CALLS=0'
Write-Output 'P3A_PROVIDER_CALLS=0'
Write-Output 'P3A_PILOT_DOCUMENTS=8'
Write-Output 'P3A_STRICT_READY=2'
Write-Output 'P3A_AWAITING_HUMAN_GOLD=6'
Write-Output 'P3A_HUMAN_A_TEMPLATES=6'
Write-Output 'P3A_HUMAN_B_TEMPLATES=6'
Write-Output "P3A_P2B_UNCHANGED=$p2Unchanged"
Write-Output 'P3A_RESERVE_UNTOUCHED=True'
Write-Output 'P3A_TERMINAL=R2_P3_AWAITING_SIX_DOCUMENT_HUMAN_GOLD'
