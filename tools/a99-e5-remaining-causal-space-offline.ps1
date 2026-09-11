param([string]$RepoRoot = (Get-Location).Path)
$ErrorActionPreference = 'Stop'
$RepoRoot = [IO.Path]::GetFullPath($RepoRoot)
$out = Join-Path $RepoRoot 'eval/a99-closed-loop/remaining-causal-space-audit/e5'
New-Item -ItemType Directory -Force -Path $out | Out-Null
function J([string]$p) { Get-Content -LiteralPath (Join-Path $RepoRoot $p) -Raw | ConvertFrom-Json }
$i6 = J 'eval/a99-closed-loop/task-decomposition/i6/summary.v1.json'
$e4 = J 'eval/a99-closed-loop/task-decomposition/e4/decision.v1.json'
$families = @(
    [ordered]@{ family='MODEL_TASK_DECOMPOSITION'; evidence='A99-I6'; terminal='REJECTED'; result='7 persistent Pass-A omissions remained; Pass-A micro F1=.857142857 and FP=105 versus B0 F1=.931447225 and FP=32' },
    [ordered]@{ family='SEMANTIC_CONTRAST_CONTRACT'; evidence='A99-I5'; terminal='REVERTED'; result='No persistent target recovery; contract reverted' },
    [ordered]@{ family='GENERIC_OMISSION_REVIEW'; evidence='A99-I1'; terminal='REJECTED'; result='No evidence-backed recall gain' },
    [ordered]@{ family='LOSSLESS_SOURCE_BOUNDARIES'; evidence='A99-I3'; terminal='REJECTED'; result='Persistent omissions remained unchanged' },
    [ordered]@{ family='DUPLICATE_IDENTITY'; evidence='A99-I2'; terminal='REJECTED'; result='No target recovery under deterministic duplicate disambiguation' },
    [ordered]@{ family='OCCURRENCE_CONTEXT'; evidence='A99-E3'; terminal='CLOSED'; result='NO_GENERIC_OCCURRENCE_DISCRIMINATOR; not authorized' },
    [ordered]@{ family='MODEL_CAPABILITY_SWAP'; evidence='A99-I4'; terminal='CLOSED'; result='No evidence to promote a second model as a generic fix' },
    [ordered]@{ family='STRUCTURAL_CONTEXT_ENRICHMENT'; evidence='A99-V1'; terminal='CLOSED'; result='Provider-blocked cells prevented measurement; no architecture change authorized' },
    [ordered]@{ family='VLM_OR_VISUAL_RECOVERY'; evidence='A99-VLM'; terminal='CLOSED'; result='Not a remaining semantic-text intervention for the current residual owner' }
)
$untested = @(
    [ordered]@{ candidate='PROMPT_WORDING_VARIANTS'; status='NO_EVIDENCE_BACKED_OPENING'; reason='Previous contract interventions are already closed and wording-only search would be random-walk' },
    [ordered]@{ candidate='SECOND_MODEL_SWAP'; status='NO_EVIDENCE_BACKED_OPENING'; reason='I4 did not establish a generic model replacement gate' },
    [ordered]@{ candidate='GENERIC_RECALL_OR_SELF_CRITIQUE_PASS'; status='NO_EVIDENCE_BACKED_OPENING'; reason='Would reopen rejected omission-review/multipass family without a new mechanism' },
    [ordered]@{ candidate='DOCUMENT_SPECIFIC_RULES_OR_GOLD_DERIVED_FILTERS'; status='PROHIBITED'; reason='Violates no-document-specific-fixes and Gold firewall' }
)
$matrix = [ordered]@{
    schemaVersion='a99-a99-e5-causal-space-matrix-v1'; experimentId='A99-E5'; status='COMPLETE_OFFLINE'; behavioralParent='B0@76c4e01'
    modelCalls=0; providerCalls=0; goldReadBeforeFreeze=$false; e4Decision=$e4.terminalClassification; i6Decision=$i6.classification
    testedFamilies=$families; remainingCandidates=$untested
    conclusion='NO_EVIDENCE_BACKED_INTERVENTION_REMAINS'
}
$decision = [ordered]@{
    schemaVersion='a99-a99-e5-remaining-causal-space-decision-v1'; experimentId='A99-E5'; status='TERMINAL_OFFLINE'
    behavioralParent='B0@76c4e01'; modelCalls=0; providerCalls=0; goldReadBeforeFreeze=$false
    terminalClassification='NO_EVIDENCE_BACKED_INTERVENTION_REMAINS'
    evidence=@('I6 failed its pre-registered keep gate: target recovery=0 and persistent target misses remained 7/7.', 'The major generic families in scope are either rejected, reverted, closed, or provider-blocked without authorization to change architecture.', 'No remaining candidate is both generic, untested, and supported by a distinct causal mechanism in the current evidence.')
    action='STOP_RANDOM_WALK'; releaseCandidate='HOLD'; nextAllowedWork='offline residual/authority audit or explicitly authorized new causal hypothesis'
}
$utf8 = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $out 'matrix.v1.json'), ($matrix | ConvertTo-Json -Depth 20), $utf8)
[IO.File]::WriteAllText((Join-Path $out 'decision.v1.json'), ($decision | ConvertTo-Json -Depth 20), $utf8)
Write-Output 'E5_MODEL_CALLS=0'
Write-Output 'E5_PROVIDER_CALLS=0'
Write-Output 'E5_CLASSIFICATION=NO_EVIDENCE_BACKED_INTERVENTION_REMAINS'
