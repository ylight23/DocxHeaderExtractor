[CmdletBinding()]
param(
    [string]$PacketPath,
    [string]$OutputPath,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-Field {
    param([object]$Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties | Where-Object { $_.Name -ieq $Name } | Select-Object -First 1
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Find-LevelField {
    param([object]$Object, [string]$Path = '$')
    if ($null -eq $Object) { return @() }
    $hits = [System.Collections.Generic.List[string]]::new()
    if ($Object -is [System.Collections.IEnumerable] -and -not ($Object -is [string])) {
        $index = 0
        foreach ($item in $Object) {
            foreach ($hit in (Find-LevelField -Object $item -Path "$Path[$index]")) { $hits.Add($hit) }
            $index++
        }
        return @($hits)
    }
    $properties = $Object.PSObject.Properties
    foreach ($property in $properties) {
        $propertyPath = "$Path.$($property.Name)"
        if ($property.Name -ieq 'level') { $hits.Add($propertyPath); continue }
        foreach ($hit in (Find-LevelField -Object $property.Value -Path $propertyPath)) { $hits.Add($hit) }
    }
    return @($hits)
}

function Validate-CanonicalPacket {
    param([object]$Packet)

    $errors = [System.Collections.Generic.List[string]]::new()
    $warnings = [System.Collections.Generic.List[string]]::new()
    $levelFields = @(Find-LevelField -Object $Packet)
    foreach ($field in $levelFields) { $errors.Add("REVIEWER_LEVEL_FIELD:$field") }

    $status = [string](Get-Field $Packet 'status')
    $occurrences = @((Get-Field $Packet 'headingOccurrences'))
    $adjudication = Get-Field $Packet 'adjudication'
    $assignments = @((Get-Field $adjudication 'occurrenceAssignments'))
    $nodes = @((Get-Field $adjudication 'semanticNodes'))
    $edges = @((Get-Field $adjudication 'parentEdges'))

    $occurrenceRefs = @($occurrences | ForEach-Object { [string](Get-Field $_ 'reviewRef') } | Where-Object { $_ })
    $knownOccurrenceSet = @{}
    foreach ($ref in $occurrenceRefs) {
        if ($knownOccurrenceSet.ContainsKey($ref)) { $errors.Add("DUPLICATE_PACKET_OCCURRENCE_REF:$ref") }
        $knownOccurrenceSet[$ref] = $true
    }

    if ($status -eq 'PREPARED_FOR_REVIEW' -and $assignments.Count -eq 0 -and $nodes.Count -eq 0 -and $edges.Count -eq 0 -and $errors.Count -eq 0) {
        return [ordered]@{
            status = 'PREPARED_FOR_REVIEW_NOT_VALIDATED'
            valid = $false
            validationScope = 'empty adjudication is not Gold'
            errorCount = 0
            errors = @()
            warnings = @('NO_ADJUDICATION_PRESENT')
            occurrenceCount = $occurrenceRefs.Count
            assignmentCount = 0
            nodeCount = 0
            parentEdgeCount = 0
            rootCount = 0
            cycleCheck = 'NOT_RUN'
        }
    }

    $assignmentByOccurrence = @{}
    $assignmentByNode = @{}
    $validRoles = @('PRIMARY','REPEAT','CONTINUATION')
    foreach ($assignment in $assignments) {
        $occurrenceRef = [string](Get-Field $assignment 'headingOccurrenceRef')
        $nodeRef = [string](Get-Field $assignment 'semanticNodeRef')
        $role = [string](Get-Field $assignment 'occurrenceRole')
        if (-not $occurrenceRef) { $errors.Add('MISSING_ASSIGNMENT_OCCURRENCE_REF'); continue }
        if (-not $knownOccurrenceSet.ContainsKey($occurrenceRef)) { $errors.Add("UNKNOWN_OCCURRENCE_REF:$occurrenceRef") }
        if ($assignmentByOccurrence.ContainsKey($occurrenceRef)) { $errors.Add("DUPLICATE_OCCURRENCE_ASSIGNMENT:$occurrenceRef") }
        else { $assignmentByOccurrence[$occurrenceRef] = $nodeRef }
        if (-not $nodeRef) { $errors.Add("MISSING_ASSIGNMENT_NODE_REF:$occurrenceRef") }
        if ($validRoles -notcontains $role) { $errors.Add(('INVALID_OCCURRENCE_ROLE:{0}:{1}' -f $occurrenceRef, $role)) }
        if (-not $assignmentByNode.ContainsKey($nodeRef)) { $assignmentByNode[$nodeRef] = [System.Collections.Generic.List[object]]::new() }
        $assignmentByNode[$nodeRef].Add($assignment)
    }

    foreach ($ref in $occurrenceRefs) {
        if (-not $assignmentByOccurrence.ContainsKey($ref)) { $errors.Add("MISSING_OCCURRENCE_ASSIGNMENT:$ref") }
    }
    foreach ($extra in @($assignmentByOccurrence.Keys | Where-Object { -not $knownOccurrenceSet.ContainsKey($_) })) {
        $errors.Add("UNKNOWN_OCCURRENCE_REF:$extra")
    }

    $nodeRefs = @()
    $nodeByRef = @{}
    foreach ($node in $nodes) {
        $nodeRef = [string](Get-Field $node 'semanticNodeRef')
        if (-not $nodeRef) { $errors.Add('MISSING_SEMANTIC_NODE_REF'); continue }
        if ($nodeByRef.ContainsKey($nodeRef)) { $errors.Add("DUPLICATE_SEMANTIC_NODE_REF:$nodeRef") }
        else { $nodeByRef[$nodeRef] = $node; $nodeRefs += $nodeRef }
        if (-not $assignmentByNode.ContainsKey($nodeRef) -or $assignmentByNode[$nodeRef].Count -eq 0) { $errors.Add("EMPTY_SEMANTIC_NODE:$nodeRef") }
        $canonicalRef = [string](Get-Field $node 'canonicalOccurrenceRef')
        if (-not $canonicalRef -or -not $knownOccurrenceSet.ContainsKey($canonicalRef)) { $errors.Add("INVALID_CANONICAL_OCCURRENCE:$nodeRef") }
        elseif (-not $assignmentByOccurrence.ContainsKey($canonicalRef) -or $assignmentByOccurrence[$canonicalRef] -ne $nodeRef) { $errors.Add("CANONICAL_OCCURRENCE_NOT_ASSIGNED_TO_NODE:$nodeRef") }
    }
    foreach ($assignedNode in @($assignmentByNode.Keys | Where-Object { -not $nodeByRef.ContainsKey($_) })) { $errors.Add("UNKNOWN_SEMANTIC_NODE_REF:$assignedNode") }
    foreach ($nodeRef in $nodeRefs) {
        $primaryCount = @($assignmentByNode[$nodeRef] | Where-Object { [string](Get-Field $_ 'occurrenceRole') -eq 'PRIMARY' }).Count
        if ($primaryCount -gt 1) { $errors.Add("MULTIPLE_PRIMARY_OCCURRENCES:$nodeRef") }
    }

    $parentByChild = @{}
    $edgeSet = @{}
    foreach ($edge in $edges) {
        $child = [string](Get-Field $edge 'childSemanticNodeRef')
        $parent = [string](Get-Field $edge 'parentSemanticNodeRef')
        if (-not $child) { $errors.Add('MISSING_PARENT_EDGE_CHILD'); continue }
        if ($parentByChild.ContainsKey($child)) { $errors.Add("MULTIPLE_PARENTS:$child") }
        else { $parentByChild[$child] = $parent }
        $edgeKey = "$child->$parent"
        if ($edgeSet.ContainsKey($edgeKey)) { $errors.Add("DUPLICATE_PARENT_EDGE:$edgeKey") }
        else { $edgeSet[$edgeKey] = $true }
        if (-not $nodeByRef.ContainsKey($child)) { $errors.Add("UNKNOWN_PARENT_EDGE_CHILD:$child") }
        if ($parent -ne 'ROOT' -and -not $nodeByRef.ContainsKey($parent)) { $errors.Add("DANGLING_PARENT:$child->$parent") }
        if ($child -eq $parent) { $errors.Add("SELF_PARENT:$child") }
    }
    foreach ($nodeRef in $nodeRefs) {
        if (-not $parentByChild.ContainsKey($nodeRef)) { $errors.Add("MISSING_PARENT_OR_ROOT:$nodeRef") }
    }

    $cycleCheck = 'PASS'
    foreach ($nodeRef in $nodeRefs) {
        $seen = @{}
        $current = $nodeRef
        while ($current -and $current -ne 'ROOT') {
            if ($seen.ContainsKey($current)) { $errors.Add("PARENT_CYCLE:$nodeRef"); $cycleCheck = 'FAIL'; break }
            $seen[$current] = $true
            if (-not $parentByChild.ContainsKey($current)) { break }
            $current = $parentByChild[$current]
        }
    }
    $rootCount = @($parentByChild.Keys | Where-Object { $parentByChild[$_] -eq 'ROOT' }).Count
    if ($nodeRefs.Count -gt 0 -and $rootCount -eq 0) { $warnings.Add('NO_ROOT_REACHABLE') }
    if ($errors.Count -eq 0 -and $nodeRefs.Count -gt 0 -and $rootCount -gt 0) {
        foreach ($nodeRef in $nodeRefs) {
            $current = $nodeRef
            $visited = @{}
            while ($current -ne 'ROOT' -and $parentByChild.ContainsKey($current) -and -not $visited.ContainsKey($current)) {
                $visited[$current] = $true
                $current = $parentByChild[$current]
            }
            if ($current -ne 'ROOT') { $errors.Add("UNREACHABLE_FROM_ROOT:$nodeRef") }
        }
    }

    $resultStatus = if ($errors.Count -eq 0) { 'VALID' } else { 'INVALID' }
    return [ordered]@{
        status = $resultStatus
        valid = ($resultStatus -eq 'VALID')
        validationScope = 'canonical semantic-node hierarchy adjudication'
        errorCount = $errors.Count
        errors = @($errors)
        warnings = @($warnings)
        occurrenceCount = $occurrenceRefs.Count
        assignmentCount = $assignments.Count
        nodeCount = $nodeRefs.Count
        parentEdgeCount = $edges.Count
        rootCount = $rootCount
        cycleCheck = $cycleCheck
    }
}

function Invoke-SelfTest {
    $valid = @'
    {"status":"REVIEWED","headingOccurrences":[{"reviewRef":"H0001"},{"reviewRef":"H0002"}],"adjudication":{"occurrenceAssignments":[{"headingOccurrenceRef":"H0001","semanticNodeRef":"N001","occurrenceRole":"PRIMARY"},{"headingOccurrenceRef":"H0002","semanticNodeRef":"N002","occurrenceRole":"PRIMARY"}],"semanticNodes":[{"semanticNodeRef":"N001","canonicalOccurrenceRef":"H0001"},{"semanticNodeRef":"N002","canonicalOccurrenceRef":"H0002"}],"parentEdges":[{"childSemanticNodeRef":"N001","parentSemanticNodeRef":"ROOT"},{"childSemanticNodeRef":"N002","parentSemanticNodeRef":"N001"}]}}
'@ | ConvertFrom-Json
    $invalid = @'
    {"status":"REVIEWED","level":1,"headingOccurrences":[{"reviewRef":"H0001"},{"reviewRef":"H0002"}],"adjudication":{"occurrenceAssignments":[{"headingOccurrenceRef":"H0001","semanticNodeRef":"N001","occurrenceRole":"PRIMARY"},{"headingOccurrenceRef":"H0002","semanticNodeRef":"N002","occurrenceRole":"PRIMARY"}],"semanticNodes":[{"semanticNodeRef":"N001","canonicalOccurrenceRef":"H0001"},{"semanticNodeRef":"N002","canonicalOccurrenceRef":"H0002"}],"parentEdges":[{"childSemanticNodeRef":"N001","parentSemanticNodeRef":"N002"},{"childSemanticNodeRef":"N002","parentSemanticNodeRef":"N001"}]}}
'@ | ConvertFrom-Json
    $validResult = Validate-CanonicalPacket -Packet $valid
    $invalidResult = Validate-CanonicalPacket -Packet $invalid
    if ($validResult.status -ne 'VALID') { throw "Self-test valid fixture failed: $($validResult | ConvertTo-Json -Compress)" }
    if ($invalidResult.status -ne 'INVALID' -or $invalidResult.errorCount -lt 2) { throw "Self-test invalid fixture failed: $($invalidResult | ConvertTo-Json -Compress)" }
    Write-Output 'SELF_TEST_PASS'
}

if ($SelfTest) {
    Invoke-SelfTest
    exit 0
}
if (-not $PacketPath) { throw 'PacketPath is required unless -SelfTest is used.' }
$packet = Get-Content -LiteralPath $PacketPath -Raw | ConvertFrom-Json
$result = Validate-CanonicalPacket -Packet $packet
$json = $result | ConvertTo-Json -Depth 20
if ($OutputPath) { $json | Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM }
Write-Output $json
