# Spec coverage: how much of the original's contract this port serves, measured rather than remembered.
#
# The original ships server/docs/api/openapi.json. This script reads it, records the operations it declares, and compares
# them with the operations this port's own specification defines. Run it from the repository root:
#
#   pwsh tools/spec-coverage.ps1
#
# It rewrites contract/upstream/mmp-v1-operations.txt, so a contract that moves shows up as a diff in review rather than as
# a surprise later. The comparison is on METHOD and path, because a path both serve is not the same operation if one reads
# through a body and the other through a query string.

[CmdletBinding()]
param(
    [string] $Upstream = (Join-Path (Split-Path (Get-Location) -Parent) 'munarium/server/docs/api/openapi.json'),
    [string] $OurSpec = 'openapi/munarium.v1.yaml',
    [string] $Record = 'contract/upstream/mmp-v1-operations.txt'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Upstream)) {
    throw "The original's specification was not found at '$Upstream'. Point -Upstream at server/docs/api/openapi.json."
}

$document = Get-Content $Upstream -Raw | ConvertFrom-Json
$declared = [System.Collections.Generic.List[string]]::new()

foreach ($path in $document.paths.PSObject.Properties) {
    foreach ($method in $path.Value.PSObject.Properties) {
        if (@('get', 'post', 'put', 'delete', 'patch') -contains $method.Name) {
            $declared.Add(('{0} {1}' -f $method.Name.ToUpperInvariant(), $path.Name))
        }
    }
}

$declared = @($declared | Sort-Object -Unique)

# The record says where it came from, because a list of names without a source is a rumour.
$header = @(
    '# The operations the original declares, one per line: METHOD path.',
    '#',
    "# Source: $Upstream",
    '# Regenerate with: pwsh tools/spec-coverage.ps1',
    ''
)

New-Item -ItemType Directory -Force -Path (Split-Path $Record -Parent) | Out-Null
Set-Content -Path $Record -Value ($header + $declared) -Encoding utf8

$served = [System.Collections.Generic.List[string]]::new()
$current = ''

foreach ($line in Get-Content $OurSpec) {
    if ($line -match '^  (/[^:]+):') {
        $current = $Matches[1]
    }
    elseif ($current -and $line -match '^    (get|post|put|delete|patch):') {
        $served.Add(('{0} {1}' -f $Matches[1].ToUpperInvariant(), $current))
    }
}

$served = @($served | Sort-Object -Unique)
$covered = @($served | Where-Object { $declared -contains $_ })
$absent = @($declared | Where-Object { $served -notcontains $_ })
$beyond = @($served | Where-Object { $declared -notcontains $_ })

'the original declares {0} operations; this port defines {1}' -f $declared.Count, $served.Count
'covered: {0}' -f $covered.Count
'absent:  {0}' -f $absent.Count
'defined here and not upstream: {0}' -f $beyond.Count

''
'--- absent, first 40 ---'
$absent | Select-Object -First 40

''
'--- defined here and not upstream ---'
$beyond
