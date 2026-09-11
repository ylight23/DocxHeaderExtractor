param([string]$RepoRoot = (Get-Location).Path)
$ErrorActionPreference = 'Stop'
$RepoRoot = [IO.Path]::GetFullPath($RepoRoot)
$closure = Join-Path $RepoRoot 'eval/a99-closed-loop/closure/c1'
$r2 = Join-Path $RepoRoot 'eval/a99-closed-loop/research-r2'
New-Item -ItemType Directory -Force -Path $closure,$r2 | Out-Null
function J([string]$path) { Get-Content -LiteralPath (Join-Path $RepoRoot $path) -Raw | ConvertFrom-Json }
function W([string]$path, $value) { $utf8 = New-Object Text.UTF8Encoding($false); [IO.File]::WriteAllText((Join-Path $RepoRoot $path), ($value | ConvertTo-Json -Depth 30), $utf8) }
function M($rows) {
    $tp = @($rows | Measure-Object tp -Sum).Sum; $fp = @($rows | Measure-Object fp -Sum).Sum; $fn = @($rows | Measure-Object fn -Sum).Sum
    $p = if ($tp + $fp -eq 0) { 0 } else { [double]$tp / ($tp + $fp) }; $r = if ($tp + $fn -eq 0) { 0 } else { [double]$tp / ($tp + $fn) }; $f = if ($p + $r -eq 0) { 0 } else { 2 * $p * $r / ($p + $r) }
    [ordered]@{ gold = @($rows | Measure-Object gold -Sum).Sum; tp=$tp; fp=$fp; fn=$fn; precision=$p; recall=$r; f1=$f; systemLoss=@($rows | Measure-Object systemLoss -Sum).Sum }
}

$b0Root = 'eval/a99-closed-loop/semantic-text-generalization'
$manifest = J "$b0Root/manifest.v1.json"
$repeatSummary = J "$b0Root/repeat-summary.v1.json"
$persistent = J "$b0Root/persistent-errors.v1.json"
$sampleFreeze = J "$b0Root/DOC-0001/r1/freeze.v1.json"
$canonicalCommit = (& git -C $RepoRoot rev-parse 76c4e01).Trim()
$perCell = @($repeatSummary.documents | ForEach-Object { $_.repeats | ForEach-Object { $_ } })
$microByRepeat = @('r1','r2','r3' | ForEach-Object { $repeat = $_; [ordered]@{ repeat=$repeat; metrics=(M @($perCell | Where-Object { $_.repeat -eq $repeat })) } })
$goldTotal = @($perCell | Select-Object -First 1 | ForEach-Object { $null })

$canonical = [ordered]@{
    schemaVersion='a99-a99-c1-canonical-authority-v1'; phase='A99-C1'; status='FROZEN_TERMINAL_AUTHORITY'
    behavioralParent='B0@76c4e01'; canonicalCommit=$canonicalCommit; artifactAuthorityCommit='76c4e01'
    model='qwen/qwen3.7-flash'; provider='OpenRouter'; actualProvider='Alibaba'
    semanticContract=[ordered]@{ version=$manifest.semanticContractVersion; hash=$manifest.semanticContractHash; promptHash=$sampleFreeze.promptHash; schemaHash=$sampleFreeze.schemaHash }
    binder=[ordered]@{ version='deterministic-exact-utf16-binder-v1'; hash='deterministic UTF-16 exact binder over sourceAlias + verbatimText' }
    validator=[ordered]@{ version='ReasoningProposalMaterializer-validator-v1'; hash='ReasoningProposalMaterializer|ReasoningTaskProjection|validator-v1' }
    cohort=[ordered]@{ documents=@($manifest.selectedCohort | ForEach-Object { $_.documentId }); documentCount=5; repeats=3; cells=15; goldOccurrences=459; authority='strict-gold-occurrence-v1' }
    goldAuthority=[ordered]@{ root='eval/a99-closed-loop/strict-gold-occurrence-v1'; readBeforeFreeze=$false; runtimeLeakage=$false; exactOccurrenceRows=459 }
    aggregate=[ordered]@{ gold=459; tp=428; fp=32; fn=31; precision=0.9304347826; recall=0.9324618736; f1=0.9314472252; systemLoss=0; bindFailure=0 }
    perCell=$perCell; perRepeat=$microByRepeat
    sourceArtifact=$b0Root; sourceManifestHash=(Get-FileHash (Join-Path $RepoRoot "$b0Root/manifest.v1.json") -Algorithm SHA256).Hash.ToLowerInvariant()
    canonicalSelection='B0 actual frozen run at 76c4e01; prior 425/22/34 lineage is retained only as historical evidence and is not authority'
}
W 'eval/a99-closed-loop/closure/c1/canonical-authority.v1.json' $canonical

$causalLedger = @(
    [ordered]@{ id='I1'; family='GENERIC_OMISSION_REVIEW'; hypothesis='A generic second review pass would recover stable model omissions'; target='7 persistent omissions'; result='No persistent target recovery'; decision='REJECTED'; reopenCondition='REOPEN_ONLY_WITH_NEW_CAUSAL_EVIDENCE: independent residual class showing omission review is the owner' },
    [ordered]@{ id='I2'; family='DUPLICATE_IDENTITY'; hypothesis='Duplicate identity/disambiguation is suppressing target headings'; target='stable duplicate/identity residuals'; result='No target recovery under deterministic disambiguation'; decision='REJECTED'; reopenCondition='REOPEN_ONLY_WITH_NEW_CAUSAL_EVIDENCE: new generic ambiguity mechanism not present in the audited cases' },
    [ordered]@{ id='I3'; family='LOSSLESS_SOURCE_BOUNDARIES'; hypothesis='Generic lossless boundaries improve semantic discovery'; target='7 persistent omissions'; result='Persistent omissions unchanged'; decision='REJECTED'; reopenCondition='REOPEN_ONLY_WITH_NEW_CAUSAL_EVIDENCE: independent evidence that missing boundaries, not representation syntax, causes the residual' },
    [ordered]@{ id='I4'; family='MODEL_CAPABILITY_QWEN35_9B'; hypothesis='A model capability swap removes the residual'; target='paired B0 capability'; result='No generic promotion evidence / challenger path closed'; decision='CLOSED'; reopenCondition='REOPEN_ONLY_WITH_NEW_CAUSAL_EVIDENCE: challenger specifically addresses the observed failure and passes pre-registered paired gate' },
    [ordered]@{ id='I5'; family='SEMANTIC_CONTRAST_CONTRACT'; hypothesis='Generic occurrence-level semantic contrast wording recovers omissions'; target='7 persistent omissions'; result='No target recovery; prompt reverted'; decision='REVERTED'; reopenCondition='REOPEN_ONLY_WITH_NEW_CAUSAL_EVIDENCE: distinct causal mechanism beyond another wording/example variant' },
    [ordered]@{ id='E3'; family='OCCURRENCE_CONTEXT'; hypothesis='Generic occurrence context/discriminator resolves the residual'; target='same-lexeme and sibling contrasts'; result='NO_GENERIC_OCCURRENCE_DISCRIMINATOR'; decision='CLOSED_NOT_AUTHORIZED'; reopenCondition='REOPEN_ONLY_WITH_NEW_CAUSAL_EVIDENCE: new source fact demonstrably separates positive and negative occurrences' },
    [ordered]@{ id='I6'; family='DISCOVERY_ROLE_DECOMPOSITION'; hypothesis='Mandatory role classification suppresses heading discovery'; target='7 persistent omissions'; result='Pass A 423/105/36 F1 .857142857; persistent misses 7/7; recovery 0'; decision='REVERTED'; reopenCondition='REOPEN_ONLY_WITH_NEW_CAUSAL_EVIDENCE: independent evidence that role pressure, rather than task decomposition itself, is the owner' },
    [ordered]@{ id='V1'; family='STRUCTURAL_CONTEXT_ENRICHMENT'; hypothesis='Objective structural facts improve discovery'; target='stable semantic residuals'; result='Provider-blocked/incomplete measurement; no architecture promotion'; decision='CLOSED_PROVIDER_BLOCKED'; reopenCondition='REOPEN_ONLY_WITH_NEW_CAUSAL_EVIDENCE: new generic residual class plus deterministic absent source fact' },
    [ordered]@{ id='VLM'; family='VLM_VISUAL_PATH'; hypothesis='Visual/layout evidence is necessary for unresolved heading truth'; target='visual capability ceiling'; result='Not admitted as current semantic-text intervention owner'; decision='CLOSED_FOR_GENERATION'; reopenCondition='REOPEN_ONLY_WITH_NEW_CAUSAL_EVIDENCE: independent cases prove text representation cannot resolve heading truth' },
    [ordered]@{ id='CANDIDATE'; family='CANDIDATE_FIRST'; hypothesis='Harness-generated candidates can recover recall'; target='semantic discovery'; result='Not an admissible architecture for this causal lane'; decision='CLOSED_PROHIBITED'; reopenCondition='REOPEN_ONLY_WITH_NEW_CAUSAL_EVIDENCE: explicit authorization and evidence that candidate generation is not hiding the target class' },
    [ordered]@{ id='OFFSET'; family='NUMERIC_OFFSET_CONTRACT'; hypothesis='Model-generated numeric offsets are a reliable discovery contract'; target='exact binding'; result='Superseded by semantic text + deterministic binder'; decision='REJECTED_AS_CONTRACT'; reopenCondition='REOPEN_ONLY_WITH_NEW_CAUSAL_EVIDENCE: independent proof that numeric coordinates improve discovery without binding regression' }
)
W 'eval/a99-closed-loop/closure/c1/causal-ledger.v1.json' ([ordered]@{ schemaVersion='a99-a99-c1-causal-ledger-v1'; experimentGeneration='A99-current-generation'; behavioralParent='B0@76c4e01'; modelCalls=0; providerCalls=0; entries=$causalLedger })

$gate = [ordered]@{
    schemaVersion='a99-a99-c1-new-evidence-admission-gate-v1'; status='FROZEN_FOR_FUTURE_RESEARCH'; modelCalls=0; providerCalls=0; goldReadBeforeFreeze=$false
    admissionRule='A future experiment may start only when every criterion is true.'
    requiredCriteria=@('targets an observed residual','identifies one causal owner','evidence is distinct from rejected mechanism families','changes exactly one causal variable','is generic across documents','requires no Gold-derived runtime rules','has a measurable pre-registered target metric','freezes KEEP/REVERT gate before inference')
    insufficientEvidence=@('another wording variant','another prompt example','larger context window','another boundary syntax','another generic second pass','another random model','higher aggregate F1 on unrelated cases','manual observation that it looks better')
    prohibited=@('Gold-derived runtime rules','document-specific fixes','silent authority changes','reopening I1-I6 without new causal evidence','production/default behavior changes during evidence generation')
}
W 'eval/a99-closed-loop/closure/c1/new-evidence-gate.v1.json' $gate

$r2Manifest = [ordered]@{
    schemaVersion='a99-a99-r2-independent-residual-generalization-study-manifest-v1'; studyId='A99-R2'; status='PREPARATION_ONLY_NOT_EXECUTED'
    parentGeneration='A99-C1'; behavioralParent='B0@76c4e01'; modelCalls=0; providerCalls=0; runtimeChanges=0; goldReadBeforeFreeze=$false
    purpose='Determine whether the seven-case failure represents a generic semantic class across unseen documents or is cohort-specific.'
    notAnIntervention=$true; noModelChanges=$true; noPromptChanges=$true; noTuning=$true
    documentSelection=[ordered]@{ criteriaFrozenBeforeLabels=$true; documentLevelSampling=$true; independentOfCurrentGoldErrors=$true; excludeCurrentDevCohort=$true; doNotSearchKnownTargetStrings=$true; knownTargetStrings=@('Africa','Western Asia','Eurostat–OECD PPP Program'); selectionBasis='fixed document metadata/domain/length/structure strata, sampled before heading labels are inspected'; selectedDocuments=@() }
    goldProtocol=[ordered]@{ humanReviewRequired=$true; exactSourceBackedSpans=$true; independentDoubleReview=$true; adjudicationForDisagreement=$true; semanticClassAnnotation=@('heading presence','semantic family','source occurrence identity','exact UTF-16 span','role if independently authorized') }
    blindness=[ordered]@{ annotatorsBlindToModelOutputs=$true; annotatorsBlindToCurrentSevenCases=$true; runtimeGoldUnavailable=$true; analysisUnblindedOnlyAfterFreeze=$true }
    measurement=[ordered]@{ primary='prevalence and reproducibility of the residual semantic class'; secondary=@('exact occurrence agreement','semantic-family agreement','text-only resolvability','negative opportunity rate'); noOptimizationMetric=$true; noKeepRevertUntilNewGeneration=$true }
    admissionPrerequisite='A99-C1 new-evidence admission gate must pass before any intervention generation is proposed.'
}
W 'eval/a99-closed-loop/research-r2/manifest.v1.json' $r2Manifest

$summary = [ordered]@{
    schemaVersion='a99-a99-c1-terminal-summary-v1'; phase='A99-C1'; terminalStatus='A99_DEV_TARGET_NOT_REACHED'; optimizationLoop='CLOSED'; causalSpaceStatus='NO_EVIDENCE_BACKED_INTERVENTION_REMAINS'
    canonicalB0=[ordered]@{ tp=428; fp=32; fn=31; precision=0.9304347826; recall=0.9324618736; f1=0.931447225 }
    systemLoss=0; bindFailure=0; releaseCandidate='HOLD'; acceptedArchitecture='B0'; acceptedModel='qwen/qwen3.7-flash'; persistentTargetOmissions=7
    roleMeasurement='NOT_MEASURED'; hierarchyMeasurement='NOT_MEASURED'; nextResearchPhase='A99-R2_INDEPENDENT_RESIDUAL_GENERALIZATION_STUDY'
    modelCalls=0; providerCalls=0; goldReadBeforeFreeze=$false; currentHead=(& git -C $RepoRoot rev-parse HEAD).Trim(); remoteExpectedHead='213475a17cb498df19114dfd3f6ebdfe55e706d3'
    authorityArtifact='eval/a99-closed-loop/closure/c1/canonical-authority.v1.json'; causalLedger='eval/a99-closed-loop/closure/c1/causal-ledger.v1.json'; newEvidenceGate='eval/a99-closed-loop/closure/c1/new-evidence-gate.v1.json'; researchProtocol='eval/a99-closed-loop/research-r2/manifest.v1.json'
}
W 'eval/a99-closed-loop/closure/c1/summary.v1.json' $summary
Write-Output 'C1_MODEL_CALLS=0'; Write-Output 'C1_PROVIDER_CALLS=0'; Write-Output 'C1_STATUS=A99_DEV_TARGET_NOT_REACHED'; Write-Output 'R2_PROTOCOL=PREPARATION_ONLY'
