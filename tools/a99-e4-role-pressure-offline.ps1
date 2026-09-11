param(
    [string]$RepoRoot = (Get-Location).Path
)

$ErrorActionPreference = 'Stop'
$RepoRoot = [IO.Path]::GetFullPath($RepoRoot)
$out = Join-Path $RepoRoot 'eval/a99-closed-loop/task-decomposition/e4'
New-Item -ItemType Directory -Force -Path $out | Out-Null

function Read-Json([string]$path) {
    Get-Content -LiteralPath (Join-Path $RepoRoot $path) -Raw | ConvertFrom-Json
}

$allowedRoles = @(
    'DOCUMENT_TITLE','PART','CHAPTER','SECTION','SUBSECTION','ARTICLE','CLAUSE_HEADING',
    'ANNEX_HEADING','LOCAL_INDEX_TITLE','AGENDA_NAVIGATION_HEADING','TOC_ENTRY','FRONT_MATTER',
    'CONTENT_HEADING','OTHER_STRUCTURAL_LABEL','RUNNING_HEADER','CAPTION','LIST_ITEM','TABLE_LABEL',
    'DECORATIVE_TEXT','BODY_FRAGMENT','OTHER_NON_TASK_STRUCTURAL'
)

$targets = @(
    [ordered]@{ documentId='DOC-0252'; sourceId='body[1]/tbl[2]/tr[1]/tc[2]/p[6]'; text='Western Asia'; semanticFamily='REGION_LABEL'; sourceKind='table-cell' },
    [ordered]@{ documentId='DOC-0258'; sourceId='body[1]/p[11]'; text='Africa'; semanticFamily='REGION_LABEL'; sourceKind='paragraph' },
    [ordered]@{ documentId='DOC-0258'; sourceId='body[1]/p[12]'; text='Asia and the Pacific'; semanticFamily='REGION_LABEL'; sourceKind='paragraph' },
    [ordered]@{ documentId='DOC-0258'; sourceId='body[1]/p[17]'; text='Eurostat–OECD PPP Program'; semanticFamily='PROGRAM_LABEL'; sourceKind='paragraph' },
    [ordered]@{ documentId='DOC-0258'; sourceId='body[1]/p[18]'; text='Commonwealth of Independent States (CIS)'; semanticFamily='REGION_LABEL'; sourceKind='paragraph' },
    [ordered]@{ documentId='DOC-0258'; sourceId='body[1]/p[19]'; text='Latin America and the Caribbean'; semanticFamily='REGION_LABEL'; sourceKind='paragraph' },
    [ordered]@{ documentId='DOC-0258'; sourceId='body[1]/p[24]'; text='Western Asia'; semanticFamily='REGION_LABEL'; sourceKind='paragraph' }
)

$analogueNames = @('Africa','Asia and the Pacific','Commonwealth of Independent States (CIS)','Eurostat–OECD PPP Program','Latin America and the Caribbean','Western Asia')
$b0252 = Read-Json 'eval/a99-closed-loop/semantic-text-generalization/DOC-0252/r1/prediction.v1.json'
$b0258 = Read-Json 'eval/a99-closed-loop/semantic-text-generalization/DOC-0258/r1/prediction.v1.json'
$analogueRows = foreach ($name in $analogueNames) {
    $rows = @($b0252.rawModelHeadings | Where-Object { $_.text -eq $name })
    [ordered]@{
        text = $name
        sourceOccurrence = if ($rows.Count -gt 0) { @($rows | ForEach-Object { [ordered]@{ source = $_.source; occurrence = $_.occurrence; role = $_.role } }) } else { @() }
        emittedInB0 = ($rows.Count -gt 0)
        roles = @($rows | ForEach-Object { $_.role } | Sort-Object -Unique)
        sameFamilyRoleConsistency = if ($rows.Count -gt 0 -and (@($rows | ForEach-Object { $_.role } | Sort-Object -Unique).Count -eq 1)) { 'CONSISTENT' } else { 'NOT_OBSERVABLE' }
    }
}

$targetRows = foreach ($target in $targets) {
    $sameText = @($b0252.rawModelHeadings | Where-Object { $_.text -eq $target.text })
    $analogue = if ($sameText.Count -gt 0) {
        @($sameText | ForEach-Object { [ordered]@{ documentId='DOC-0252'; text=$_.text; sourceOccurrence=[ordered]@{ source=$_.source; occurrence=$_.occurrence }; role=$_.role } })
    } else {
        @($analogueRows | Where-Object { $_.text -eq $target.text } | ForEach-Object { $_.sourceOccurrence | ForEach-Object { [ordered]@{ documentId='DOC-0252'; text=$target.text; sourceOccurrence=$_; role=$_.role } } })
    }
    [ordered]@{
        documentId=$target.documentId; targetSourceId=$target.sourceId; targetText=$target.text; targetSemanticFamily=$target.semanticFamily
        targetEmitted=$false; targetRole='NOT_AVAILABLE'; targetRoleReason='B0 omission'
        closestEmittedSemanticAnalogues=$analogue
        sameFamilyRoleConsistency=if ($analogue.Count -gt 0 -and (@($analogue | ForEach-Object { $_.role } | Sort-Object -Unique).Count -eq 1)) { 'CONSISTENT' } else { 'NOT_OBSERVABLE' }
    }
}

$rolePressure = [ordered]@{
    schemaVersion='a99-a99-e4-role-pressure-v1'; experimentId='A99-E4'; status='COMPLETE_OFFLINE'
    behavioralParent='B0@76c4e01'; model='qwen/qwen3.7-flash'; modelCalls=0; providerCalls=0
    goldReadBeforeFreeze=$false; source='canonical B0 frozen outputs only; Gold used only for offline target labels'
    persistentOmissionCount=7; targets=$targetRows; analogueAudit=$analogueRows
    roleOntology=[ordered]@{
        allowedRoles=$allowedRoles
        targetSemanticClasses=@(
            [ordered]@{ semanticClass='REGION_LABEL'; plausibleExistingRole='SUBSECTION'; evidence='DOC-0252 B0 analogues including Africa, Asia and the Pacific, CIS, Latin America and Western Asia are emitted as SUBSECTION' },
            [ordered]@{ semanticClass='PROGRAM_LABEL'; plausibleExistingRole='SUBSECTION'; evidence='DOC-0252 B0 analogue Eurostat–OECD PPP Program is emitted as SUBSECTION' },
            [ordered]@{ semanticClass='TOPIC_LABEL'; plausibleExistingRole='CONTENT_HEADING or OTHER_STRUCTURAL_LABEL'; evidence='existing task vocabulary maps non-level-specific semantic labels to heading roles' }
        )
        conclusion='ROLE_ONTOLOGY_SUFFICIENT'
    }
    jointTaskLoad=[ordered]@{
        roleRequiredBySchema=$true; roleRequiredByParser=$true; roleUsedByExactBinder=$false
        roleUsedByHardValidator=$false; roleUsedByHeadingIdentityScorer=$false; roleCurrentlyMeasured=$false
        modelRoleError='NOT_MEASURED'
        notes=@(
            'B0 schema requires role and parser rejects a heading without an allowed role.',
            'SemanticTextExactBinder resolves source alias and verbatim text; role is carried into the bound DTO but does not affect matching.',
            'ReasoningHardInvariantValidator checks source/span/text invariants, not semantic role.',
            'Exact heading identity scoring compares sourceId plus UTF-16 start/end and does not use role.',
            'Role is consumed later by materialization/projection, so it is a carried pipeline field rather than a discovery identity key.'
        )
    }
    causalDecision='DISCOVERY_ROLE_COUPLING_TEST_JUSTIFIED'
    decisionBasis=@(
        'Existing roles represent the closest positive analogues; no ontology gap was found.',
        'The B0 model-facing schema makes role mandatory in the same response as heading discovery.',
        'Role correctness is not the current measured residual owner: MODEL_ROLE_ERROR remains NOT_MEASURED and heading identity scoring is role-independent.',
        'This joint discovery/role coupling has not been independently isolated by a discovery-only then role-only experiment.'
    )
    i6Authorized=$true; runtimeGoldLeakage=$false
}

$decision = [ordered]@{
    schemaVersion='a99-a99-e4-role-pressure-decision-v1'; experimentId='A99-E4'; behavioralParent='B0@76c4e01'
    modelCalls=0; providerCalls=0; goldReadBeforeFreeze=$false
    terminalClassification='DISCOVERY_ROLE_COUPLING_TEST_JUSTIFIED'
    roleOntologyConclusion='ROLE_ONTOLOGY_SUFFICIENT'
    i6Authorized=$true
    reason='Existing roles are sufficient, role is mandatory at discovery time, identity scoring does not depend on role, and coupling has not been independently isolated.'
    nextTask='A99-I6 DISCOVERY / ROLE DECOMPOSITION'
}

$utf8 = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $out 'role-pressure.v1.json'), ($rolePressure | ConvertTo-Json -Depth 20), $utf8)
[IO.File]::WriteAllText((Join-Path $out 'decision.v1.json'), ($decision | ConvertTo-Json -Depth 20), $utf8)
Write-Output 'E4_MODEL_CALLS=0'
Write-Output 'E4_PROVIDER_CALLS=0'
Write-Output 'E4_CLASSIFICATION=DISCOVERY_ROLE_COUPLING_TEST_JUSTIFIED'
