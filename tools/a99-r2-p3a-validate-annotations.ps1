param(
    [Parameter(Mandatory = $true)][string]$AnnotationDirectory,
    [string]$PacketDirectory = 'eval/a99-closed-loop/research-r2/p3-human-gold/packets',
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath (git rev-parse --show-toplevel)

function Read-Json([string]$Path) { Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
function Sha256Text([string]$Value) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value)))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}
function AsArray($Value) { if ($null -eq $Value) { return @() }; return @($Value) }

$families = @('DOCUMENT_TITLE','PART_CHAPTER','SECTION','SUBSECTION','REGION_OR_GEOGRAPHIC_LABEL','PROGRAM_OR_TOPIC_LABEL','TABLE_OR_LOCAL_LABEL','NAVIGATION_OR_AGENDA','ANNEX','CAPTION','OTHER_STRUCTURAL_LABEL','UNKNOWN')
$forbidden = @('prediction','predictions','residual','residuals','prompt','score','modeloutput','modeloutputs','goldlabel','goldlabels','answer','answers','currentdev','b0output','b0outputs')
$packets = @{}
foreach ($path in Get-ChildItem -LiteralPath $PacketDirectory -Filter '*.source.v1.json' -File) {
    $packet = Read-Json $path
    $packets[[string]$packet.documentId] = $packet
}

function Assert-Clean([object]$Value, [string]$Path) {
    if ($Value -is [string]) { return }
    if ($Value -is [System.Collections.IDictionary]) {
        foreach ($key in $Value.Keys) {
            $lower = ([string]$key).ToLowerInvariant()
            if ($forbidden -contains $lower) { throw "FORBIDDEN_ANNOTATION_PROPERTY $Path.$key" }
            Assert-Clean $Value[$key] "$Path.$key"
        }
        return
    }
    if ($Value -is [System.Collections.IEnumerable]) { foreach ($item in $Value) { Assert-Clean $item "$Path[]" }; return }
    if ($null -ne $Value -and $Value.PSObject.Properties.Count -gt 0) {
        foreach ($property in $Value.PSObject.Properties) {
            $lower = $property.Name.ToLowerInvariant()
            if ($forbidden -contains $lower) { throw "FORBIDDEN_ANNOTATION_PROPERTY $Path.$($property.Name)" }
            Assert-Clean $property.Value "$Path.$($property.Name)"
        }
        return
    }
}

$files = @(Get-ChildItem -LiteralPath $AnnotationDirectory -Filter '*.annotation.v1.json' -File | Sort-Object Name)
$reports = @()
$errors = @()
foreach ($file in $files) {
    try {
        $root = Read-Json $file.FullName
        Assert-Clean $root '$'
        $id = [string]$root.documentId
        if (-not $packets.ContainsKey($id)) { throw "unknown documentId $id" }
        if ([string]$root.studyId -ne 'A99-R2' -or [string]$root.pilotId -ne 'R2-PILOT-8') { throw 'study or pilot mismatch' }
        if ([string]$root.annotatorId -notin @('A','B')) { throw 'annotatorId must be A or B' }
        if ([string]$root.sourceSha256 -ne ([string]$packets[$id].sourceSha256)) { throw 'source SHA mismatch' }
        $rows = @(AsArray $root.rows)
        $ordinals = @()
        foreach ($row in $rows) {
            if ([string]$row.documentId -ne $id) { throw 'row documentId mismatch' }
            if ([string]$row.semanticMembership -ne 'HEADING') { throw 'semanticMembership must be HEADING' }
            if ([string]$row.semanticFamily -notin $families) { throw "unknown semanticFamily $($row.semanticFamily)" }
            if ([string]$row.confidence -notin @('CERTAIN','REVIEW_REQUIRED')) { throw 'invalid confidence' }
            $ordinal = [int]$row.humanHeadingOrdinal
            if ($ordinal -lt 1 -or $ordinals -contains $ordinal) { throw 'duplicate or invalid humanHeadingOrdinal' }
            $ordinals += $ordinal
            if ($null -ne $row.sourceOccurrenceHint -and [string]$row.sourceOccurrenceHint -ne '') {
                $occ = @($packets[$id].occurrences | Where-Object { [string]$_.sourceOccurrenceId -eq [string]$row.sourceOccurrenceHint })
                if ($occ.Count -ne 1) { throw "unknown sourceOccurrenceHint $($row.sourceOccurrenceHint)" }
                $literal = [string]$occ[0].literalSourceText
                if (-not $literal.Contains([string]$row.documentTitleText, [StringComparison]::Ordinal)) {
                    throw 'documentTitleText is not an exact substring of hinted source occurrence'
                }
            }
            foreach ($optional in @('utf16Start','utf16End')) {
                if ($row.PSObject.Properties.Name -contains $optional) {
                    if ($row.$optional -isnot [int] -or [int]$row.$optional -lt 0) { throw "$optional must be a non-negative integer" }
                }
            }
        }
        if ($ordinals.Count -gt 1) {
            $expected = 1..$ordinals.Count
            if (-not (@($ordinals | Sort-Object) -ceq @($expected))) { throw 'rows are not sorted contiguously by humanHeadingOrdinal' }
        }
        $reports += [ordered]@{ file = $file.Name; documentId = $id; annotatorId = [string]$root.annotatorId; rowCount = $rows.Count; status = if ($rows.Count -eq 0) { 'EMPTY_PENDING_HUMAN' } else { 'VALID' } }
    } catch {
        $errors += [ordered]@{ file = $file.Name; error = $_.Exception.Message }
    }
}

$result = [ordered]@{
    schemaVersion = 'a99-r2-p3a-annotation-validation-v1'
    annotationDirectory = $AnnotationDirectory
    packetDirectory = $PacketDirectory
    files = $reports
    errors = $errors
    valid = ($errors.Count -eq 0)
    semanticDecisionMadeByTool = $false
    modelCalls = 0
    providerCalls = 0
}
$json = $result | ConvertTo-Json -Depth 20
if ($OutputPath) { $json | Set-Content -LiteralPath $OutputPath -Encoding UTF8 }
else { $json }
if ($errors.Count -gt 0) { exit 1 }
