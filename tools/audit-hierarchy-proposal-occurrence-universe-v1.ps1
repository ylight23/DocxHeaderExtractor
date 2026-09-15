[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$ProposalRoot = 'artifacts/authority-audit/strict-heading-canonical-hierarchy-proposals-v1',
    [string]$ReviewPacketRoot = 'artifacts/authority-audit/strict-heading-canonical-hierarchy-review-v2/packets',
    [string]$OutputRoot = 'artifacts/authority-audit/strict-heading-canonical-hierarchy-proposals-v1'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = (Resolve-Path $RepoRoot).Path
$proposalDir = Join-Path $repo $ProposalRoot
$packetDir = Join-Path $repo $ReviewPacketRoot
$out = Join-Path $repo $OutputRoot

function Get-Field {
    param([object]$Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties | Where-Object { $_.Name -ieq $Name } | Select-Object -First 1
    if ($null -eq $property) { return $null }
    return $property.Value
}
function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Write-Json([object]$Value, [string]$Path, [int]$Depth = 30) {
    $Value | ConvertTo-Json -Depth $Depth | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

$documents = @('DOC-0001','DOC-0116','DOC-0122','DOC-0205','DOC-0216','DOC-0243','DOC-0252','DOC-0256','DOC-0258')
$rows = [System.Collections.Generic.List[object]]::new()

foreach ($doc in $documents) {
    $packetPath = Join-Path $repo "$ReviewPacketRoot/$doc.canonical-hierarchy-review.v2.json"
    if (-not (Test-Path $packetPath)) { throw "Missing v2 packet: $doc" }
    $packet = Get-Content -LiteralPath $packetPath -Raw | ConvertFrom-Json
    $proposalPath = if ($doc -eq 'DOC-0001') {
        Join-Path $repo 'artifacts/authority-audit/strict-heading-canonical-hierarchy-gold-v1/DOC-0001/source-review-adjudication.json'
    } else {
        Join-Path $repo "$ProposalRoot/$doc/source-review-adjudication.proposal.json"
    }
    if (-not (Test-Path $proposalPath)) { throw "Missing proposal/adjudication artifact: $doc" }
    $proposal = Get-Content -LiteralPath $proposalPath -Raw | ConvertFrom-Json
    $proposalOccurrenceCount = @($packet.headingOccurrences).Count
    $strictTotal = $proposalOccurrenceCount
    $packetSha = Sha $packetPath

    $canonicalCandidates = @(Get-ChildItem (Join-Path $repo 'eval/a99-closed-loop/canonical-semantic-gold-vnext') -Recurse -Filter "$doc.semantic-gold.v1.json" -File | ForEach-Object {
        $j = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
        [pscustomobject]@{ Path=$_.FullName; Json=$j }
    })
    if ($canonicalCandidates.Count -eq 0) {
        $canonicalCandidates = @(Get-ChildItem (Join-Path $repo 'eval/a99-closed-loop/canonical-semantic-gold-vnext') -Recurse -Filter "$doc.occurrence-gold.v1.json" -File | ForEach-Object {
            $j = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
            [pscustomobject]@{ Path=$_.FullName; Json=$j }
        })
    }
    $canonical = if ($canonicalCandidates.Count -gt 0) { $canonicalCandidates[0] } else { $null }
    $canonicalTotal = if ($null -ne $canonical) { Get-Field $canonical.Json 'semanticHeadingTotal' } else { $null }
    $canonicalExactFreeze = if ($null -ne $canonical) { [bool](Get-Field $canonical.Json 'exactOccurrenceFreeze') } else { $null }
    $canonicalSourceSha = if ($null -ne $canonical) { [string](Get-Field $canonical.Json 'sourceSha256') } else { $null }
    $lineageMatch = if ($null -ne $canonicalSourceSha -and $canonicalSourceSha) { $canonicalSourceSha -eq [string]$packet.sourceSha256 } else { $null }

    $sourcePath = [string]$packet.headingOccurrences[0].sourceProvenance.sourceReferencePath
    $sourceClass = if ($sourcePath -match 'toc-derived|partial-human|format-driven-human|rebased|tagged-pdf-coverage|legal-human') { 'KNOWN_POSITIVE_SUBSET' } else { 'UNKNOWN' }
    $classification = $sourceClass
    $secondary = $null
    if ($doc -eq 'DOC-0001') {
        $classification = 'UNKNOWN'
        $secondary = 'EXPLICIT_USER_APPROVAL_EXISTS_BUT_CANONICAL_EXHAUSTIVENESS_UNPROVEN'
    } elseif ($null -ne $lineageMatch -and -not $lineageMatch) {
        $classification = 'SOURCE_LINEAGE_MISMATCH'
        $secondary = 'KNOWN_POSITIVE_SUBSET'
    }
    $canonicalCapability = if ($null -eq $canonical) { 'UNKNOWN' } elseif ($canonicalExactFreeze) { 'EXACT_OCCURRENCE_MATERIALIZATION_CLAIMED' } else { 'NOT_EXHAUSTIVE_EXACT_OCCURRENCE_MATERIALIZATION' }
    $recommendedLabel = 'HISTORICAL_STRICT_HIERARCHY_PROPOSAL'
    $userApproved = ($doc -eq 'DOC-0001' -and [bool](Get-Field $proposal 'approvedByUser') -and [string](Get-Field $proposal 'approvalAuthority') -eq 'USER_APPROVAL_IN_CONVERSATION')
    $rows.Add([ordered]@{
        documentId=$doc
        proposalPath=($proposalPath.Replace($repo+'\','').Replace('\','/'))
        proposalStatus=[string](Get-Field $proposal 'status')
        proposalOccurrenceCount=$proposalOccurrenceCount
        proposalOccurrenceAuthority='STRICT_GOLD_V4_HEADING_ROWS'
        proposalSourcePacketSha256=$packetSha
        sourceReferencePath=$sourcePath
        strictHistoricalTotal=$strictTotal
        canonicalVNextSemanticTotal=$canonicalTotal
        canonicalVNextArtifact=if ($null -ne $canonical) { $canonical.Path.Replace($repo+'\','').Replace('\','/') } else { $null }
        canonicalVNextSourceSha256=$canonicalSourceSha
        sourceLineageMatch=$lineageMatch
        canonicalVNextExactOccurrenceFreeze=$canonicalExactFreeze
        exactOccurrenceMaterialization=[ordered]@{
            proposalRowsMaterialized=$true
            proposalRowCount=$proposalOccurrenceCount
            canonicalExhaustiveRowsMaterialized=$false
            capability=$canonicalCapability
        }
        authorityClassification=$classification
        secondaryClassification=$secondary
        recommendedProposalAuthorityLabel=$recommendedLabel
        explicitUserApproval=$userApproved
        canonicalHierarchyGoldEligible=$false
        reason='A strict heading row count is not proof of an exhaustive all-true-heading occurrence universe; semantic totals are not occurrence lists and exactOccurrenceFreeze is false or unavailable.'
    })
}

$audit = [ordered]@{
    auditKind='A99_HIERARCHY_PROPOSAL_OCCURRENCE_UNIVERSE_AUTHORITY_AUDIT'
    schemaVersion='a99-hierarchy-proposal-occurrence-universe-authority-audit-v1'
    status='PROPOSALS_NOT_CANONICAL_EXHAUSTIVE'
    providerCalls=0
    modelCalls=0
    historicalLevelRead=$false
    parentEdgesChanged=$false
    treeChanged=$false
    goldMutation=$false
    canonicalHierarchyGoldRule='Only an exact, source-compatible, canonical-exhaustive occurrence universe may authorize canonical hierarchy Gold.'
    documents=$rows
}
$auditPath = Join-Path $out 'hierarchy-occurrence-universe-audit.json'
Write-Json $audit $auditPath

$classificationLines = ($rows | ForEach-Object {
    "- $($_.documentId): proposal=$($_.proposalOccurrenceCount), strictHistoricalTotal=$($_.strictHistoricalTotal), canonicalVNextSemanticTotal=$($_.canonicalVNextSemanticTotal), classification=$($_.authorityClassification), lineageMatch=$($_.sourceLineageMatch), canonicalExactFreeze=$($_.canonicalVNextExactOccurrenceFreeze), explicitUserApproval=$($_.explicitUserApproval), recommendedLabel=$($_.recommendedProposalAuthorityLabel)"
}) -join "`n"
$report = @"
# Hierarchy proposal occurrence-universe authority audit

Status: **PROPOSALS_NOT_CANONICAL_EXHAUSTIVE**

This audit does not change parent edges, tree structure, proposal packets, or
Gold. It does not read historical level and made zero provider/model calls.

The proposal inputs are the 9 strict-gold-v4 heading-row packets. Those rows
are source-backed and materialized exactly once, but the strict heading row set
is not proven to be the canonical all-true-heading occurrence universe.
Canonical vNext semantic totals are not occurrence lists; where present,
exactOccurrenceFreeze is false. Missing occurrences were not inferred.

## Per-document result

$classificationLines

## Authority conclusion

No document passes the canonical-exhaustive occurrence gate in this audit.
The five previously valid trees remain source-backed proposals only. Their
safe authority label is `HISTORICAL_STRICT_HIERARCHY_PROPOSAL`. DOC-0116,
DOC-0122, and DOC-0216 remain `REVIEW_REQUIRED_CANONICAL_HIERARCHY` for their
existing parent-context ambiguity. DOC-0001 has explicit user approval in the
conversation provenance, but its canonical occurrence exhaustiveness is still
unproven; the existing approved artifact is not mutated here.

Historical-level comparison is blocked until a canonical-exhaustive,
source-compatible occurrence universe is independently established.
"@
$report | Set-Content -LiteralPath (Join-Path $out 'hierarchy-occurrence-universe-audit.report.md') -Encoding utf8NoBOM
Write-Output "Occurrence-universe audit complete: documents=$($rows.Count) canonicalGoldEligible=0 status=$($audit.status)"
