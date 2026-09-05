# HL2 Publication

This publication records the final evaluation-only HL2 occurrence measurement.

```ini
baseRevision = eb528604dba6d2ce60df59cf3cf84fe6c82bcdc3
executionRevision = 224f954ac2b246e613322353488c02559bca5231
measurementArtifactCommit = d6ce5d0

referenceOccurrences = 4300
exactSourceId = 3271
exactSpan = 0
exactOrdinalText = 0
uniqueExactText = 642
ambiguous = 60
notFound = 327

trustedDocuments = 3
trustedGroups = 3
provider = OpenRouter
model = qwen/qwen3.5-9b
repeats = 3
providerCalls = 314

sourceStructuralDocuments = 12
sourceStructuralOccurrences = 1672
gapsBefore = 124
gapsResolved = 1
gapsRemaining = 123

reviewP0Packets = 40
reviewP1Packets = 2
reviewPackets = 42
humanReviewPending = YES
newHumanKeysImported = 0

fullSuite = 1052/1051/1/0
knownFailures = N15
newFailures = 0
productionBehaviorChanged = NO
accuracy99Claim = NOT_MEASURED
harnessLiftStatus = MEASURED_ON_TRUSTED_SUBSET_WITH_LIMITATIONS
```

The 314 provider calls are the successful final trusted-subset pass at the execution revision. An earlier discarded pass failed while exporting artifacts; it is not part of this publication. Human review remains pending, and no source packets are treated as gold automatically. The publication revision is the Git commit containing this file.
