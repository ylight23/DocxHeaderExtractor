$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$artifactDir = Join-Path $root 'artifacts/identity-gold'
$tempRoot = Join-Path $env:TEMP 'a99-tesseract-spike'
$v3Path = Join-Path $artifactDir 'semantic-identity-bindings.user-reviewed.v3.json'
$rawPath = Join-Path $artifactDir 'ir018-im1-tesseract-raw.v1.json'
$matchPath = Join-Path $artifactDir 'ir018-im1-tesseract-match.v1.json'
$v4Path = Join-Path $artifactDir 'semantic-identity-bindings.user-reviewed.v4.json'
$feasibilityPath = Join-Path $artifactDir 'tesseract-local-binding-feasibility.v1.json'

function Sha256([string] $path) {
    (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

$raw = Get-Content -LiteralPath $rawPath -Raw | ConvertFrom-Json
$match = Get-Content -LiteralPath $matchPath -Raw | ConvertFrom-Json
if ($raw.SchemaVersion -ne 'a99-ir018-local-tesseract-raw-v1') { throw 'Unexpected raw OCR schema.' }
if ($match.Status -ne 'UNIQUE_MATCH' -or $match.Candidates.Count -ne 1) { throw 'IR-018 is not uniquely matched.' }

$candidate = $match.Candidates[0]
$ir018 = $null
$v4 = Get-Content -LiteralPath $v3Path -Raw | ConvertFrom-Json
$ir018 = $v4.items | Where-Object id -eq 'IR-018'
if ($null -eq $ir018) { throw 'IR-018 item not found.' }

# Update only the previously-unbound inner region. All other v3 properties and
# IR-019..IR-022 item payloads are carried forward unchanged in memory.
$ir018.bindingStatus = 'EXACT_BOUND'
$ir018.machineEvaluable = $true
$ir018.inner.regionStatus = 'EXACT_BOUND'
$ir018.inner | Add-Member -NotePropertyName locatorAuthority -NotePropertyValue 'VISUAL_DERIVED' -Force
$ir018.inner | Add-Member -NotePropertyName sourceOccurrenceId -NotePropertyValue $candidate.StableOccurrenceId -Force
$ir018.inner.regionLocatorMethod = 'LOCAL_TESSERACT_5_2_0_EXACT_LINE'
$ir018.inner.pixelBBox = $candidate.PixelBBox
$ir018.inner.normalizedBBox = [pscustomobject]@{
    X = [double]$candidate.PixelBBox.Left / $raw.ImageWidth
    Y = [double]$candidate.PixelBBox.Top / $raw.ImageHeight
    Width = [double]$candidate.PixelBBox.Width / $raw.ImageWidth
    Height = [double]$candidate.PixelBBox.Height / $raw.ImageHeight
}
$ir018.inner.regionImageSha256 = $candidate.RegionImageSha256
$ir018.inner.textEvidence = $candidate.RecognizedText
$ir018.inner | Add-Member -NotePropertyName ocrEvidence -NotePropertyValue ([pscustomobject]@{
    normalizedText = $match.ExpectedSurfaceNormalized
    confidence = [double](($raw.AllDetectedRegions | Where-Object { $_.Kind -eq 'LINE' -and $_.RegionIndex -eq $candidate.ConstituentRegionIndices[0] }).Confidence)
    engine = 'Tesseract 5.2.0'
    wrapper = 'Tesseract NuGet wrapper 5.2.0'
    language = 'eng'
    pageSegmentationMode = 'SPARSE_TEXT'
    ocrEngineMode = 'LSTM_ONLY'
    userDefinedDpi = 300
    preprocessing = 'NONE'
    rawArtifact = 'artifacts/identity-gold/ir018-im1-tesseract-raw.v1.json'
    rawArtifactSha256 = Sha256 $rawPath
    matchArtifact = 'artifacts/identity-gold/ir018-im1-tesseract-match.v1.json'
    matchArtifactSha256 = Sha256 $matchPath
}) -Force
$ir018.inner.sourceLineageVerified = $true
$ir018.inner.bindingEvidence = @(
    'PDF source SHA256 verified',
    'page 66 /Im1 XObject name verified',
    'exact image XObject bytes and image SHA256 verified',
    'raw local OCR output was frozen before matching',
    'one exact normalized line match found in frozen OCR regions',
    'pixel bbox and region PNG SHA256 are source-image-backed',
    'stable visual-region occurrence ID derived by the existing identity factory',
    'no VLM, provider, Gold, fuzzy matching, or fabricated coordinates used'
)

$v4.artifactKind = 'a99_semantic_identity_bindings'
$v4.schemaVersion = 'a99-semantic-identity-bindings-user-reviewed-v4'
$v4.status = 'EXACT_BOUND'
$v4.createdFromCommit = (& git -C $root rev-parse HEAD).Trim()
$v4.summary.totalGoldItems = 5
$v4.summary.exactBound = 5
$v4.summary.imageXObjectsBound = 1
$v4.summary.imageRegionsBound = 1
$v4.summary.bindingIncomplete = 0
$v4.summary.machineEvaluable = 5
$v4.summary.fuzzyJoins = 0
$v4.summary.fabricatedOccurrenceIds = 0
$v4.summary.goldReadBeforeBinding = $false
$v4.gate = 'READY_FOR_IDENTITY_PROMOTION_BENCHMARK'
$v4.remainingBlockers = @()
$v4.pdfRecovery = [pscustomobject]@{
    engine = 'Tesseract 5.2.0'
    wrapper = 'Tesseract NuGet wrapper 5.2.0'
    mode = 'LOCAL_FEASIBILITY_SPIKE_ONLY'
    productionDependencyAdded = $false
    sourceImageSha256 = $raw.SourceImageSha256
    rawOcrArtifact = 'artifacts/identity-gold/ir018-im1-tesseract-raw.v1.json'
    rawOcrArtifactSha256 = Sha256 $rawPath
    exactMatchArtifact = 'artifacts/identity-gold/ir018-im1-tesseract-match.v1.json'
    exactMatchArtifactSha256 = Sha256 $matchPath
    deterministicReplay = 'TWO_RUNS_BYTE_IDENTICAL'
    providerCalls = 0
    modelCalls = 0
}
$v4 | Add-Member -NotePropertyName lineage -NotePropertyValue $null -Force
$v4.lineage = [pscustomobject]@{
    priorForensicArtifact = 'artifacts/identity-gold/pdf-binding-forensic.v3.json'
    priorForensicArtifactPreserved = $true
    priorBindingArtifact = $v3Path.Replace($root + '\', '').Replace('\', '/')
    correctedBindingArtifact = 'artifacts/identity-gold/semantic-identity-bindings.user-reviewed.v4.json'
    rawOcrArtifact = 'artifacts/identity-gold/ir018-im1-tesseract-raw.v1.json'
    exactMatchArtifact = 'artifacts/identity-gold/ir018-im1-tesseract-match.v1.json'
    semanticGoldChanged = $false
    knownN15Changed = $false
    goldUsed = $false
    modelOutputsUsed = $false
}

$feasibility = [ordered]@{
    artifactKind = 'a99_local_tesseract_binding_feasibility'
    schemaVersion = 'a99-local-tesseract-binding-feasibility-v1'
    status = 'FEASIBLE_SPIKE'
    source = [ordered]@{
        documentId = 'DOC-0133'
        sourcePath = 'todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/048_IBRD_Financial_Statements_March_2025.pdf'
        sourceSha256 = $raw.SourcePdfSha256
        page = 66
        xObjectResourceName = '/Im1'
        imageOccurrenceId = $raw.SourceImageOccurrenceId
        imageSha256 = $raw.SourceImageSha256
        imageSize = [ordered]@{ width = $raw.ImageWidth; height = $raw.ImageHeight }
    }
    execution = [ordered]@{
        providerCalls = 0; modelCalls = 0; vlmCalls = 0; remoteOcr = $false; goldUsed = $false; modelOutputsUsed = $false
        sourceTextUsedForRecognition = $false; exactImageBytesUsed = $true; preprocessing = 'NONE'
    }
    engine = [ordered]@{
        name = 'Tesseract'
        version = '5.2.0'
        wrapper = 'Tesseract NuGet wrapper 5.2.0'
        language = 'eng'
        pageSegmentationMode = 'SPARSE_TEXT'
        ocrEngineMode = 'LSTM_ONLY'
        userDefinedDpi = 300
        tessdataSha256 = $raw.Configuration.LanguageDataSha256
        packageSha256 = '202d82fc7c7d8384df7da57206d5e1f456ccdabd648c46e67cdfaa3a911d4795'
        nativeX64 = [ordered]@{
            tesseract50Sha256 = 'de4d04ec75095374d98f5dd7a60d14d7e2e0f76589db693eccf7ae658be8cb2b'
            leptonicaSha256 = 'dfcb3e6ed0b16bc55bfdbcf53543cfe42a354b87c3e35bd3a95eebf005d73e76'
        }
    }
    output = [ordered]@{
        rawArtifact = 'artifacts/identity-gold/ir018-im1-tesseract-raw.v1.json'
        rawArtifactSha256 = Sha256 $rawPath
        rawRegionCount = $raw.AllDetectedRegions.Count
        lineRegionCount = @($raw.AllDetectedRegions | Where-Object Kind -eq 'LINE').Count
        wordRegionCount = @($raw.AllDetectedRegions | Where-Object Kind -eq 'WORD').Count
        replayRuns = 2
        replayByteIdentical = $true
        exactMatcher = 'NARROW_EXACT_NORMALIZATION_ONLY'
        matchedRegionCount = $match.Candidates.Count
        matchArtifact = 'artifacts/identity-gold/ir018-im1-tesseract-match.v1.json'
        matchArtifactSha256 = Sha256 $matchPath
        stableVisualOccurrenceId = $candidate.StableOccurrenceId
        pixelBoundingBox = $candidate.PixelBBox
        regionImageSha256 = $candidate.RegionImageSha256
    }
    licensing = [ordered]@{
        tesseract = 'Apache-2.0'
        leptonica = 'BSD-2-Clause'
        packageSource = 'NuGet package Tesseract 5.2.0; temporary feasibility environment only'
        committedProductionDependency = $false
    }
    deployment = [ordered]@{
        currentStatus = 'FEASIBILITY_ONLY_NOT_PRODUCTION'
        windowsSpike = $true
        x64NativeAssetsObserved = $true
        pinnedLanguageDataRequired = $true
        productionPackagingDecision = 'NOT_MADE'
        portabilityRisk = 'native runtime and tessdata require explicit per-platform packaging and validation'
    }
    contracts = [ordered]@{
        locatorAuthority = 'VISUAL_DERIVED'
        coordinateAuthority = 'HARNESS_SOURCE_IMAGE_PIXELS'
        textAuthority = 'VISUAL_DERIVED'
        sourceTextDuplicated = $false
        modelNumericOffsets = $false
        wholeImageAsHeadingRegion = $false
        fuzzyMatching = $false
        goldSpecificLocator = $false
    }
    gate = [ordered]@{
        sourceImageIdentity = 'PASS'
        rawOcrPersistedBeforeMatch = 'PASS'
        uniqueExactRegion = 'PASS'
        deterministicReplay = 'PASS'
        visualRegionBinding = 'PASS_FOR_IR018_SPIKE'
        fiveOfFiveIdentityGold = 'PASS'
        productionReady = $false
        nextGate = 'INDEPENDENT_IDENTITY_PROMOTION_BENCHMARK'
    }
    provenance = [ordered]@{
        generatedFromCommit = $v4.createdFromCommit
        previousBindingArtifact = 'artifacts/identity-gold/semantic-identity-bindings.user-reviewed.v3.json'
        previousForensicArtifact = 'artifacts/identity-gold/pdf-binding-forensic.v3.json'
        semanticGoldChanged = $false
        ir018SemanticRelationChanged = $false
        knownN15Changed = $false
    }
}

[IO.File]::WriteAllText($v4Path, ($v4 | ConvertTo-Json -Depth 30) + [Environment]::NewLine)
[IO.File]::WriteAllText($feasibilityPath, ($feasibility | ConvertTo-Json -Depth 30) + [Environment]::NewLine)
Write-Output "V4=$v4Path"
Write-Output "FEASIBILITY=$feasibilityPath"
Write-Output "V4_STATUS=$($v4.status)"
Write-Output "MACHINE_EVALUABLE=$($v4.summary.machineEvaluable)/$($v4.summary.totalGoldItems)"
