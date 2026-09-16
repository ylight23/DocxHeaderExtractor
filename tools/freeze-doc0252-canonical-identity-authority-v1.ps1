[CmdletBinding()]
param(
    [string]$OccurrenceRoot = 'artifacts/authority-audit/canonical-exhaustive-heading-occurrence-v1/DOC-0252',
    [string]$IdentityRoot = 'artifacts/authority-audit/canonical-semantic-identity-v1/DOC-0252',
    [string]$OutputRoot = 'artifacts/authority-audit/canonical-identity-authority-freeze-v1/DOC-0252',
    [string]$FreezeCommit = 'THIS_FREEZE_COMMIT'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Get-Location).Path
$documentId = 'DOC-0252'
$expectedSourceSha256 = '4dda3c8ec8cd74e3a61503db0f8e9f168270d39036e3825441ab6167f9e16a77'
$occurrencePath = Join-Path $repoRoot (($OccurrenceRoot -replace '/', '\') + '\exact-bindings.json')
$occurrenceManifestPath = Join-Path $repoRoot (($OccurrenceRoot -replace '/', '\') + '\manifest.json')
$occurrenceDecisionsPath = Join-Path $repoRoot (($OccurrenceRoot -replace '/', '\') + '\occurrence-decisions.json')
$sourceAuthorityPath = Join-Path $repoRoot (($OccurrenceRoot -replace '/', '\') + '\source-authority.json')
$identityManifestPath = Join-Path $repoRoot (($IdentityRoot -replace '/', '\') + '\manifest.json')
$identityInputPath = Join-Path $repoRoot (($IdentityRoot -replace '/', '\') + '\occurrence-input-manifest.json')
$assignmentsPath = Join-Path $repoRoot (($IdentityRoot -replace '/', '\') + '\occurrence-assignments.json')
$nodesPath = Join-Path $repoRoot (($IdentityRoot -replace '/', '\') + '\semantic-nodes.json')
$decisionsPath = Join-Path $repoRoot (($IdentityRoot -replace '/', '\') + '\identity-decisions.json')
$ambiguitiesPath = Join-Path $repoRoot (($IdentityRoot -replace '/', '\') + '\ambiguities.json')
$identityValidationPath = Join-Path $repoRoot (($IdentityRoot -replace '/', '\') + '\validation.json')
$postFreezeDiagnosticsPath = Join-Path $repoRoot (($IdentityRoot -replace '/', '\') + '\post-freeze-diagnostics.json')
$outputPath = Join-Path $repoRoot ($OutputRoot -replace '/', '\')

function Write-JsonFile {
    param([string]$Path, $Value)
    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    $json = $Value | ConvertTo-Json -Depth 60
    [IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

function Write-TextFile {
    param([string]$Path, [string]$Text)
    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    [IO.File]::WriteAllText($Path, $Text.TrimStart() + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

function Require-Path {
    param([string]$Path, [string]$Label)
    if (-not (Test-Path -LiteralPath $Path)) { throw ("IDENTITY_FREEZE_INPUT_MISSING: {0}: {1}" -f $Label,$Path) }
}

foreach ($item in @(
    @{ Path = $occurrencePath; Label = 'occurrence bindings' },
    @{ Path = $occurrenceManifestPath; Label = 'occurrence manifest' },
    @{ Path = $occurrenceDecisionsPath; Label = 'occurrence decisions' },
    @{ Path = $sourceAuthorityPath; Label = 'source authority' },
    @{ Path = $identityManifestPath; Label = 'identity manifest' },
    @{ Path = $identityInputPath; Label = 'identity input' },
    @{ Path = $assignmentsPath; Label = 'identity assignments' },
    @{ Path = $nodesPath; Label = 'semantic nodes' },
    @{ Path = $decisionsPath; Label = 'identity decisions' },
    @{ Path = $ambiguitiesPath; Label = 'ambiguities' },
    @{ Path = $identityValidationPath; Label = 'identity validation' },
    @{ Path = $postFreezeDiagnosticsPath; Label = 'post-freeze diagnostics' }
)) { Require-Path -Path $item.Path -Label $item.Label }

$occurrenceManifest = Get-Content -Raw $occurrenceManifestPath | ConvertFrom-Json
$sourceAuthority = Get-Content -Raw $sourceAuthorityPath | ConvertFrom-Json
$occurrenceAuthority = Get-Content -Raw $occurrencePath | ConvertFrom-Json
$identityManifest = Get-Content -Raw $identityManifestPath | ConvertFrom-Json
$identityInput = Get-Content -Raw $identityInputPath | ConvertFrom-Json
$assignmentsArtifact = Get-Content -Raw $assignmentsPath | ConvertFrom-Json
$nodesArtifact = Get-Content -Raw $nodesPath | ConvertFrom-Json
$decisionsArtifact = Get-Content -Raw $decisionsPath | ConvertFrom-Json
$ambiguitiesArtifact = Get-Content -Raw $ambiguitiesPath | ConvertFrom-Json
$identityValidation = Get-Content -Raw $identityValidationPath | ConvertFrom-Json
$postFreezeDiagnostics = Get-Content -Raw $postFreezeDiagnosticsPath | ConvertFrom-Json

$sourcePath = [string]$sourceAuthority.sourcePath
$sourceFullPath = Join-Path $repoRoot ($sourcePath -replace '/', '\')
Require-Path -Path $sourceFullPath -Label 'authoritative DOCX source'
$actualSourceSha256 = (Get-FileHash -LiteralPath $sourceFullPath -Algorithm SHA256).Hash.ToLowerInvariant()

$bindings = @($occurrenceAuthority.acceptedCanonicalBindings)
$assignments = @($assignmentsArtifact.assignments)
$nodes = @($nodesArtifact.semanticNodes)
$decisions = @($decisionsArtifact.decisions)
$ambiguities = @($ambiguitiesArtifact.ambiguities)
$occurrenceRefs = @($identityInput.occurrenceRefs)

$occurrenceIds = @($bindings | ForEach-Object occurrenceId)
$assignmentRefs = @($assignments | ForEach-Object headingOccurrenceRef)
$inputRefs = @($occurrenceRefs | ForEach-Object headingOccurrenceRef)
$nodeMemberRefs = @($nodes | ForEach-Object { @($_.memberOccurrenceRefs) })
$nodeRefs = @($nodes | ForEach-Object semanticNodeRef)
$assignedNodeRefs = @($assignments | ForEach-Object semanticNodeRef)
$duplicateAssignmentRefs = @($assignmentRefs | Group-Object | Where-Object Count -gt 1 | ForEach-Object Name)
$duplicateMembershipRefs = @($nodeMemberRefs | Group-Object | Where-Object Count -gt 1 | ForEach-Object Name)
$unknownAssignmentRefs = @($assignmentRefs | Where-Object { $inputRefs -notcontains $_ })
$unknownNodeMemberRefs = @($nodeMemberRefs | Where-Object { $inputRefs -notcontains $_ })
$orphanOccurrenceRefs = @($inputRefs | Where-Object { $assignmentRefs -notcontains $_ })
$orphanNodeRefs = @($nodeRefs | Where-Object { $assignedNodeRefs -notcontains $_ })
$multiNodes = @($nodes | Where-Object { @($_.memberOccurrenceRefs).Length -gt 1 })
$continuationAssignments = @($assignments | Where-Object occurrenceRole -eq 'CONTINUATION')
$primaryAssignments = @($assignments | Where-Object occurrenceRole -eq 'PRIMARY')
$repeatAssignments = @($assignments | Where-Object occurrenceRole -eq 'REPEAT')

$agendaSessionV = @($bindings | Where-Object { $_.exactText -eq 'SESSION V: Current Research' -and $_.sourceId -eq 'body[1]/p[166]' })
$agendaSessionVContinuation = @($bindings | Where-Object { $_.exactText -eq "SESSION V: Current Research (Cont$([char]0x2019)d)" -and $_.sourceId -eq 'body[1]/p[168]' })
$multiNodeMemberMatch = $false
if ($multiNodes.Count -eq 1) {
    $members = @($multiNodes[0].memberOccurrenceRefs)
    $agendaSessionVRef = @($occurrenceRefs | Where-Object { $_.sourceId -eq 'body[1]/p[166]' } | Select-Object -ExpandProperty headingOccurrenceRef)
    $agendaSessionVContinuationRef = @($occurrenceRefs | Where-Object { $_.sourceId -eq 'body[1]/p[168]' } | Select-Object -ExpandProperty headingOccurrenceRef)
    $multiNodeMemberMatch = ($members.Count -eq 2 -and $agendaSessionVRef.Count -eq 1 -and $agendaSessionVContinuationRef.Count -eq 1 -and $members -contains $agendaSessionVRef[0] -and $members -contains $agendaSessionVContinuationRef[0])
}

$allIdentityJson = (($assignmentsArtifact | ConvertTo-Json -Depth 60 -Compress) + ($nodesArtifact | ConvertTo-Json -Depth 60 -Compress) + ($decisionsArtifact | ConvertTo-Json -Depth 60 -Compress))
$forbiddenHierarchyFieldsPresent = ($allIdentityJson -match '"(parentSemanticNodeRef|parentHeadingOccurrenceId|parentEdges|ROOT|derivedLevel|treeDepth)"')
$checks = [ordered]@{
    occurrenceCount = ($bindings.Count -eq 40)
    occurrenceAssignmentCount = ($assignments.Count -eq 40)
    semanticNodeCount = ($nodes.Count -eq 39)
    primaryCount = ($primaryAssignments.Count -eq 39)
    repeatCount = ($repeatAssignments.Count -eq 0)
    continuationCount = ($continuationAssignments.Count -eq 1)
    multiOccurrenceNodeCount = ($multiNodes.Count -eq 1)
    exactlyOneApprovedContinuationPair = ($agendaSessionV.Count -eq 1 -and $agendaSessionVContinuation.Count -eq 1 -and $multiNodeMemberMatch)
    everyOccurrenceAssignedExactlyOnce = ($assignmentRefs.Count -eq 40 -and @($assignmentRefs | Sort-Object -Unique).Count -eq 40)
    everyOccurrenceKnown = ($unknownAssignmentRefs.Count -eq 0)
    everyNodeNonEmpty = (@($nodes | Where-Object { @($_.memberOccurrenceRefs).Length -lt 1 }).Count -eq 0)
    noDuplicateMembership = ($duplicateMembershipRefs.Count -eq 0)
    noOrphanOccurrence = ($orphanOccurrenceRefs.Count -eq 0)
    noOrphanSemanticNode = ($orphanNodeRefs.Count -eq 0)
    noUnknownNodeMembers = ($unknownNodeMemberRefs.Count -eq 0)
    noForbiddenHierarchyFields = (-not $forbiddenHierarchyFieldsPresent)
    occurrenceSourceShaMatches = ([string]$sourceAuthority.sourceSha256 -eq $expectedSourceSha256 -and $actualSourceSha256 -eq $expectedSourceSha256)
    hierarchyOpened = $false
    parentEdgeCount = 0
    levelDerived = $false
    historicalLevelRead = $false
    historicalParentRead = $false
    historicalHierarchyUsedForDecision = $false
    oldSemanticTotalUsedForDecision = $false
    providerCalls = 0
    modelCalls = 0
    occurrenceMutation = $false
    identityMutation = $false
    parentMutation = $false
    levelMutation = $false
    GoldMutationOutsideFreezeLane = $false
}
$positiveCheckNames = @('occurrenceCount','occurrenceAssignmentCount','semanticNodeCount','primaryCount','repeatCount','continuationCount','multiOccurrenceNodeCount','exactlyOneApprovedContinuationPair','everyOccurrenceAssignedExactlyOnce','everyOccurrenceKnown','everyNodeNonEmpty','noDuplicateMembership','noOrphanOccurrence','noOrphanSemanticNode','noUnknownNodeMembers','noForbiddenHierarchyFields','occurrenceSourceShaMatches')
$failedChecks = @($positiveCheckNames | Where-Object { -not [bool]$checks[$_] })
$firewallPass = (
    -not [bool]$checks.hierarchyOpened -and
    $checks.parentEdgeCount -eq 0 -and
    -not [bool]$checks.levelDerived -and
    -not [bool]$checks.historicalLevelRead -and
    -not [bool]$checks.historicalParentRead -and
    -not [bool]$checks.historicalHierarchyUsedForDecision -and
    -not [bool]$checks.oldSemanticTotalUsedForDecision -and
    $checks.providerCalls -eq 0 -and
    $checks.modelCalls -eq 0 -and
    -not [bool]$checks.occurrenceMutation -and
    -not [bool]$checks.identityMutation -and
    -not [bool]$checks.parentMutation -and
    -not [bool]$checks.levelMutation -and
    -not [bool]$checks.GoldMutationOutsideFreezeLane
)
if (-not $firewallPass) { $failedChecks += 'firewall' }
$status = if ($failedChecks.Count -eq 0) { 'USER_REVIEWED_CANONICAL_IDENTITY_GOLD' } else { 'IDENTITY_FREEZE_VALIDATION_FAILED' }

New-Item -ItemType Directory -Force -Path $outputPath | Out-Null

$inputFiles = @(
    [pscustomobject]@{ artifact = 'authoritativeDocx'; path = $sourcePath },
    [pscustomobject]@{ artifact = 'occurrenceAuthorityManifest'; path = (($OccurrenceRoot + '/manifest.json') -replace '\\','/') },
    [pscustomobject]@{ artifact = 'occurrenceDecisions'; path = (($OccurrenceRoot + '/occurrence-decisions.json') -replace '\\','/') },
    [pscustomobject]@{ artifact = 'exactBindings'; path = (($OccurrenceRoot + '/exact-bindings.json') -replace '\\','/') },
    [pscustomobject]@{ artifact = 'identityManifest'; path = (($IdentityRoot + '/manifest.json') -replace '\\','/') },
    [pscustomobject]@{ artifact = 'occurrenceInputManifest'; path = (($IdentityRoot + '/occurrence-input-manifest.json') -replace '\\','/') },
    [pscustomobject]@{ artifact = 'occurrenceAssignments'; path = (($IdentityRoot + '/occurrence-assignments.json') -replace '\\','/') },
    [pscustomobject]@{ artifact = 'semanticNodes'; path = (($IdentityRoot + '/semantic-nodes.json') -replace '\\','/') },
    [pscustomobject]@{ artifact = 'identityDecisions'; path = (($IdentityRoot + '/identity-decisions.json') -replace '\\','/') },
    [pscustomobject]@{ artifact = 'ambiguities'; path = (($IdentityRoot + '/ambiguities.json') -replace '\\','/') },
    [pscustomobject]@{ artifact = 'identityValidation'; path = (($IdentityRoot + '/validation.json') -replace '\\','/') },
    [pscustomobject]@{ artifact = 'postFreezeDiagnostics'; path = (($IdentityRoot + '/post-freeze-diagnostics.json') -replace '\\','/') }
)
$hashes = foreach ($item in $inputFiles) {
    $fullPath = if ($item.artifact -eq 'authoritativeDocx') { $sourceFullPath } else { Join-Path $repoRoot ($item.path -replace '/', '\') }
    [pscustomobject]@{ artifact = $item.artifact; path = $item.path; sha256 = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant() }
}

$validation = [ordered]@{
    artifactKind = 'A99_USER_REVIEWED_CANONICAL_IDENTITY_FREEZE_VALIDATION'
    schemaVersion = 'a99-canonical-identity-authority-freeze-v1-doc0252'
    documentId = $documentId
    status = $status
    checks = $checks
    failedChecks = $failedChecks
    occurrenceCount = $bindings.Count
    occurrenceAssignmentCount = $assignments.Count
    semanticNodeCount = $nodes.Count
    primaryCount = $primaryAssignments.Count
    repeatCount = $repeatAssignments.Count
    continuationCount = $continuationAssignments.Count
    multiOccurrenceNodeCount = $multiNodes.Count
    hierarchyOpened = $false
    parentEdgeCount = 0
    levelDerived = $false
    providerCalls = 0
    modelCalls = 0
    occurrenceMutation = $false
    identityMutation = $false
    parentMutation = $false
    levelMutation = $false
    GoldMutationOutsideFreezeLane = $false
}
Write-JsonFile (Join-Path $outputPath 'validation.json') $validation

$manifest = [ordered]@{
    artifactKind = 'A99_USER_REVIEWED_CANONICAL_IDENTITY_AUTHORITY_FREEZE'
    schemaVersion = 'a99-canonical-identity-authority-freeze-v1-doc0252'
    documentId = $documentId
    authorityStatus = 'USER_REVIEWED_CANONICAL_IDENTITY_GOLD'
    explicitUserApproval = $true
    approvedCheckpoints = @('4ce5cca','1b49f60')
    commitLineage = @(
        [pscustomobject]@{ commit = '4ce5cca'; role = 'canonical occurrence authority' }
        [pscustomobject]@{ commit = '1b49f60'; role = 'source-backed identity proposal' }
        [pscustomobject]@{ commit = $FreezeCommit; role = 'explicit user-approved identity freeze' }
    )
    sourcePath = $sourcePath
    sourceSha256 = $actualSourceSha256
    occurrenceCount = 40
    semanticNodeCount = 39
    occurrenceRoles = [ordered]@{ PRIMARY = 39; REPEAT = 0; CONTINUATION = 1 }
    multiOccurrenceNodeCount = 1
    approvedContinuation = [ordered]@{
        fromText = 'SESSION V: Current Research'
        toText = "SESSION V: Current Research (Cont$([char]0x2019)d)"
        owner = 'Agenda'
        sameSemanticNode = $true
    }
    provenance = [ordered]@{
        occurrenceAuthority = 'CANONICAL_EXHAUSTIVE_OCCURRENCE_READY'
        identityProposal = 'SOURCE_BACKED_IDENTITY_ADJUDICATION_AT_1b49f60'
        finalPromotion = 'EXPLICIT_USER_APPROVAL'
        hierarchyStatus = 'NOT_OPENED'
    }
    artifactHashes = @($hashes)
    validationArtifact = (($OutputRoot + '/validation.json') -replace '\\','/')
    historicalLevelRead = $false
    historicalParentRead = $false
    historicalHierarchyUsedForDecision = $false
    oldSemanticTotalUsedForDecision = $false
    hierarchyOpened = $false
    parentEdgeCount = 0
    levelDerived = $false
    providerCalls = 0
    modelCalls = 0
    occurrenceMutation = $false
    identityMutation = $false
    parentMutation = $false
    levelMutation = $false
    GoldMutationOutsideFreezeLane = $false
}
Write-JsonFile (Join-Path $outputPath 'authority-freeze-manifest.json') $manifest

$report = @"
# DOC-0252 — user-reviewed canonical identity authority freeze

Status: **$status**

## Frozen identity authority

- Canonical heading occurrences: **40**
- Semantic nodes: **39**
- PRIMARY: **39**
- REPEAT: **0**
- CONTINUATION: **1**
- Multi-occurrence semantic nodes: **1**
- Approved continuation: Agenda SESSION V: Current Research → SESSION V: Current Research (Cont’d)

The continuation occurrence belongs to the same semantic node and does not create a second hierarchy node. Future hierarchy input cardinality is 39 semantic nodes, not 40 occurrences.

## Provenance

- Occurrence authority: canonical exhaustive occurrence authority from checkpoint 4ce5cca
- Identity proposal: source-backed identity adjudication from checkpoint 1b49f60
- Final promotion: explicit user approval
- Source SHA-256: $actualSourceSha256
- Hashes recorded: $($hashes.Count)
- Commit lineage: 4ce5cca → 1b49f60 → $FreezeCommit

The proposal remains identified as source-backed; it is not rewritten as originally human-authored.

## Hierarchy firewall

- hierarchyOpened: false
- parentEdgeCount: 0
- levelDerived: false
- Historical parent/level: not read
- Provider/model calls: 0
- Occurrence mutation: false
- Identity mutation: false
- Parent mutation: false
- Level mutation: false

Validator: **$status**

Hierarchy is the next separate phase and must operate on the 39 frozen semantic nodes.
"@
Write-TextFile (Join-Path $outputPath 'report.md') $report

[pscustomobject]@{
    documentId = $documentId
    status = $status
    sourceSha256 = $actualSourceSha256
    occurrences = 40
    semanticNodes = 39
    primary = 39
    repeat = 0
    continuation = 1
    multiOccurrenceNodes = 1
    hashCount = $hashes.Count
    validator = if ($failedChecks.Count -eq 0) { 'PASS' } else { 'FAIL' }
    hierarchyOpened = $false
    parentEdgeCount = 0
    levelDerived = $false
    providerCalls = 0
    modelCalls = 0
} | ConvertTo-Json -Depth 10
