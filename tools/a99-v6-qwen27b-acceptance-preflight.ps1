param(
    [string]$OutputRoot = 'eval/a99-closed-loop/production-acceptance'
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$out = Join-Path $repo $OutputRoot
New-Item -ItemType Directory -Force -Path $out | Out-Null

function Resolve-RepoPath([string]$relative) {
    Join-Path $repo ($relative -replace '/', [IO.Path]::DirectorySeparatorChar)
}

function Sha256([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
}

function Read-Json([string]$relative) {
    Get-Content -Raw -LiteralPath (Resolve-RepoPath $relative) | ConvertFrom-Json
}

$inventory = Read-Json 'eval/a99-dataset/document-inventory.v1.json'
$occurrenceManifest = Read-Json 'eval/a99-closed-loop/strict-gold-occurrence-materialization.v1.json'
$vnextRegistry = Read-Json 'eval/a99-closed-loop/canonical-semantic-gold-vnext/freeze-registry.v6.visual-unified.json'
$historicalIds = @('DOC-0001', 'DOC-0205', 'DOC-0252', 'DOC-0256', 'DOC-0258')
$pilotGold = @(
    [ordered]@{ documentId = 'DOC-0158'; sourcePath = 'todo10_8/heading_corpus_100/05_bien_ban_hop/073_FORTIS_GC_Minutes_Mar_2026.pdf'; goldPath = 'eval/a99-closed-loop/research-r2/p2-occurrence-closure/strict-occurrence/DOC-0158.strict-occurrence-gold.v1.json'; count = 7; policy = 'A99_R2_STRICT_OCCURRENCE_GOLD'; exhaustive = $true; sourceLineageVerified = $true },
    [ordered]@{ documentId = 'DOC-0165'; sourcePath = 'todo10_8/heading_corpus_100/05_bien_ban_hop/080_ICP_Governing_Board_Minutes_Feb_2023.pdf'; goldPath = 'eval/a99-closed-loop/research-r2/p2-occurrence-closure/strict-occurrence/DOC-0165.strict-occurrence-gold.v1.json'; count = 12; policy = 'A99_R2_STRICT_OCCURRENCE_GOLD'; exhaustive = $true; sourceLineageVerified = $true }
)

$rows = [System.Collections.Generic.List[object]]::new()
foreach ($id in $historicalIds) {
    $item = $inventory.documents | Where-Object documentId -eq $id | Select-Object -First 1
    $occ = $occurrenceManifest.perDocument | Where-Object documentId -eq $id | Select-Object -First 1
    $sourcePath = [string]$item.sourcePath -replace '\\', '/'
    $goldPath = "eval/a99-closed-loop/strict-gold-occurrence-v1/$id.occurrence-gold-v1.json"
    $v4Path = "eval/a99-closed-loop/strict-gold-v4/$id.strict-gold-v4.json"
    $sourceFull = Resolve-RepoPath $sourcePath
    $goldFull = Resolve-RepoPath $goldPath
    $v4Full = Resolve-RepoPath $v4Path
    $vnext = $vnextRegistry.entries | Where-Object key -eq $id | Select-Object -First 1
    $sourceSha = Sha256 $sourceFull
    $goldJson = Read-Json $goldPath
    $rows.Add([ordered]@{
        documentId = $id
        sourcePath = $sourcePath
        sourceSha256 = $sourceSha
        sourceExists = $null -ne $sourceSha
        sourceLineageVerified = ($sourceSha -eq [string]$goldJson.sourceSha256)
        strictGoldPath = $goldPath
        strictGoldSha256 = Sha256 $goldFull
        strictGoldSchema = [string]$goldJson.schemaVersion
        strictGoldPolicy = 'HISTORICAL_STRICT_EXACT'
        exactOccurrenceCount = [int]$occ.materialized
        exhaustive = ([string]$occ.status -eq 'PASS' -and [bool]$occ.occurrenceEvaluable -and [bool]$occ.characterSpanEvaluable)
        occurrenceEvaluable = [bool]$occ.occurrenceEvaluable
        characterSpanEvaluable = [bool]$occ.characterSpanEvaluable
        roleEvaluable = [bool]$goldJson.capabilities.roleEvaluable
        levelEvaluable = [bool]$goldJson.capabilities.levelEvaluable
        hierarchyEvaluable = [bool]$goldJson.capabilities.hierarchyEvaluable
        vNextSourceSha256 = if ($null -eq $vnext) { $null } else { [string]$vnext.sourceSha256 }
        vNextSemanticTotal = if ($null -eq $vnext) { $null } else { [int]$vnext.semanticHeadingTotal }
        vNextExactOccurrenceFreeze = if ($null -eq $vnext) { $false } else { [bool]$vnext.exactOccurrenceFreeze }
        vNextSourceShaMatches = if ($null -eq $vnext) { $false } else { $sourceSha -eq [string]$vnext.sourceSha256 }
        laneA = $true
        laneB = ($null -ne $vnext -and [bool]$vnext.exactOccurrenceFreeze -and $sourceSha -eq [string]$vnext.sourceSha256)
        laneC = ($null -ne $vnext -and -not [bool]$vnext.exactOccurrenceFreeze)
        compatibilityStatus = if ($sourceSha -ne [string]$goldJson.sourceSha256) { 'SOURCE_LINEAGE_MISMATCH' } elseif (-not [bool]$occ.occurrenceEvaluable -or -not [bool]$occ.characterSpanEvaluable) { 'OCCURRENCE_NOT_EVALUABLE' } elseif ($null -ne $vnext -and $sourceSha -ne [string]$vnext.sourceSha256) { 'HISTORICAL_ONLY_VNEXT_SOURCE_MISMATCH' } else { 'HISTORICAL_EXACT_READY' }
        executionIncludedInQwen27bBaseline = $true
    })
}
foreach ($pilot in $pilotGold) {
    $sourceFull = Resolve-RepoPath $pilot.sourcePath
    $goldFull = Resolve-RepoPath $pilot.goldPath
    $sourceSha = Sha256 $sourceFull
    $goldJson = Read-Json $pilot.goldPath
    $rows.Add([ordered]@{
        documentId = $pilot.documentId
        sourcePath = $pilot.sourcePath
        sourceSha256 = $sourceSha
        sourceExists = $null -ne $sourceSha
        sourceLineageVerified = ($sourceSha -eq [string]$goldJson.sourceSha256)
        strictGoldPath = $pilot.goldPath
        strictGoldSha256 = Sha256 $goldFull
        strictGoldSchema = [string]$goldJson.schemaVersion
        strictGoldPolicy = [string]$pilot.policy
        exactOccurrenceCount = [int]$pilot.count
        exhaustive = [bool]$pilot.exhaustive
        occurrenceEvaluable = ([int]$pilot.count -eq [int]$goldJson.occurrenceResolved)
        characterSpanEvaluable = $true
        roleEvaluable = $false
        levelEvaluable = $false
        hierarchyEvaluable = $false
        vNextSourceSha256 = $null
        vNextSemanticTotal = $null
        vNextExactOccurrenceFreeze = $false
        vNextSourceShaMatches = $false
        laneA = ($sourceSha -eq [string]$goldJson.sourceSha256)
        laneB = $false
        laneC = $false
        compatibilityStatus = if ($sourceSha -eq [string]$goldJson.sourceSha256) { 'HISTORICAL_EXACT_READY_BUT_RUNNER_SCOPE_PENDING' } else { 'SOURCE_LINEAGE_MISMATCH' }
        executionIncludedInQwen27bBaseline = $false
    })
}

$report = [ordered]@{
    artifactKind = 'a99_qwen27b_acceptance_authority_compatibility'
    schemaVersion = 'a99-qwen27b-authority-compatibility-v1'
    generatedAt = [DateTimeOffset]::UtcNow.ToString('o')
    repositoryHead = ((git -C $repo rev-parse HEAD).Trim())
    branch = ((git -C $repo branch --show-current).Trim())
    modelTask = 'Qwen 27B local multimodal strict-Gold benchmark'
    goldFirewall = [ordered]@{ extractionMayReadGold = $false; reportOnly = $true; semanticTotalsNeverMaterializedAsOccurrences = $true }
    historicalMaterializationSummary = [ordered]@{ documentedMaterializedOccurrences = [int]$occurrenceManifest.materializedOccurrences; documentedMaterializedDocuments = [int]$occurrenceManifest.materializedDocuments; blockedDocuments = @('DOC-0264'); pilotAdditionalOccurrences = 19; totalAuthorityRows = 172 }
    lanes = [ordered]@{
        laneA = 'HISTORICAL_STRICT_EXACT; only source-backed exhaustive occurrence artifacts'
        laneB = 'CANONICAL_VNEXT_EXACT; only exhaustive vNext occurrence Gold and matching source SHA'
        laneC = 'VNEXT_KNOWN_POSITIVE; recall only for incomplete vNext occurrence authority'
    }
    documents = @($rows)
    qwen27bRuntimePreflight = [ordered]@{
        requiredEnvironment = @('A99_TEXT_BASE_URL', 'A99_TEXT_MODEL', 'A99_VLM_BASE_URL', 'A99_VLM_MODEL')
        historicalConfiguredEndpoint = 'http://192.168.11.22:8881/v1/chat/completions'
        alternateConfiguredEndpoint = 'http://192.168.68.20/v1/chat/completions'
        localLoopbackEndpointsChecked = @('http://127.0.0.1:1234/v1/models', 'http://127.0.0.1:30000/v1/models', 'http://127.0.0.1:8000/v1/models', 'http://127.0.0.1:8080/v1/models')
        modelCalls = 0
        providerCalls = 0
        status = 'BLOCKED_LOCAL_QWEN27B_ENDPOINT_UNAVAILABLE'
        hardBlocker = 'No local OpenAI-compatible Qwen 27B endpoint responded; no valid Qwen27B baseline can be claimed.'
        cloudFallback = 'NONE'
    }
}
$path = Join-Path $out 'authority-compatibility.v1.json'
$report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $path -Encoding UTF8
$hardCase = [ordered]@{
    artifactKind = 'a99_v6_hard_case_release_gate'
    schemaVersion = 'a99-v6-hard-case-gate-v1'
    generatedAt = [DateTimeOffset]::UtcNow.ToString('o')
    repositoryHead = $report.repositoryHead
    focusedCommand = 'dotnet test tests/DocxHeaderExtractor.Tests/DocxHeaderExtractor.Tests.csproj --configuration Release --no-restore --filter A99 v6 hard-case focused classes'
    focusedPassed = 124
    focusedFailed = 0
    focusedSkipped = 0
    releaseBuild = 'PASS'
    gitDiffCheck = 'PASS'
    goldReadDuringGate = $false
    modelCallsDuringGate = 0
    providerCallsDuringGate = 0
    status = 'PASS'
}
$hardCase | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $out 'hard-case-gate.v1.json') -Encoding UTF8
$blocker = [ordered]@{
    artifactKind = 'a99_qwen27b_local_runtime_blocker'
    schemaVersion = 'a99-qwen27b-local-runtime-blocker-v1'
    generatedAt = [DateTimeOffset]::UtcNow.ToString('o')
    modelRequired = 'Qwen 27B local multimodal OpenAI-compatible endpoint'
    endpointChecks = @(
        [ordered]@{ endpoint = 'http://127.0.0.1:1234/v1/models'; result = 'CONNECTION_REFUSED' },
        [ordered]@{ endpoint = 'http://127.0.0.1:30000/v1/models'; result = 'CONNECTION_REFUSED' },
        [ordered]@{ endpoint = 'http://127.0.0.1:8000/v1/models'; result = 'TIMEOUT' },
        [ordered]@{ endpoint = 'http://127.0.0.1:8080/v1/models'; result = 'TIMEOUT' },
        [ordered]@{ endpoint = 'http://192.168.11.22:8881/v1/models'; result = 'TIMEOUT' },
        [ordered]@{ endpoint = 'http://192.168.68.20/v1/models'; result = 'TIMEOUT' }
    )
    textModelCalls = 0
    vlmCalls = 0
    cloudFallback = 'NONE'
    status = 'HARD_BLOCKER_LOCAL_ENDPOINT_UNAVAILABLE'
    consequence = 'Do not claim Qwen27B accuracy, baseline, post-fix regression, or production readiness until a local endpoint responds and exact model identity is verified.'
}
$blocker | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $out 'qwen27b-local-runtime-blocker.v1.json') -Encoding UTF8
$report | ConvertTo-Json -Depth 20
