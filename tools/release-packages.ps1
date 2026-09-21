# Release packages: what a release publishes, and what has to be inside it, measured rather than assumed.
#
# The release workflow packs and pushes whatever `dotnet pack` produces, so the shape of that output is
# the contract a release makes with its consumers. This script packs the same way the workflow does and
# then reads the packages back: the ids are the ones this repository ships (no test and no tool package
# can slip into a push), every package carries the licence, notice, credits, readme and icon a NuGet
# consumer is entitled to, and every library has a symbol package beside it.
#
# Run it from the repository root or anywhere else:
#
#   pwsh tools/release-packages.ps1                      # pack (building if needed), then check
#   pwsh tools/release-packages.ps1 -NoBuild             # pack without rebuilding, then check
#   pwsh tools/release-packages.ps1 -CheckOnly           # check packages that are already packed
#   pwsh tools/release-packages.ps1 -CheckOnly -Version 0.1.0   # check what a tag would publish

[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Output = 'artifacts/nugetout',
    [string] $Version = '',
    [switch] $NoBuild,
    [switch] $CheckOnly
)

$ErrorActionPreference = 'Stop'

if ($NoBuild -and $CheckOnly) {
    throw '-NoBuild and -CheckOnly say different things: -CheckOnly never packs, so there is nothing for -NoBuild to skip. Pass one of them.'
}

$root = Split-Path $PSScriptRoot -Parent

# The packages a release publishes, and nothing else. A sixth name here is a release that pushes
# something nobody decided to publish, and a missing one is a release that ships an incomplete port.
$expected = @(
    'Munarium.Core',
    'Munarium.Providers',
    'Munarium.Runbooks',
    'Munarium.Store.SharpCoreDb',
    'Munarium.Wire'
)

# What every package has to carry: Apache-2.0 requires the licence and the notice to travel with the
# artifact, CREDITS records the original work this port derives from, and the readme and the icon are
# what a reader of the package page sees.
$required = @('LICENSE', 'NOTICE', 'CREDITS.md', 'README.md', 'icon.png')

$outputPath = Join-Path $root $Output

if (-not $CheckOnly) {
    # Regenerating the packages, so the ones this is about to produce are removed first: a package left
    # in the folder by an earlier pack could otherwise answer for a pack that just failed, and the
    # check would pass on an artifact nobody built. Only the five ids below are touched, so an -Output
    # that doubles as a feed keeps everything else in it.
    if (Test-Path $outputPath) {
        foreach ($id in $expected) {
            Get-ChildItem -Path $outputPath -Filter "$id.*.nupkg" -ErrorAction SilentlyContinue | Remove-Item -Force
            Get-ChildItem -Path $outputPath -Filter "$id.*.snupkg" -ErrorAction SilentlyContinue | Remove-Item -Force
        }
    }

    # --tl:off: the pack's own progress output would otherwise bury the lines this script prints about
    # what it checked.
    $packArgs = @('pack', (Join-Path $root 'Munarium.slnx'), '-c', $Configuration, '-o', $outputPath, '--tl:off')

    if ($NoBuild) {
        $packArgs += '--no-build'
    }

    if ($Version) {
        $packArgs += "-p:Version=$Version"
    }

    Write-Output ('packing: dotnet ' + ($packArgs -join ' '))
    & dotnet @packArgs --nologo -v q

    if ($LASTEXITCODE -ne 0) {
        throw "the pack failed with exit code $LASTEXITCODE, so nothing was checked."
    }
}

if (-not (Test-Path $outputPath)) {
    throw "no package output at '$outputPath' - pack first, or drop -CheckOnly."
}

$packages = @(Get-ChildItem -Path $outputPath -Filter '*.nupkg' | Where-Object { $_.Name -notlike '*.snupkg' })

if ($packages.Count -eq 0) {
    throw "no packages were produced in '$outputPath'."
}

$failures = [System.Collections.Generic.List[string]]::new()
$ids = [System.Collections.Generic.List[string]]::new()

foreach ($package in $packages) {
    # The id and the version are read out of the package's own manifest rather than from its file name:
    # a repository whose ids contain dots cannot split a file name back into the two.
    $archive = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)

    try {
        $entries = $archive.Entries | ForEach-Object { $_.FullName }
        $manifestEntry = $archive.Entries | Where-Object { $_.FullName -like '*.nuspec' } | Select-Object -First 1

        if ($null -eq $manifestEntry) {
            $failures.Add("$($package.Name): the package carries no manifest.")
            continue
        }

        $manifest = [System.IO.StreamReader]::new($manifestEntry.Open()).ReadToEnd()

        if ($manifest -notmatch '<id>([^<]+)</id>') {
            $failures.Add("$($package.Name): the manifest declares no id.")
            continue
        }

        $id = $Matches[1]

        if ($manifest -notmatch '<version>([^<]+)</version>') {
            $failures.Add("${id}: the manifest declares no version.")
            continue
        }

        # Deliberately not named $version: PowerShell variables are case-insensitive, so a local
        # $version would overwrite the -Version parameter and the comparison below would then compare
        # a value with itself and pass for every version.
        $packageVersion = $Matches[1]
        $ids.Add($id)

        if (-not $expected.Contains($id)) {
            $failures.Add("${id}: this is not one of the packages this repository publishes.")
        }

        if ($Version -and $packageVersion -ne $Version) {
            $failures.Add("${id}: packed as $packageVersion, and the release asked for $Version.")
        }

        if (-not (Test-Path (Join-Path $outputPath "$id.$packageVersion.snupkg"))) {
            $failures.Add("${id}: no symbol package beside it, so a consumer's debugger has no symbols to read.")
        }

        foreach ($file in $required) {
            if ($entries -notcontains $file) {
                $failures.Add("${id}: the package does not carry $file.")
            }
        }

        if (-not ($entries | Where-Object { $_ -like 'lib/*/*.dll' })) {
            $failures.Add("${id}: the package carries no library.")
        }

        Write-Output ("checked {0} {1} ({2} KiB)" -f $id, $packageVersion, [math]::Round($package.Length / 1KB))
    }
    finally {
        $archive.Dispose()
    }
}

foreach ($missing in $expected | Where-Object { -not $ids.Contains($_) }) {
    $failures.Add("${missing}: expected a package and none was produced.")
}

if ($failures.Count -gt 0) {
    Write-Output ''
    Write-Output 'the release packages are not what a release may publish:'

    foreach ($failure in $failures) {
        Write-Output "  - $failure"
    }

    exit 1
}

Write-Output ''
Write-Output ("the release packages are complete: {0} libraries, each with its symbols and its licence, notice, credits, readme and icon." -f $packages.Count)

