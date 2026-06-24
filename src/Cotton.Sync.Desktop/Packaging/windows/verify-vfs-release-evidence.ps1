# SPDX-License-Identifier: MIT
# Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

param(
    [Parameter(Mandatory = $true)]
    [string]$EvidenceDirectory
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $EvidenceDirectory)) {
    throw "VFS release evidence directory was not found: $EvidenceDirectory"
}

$resolvedEvidenceDirectory = (Resolve-Path -LiteralPath $EvidenceDirectory).Path

function Read-EvidenceFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RelativePath
    )

    $path = Join-Path $resolvedEvidenceDirectory $RelativePath
    if (-not (Test-Path -LiteralPath $path)) {
        throw "VFS release evidence file was not found: $RelativePath"
    }

    return Get-Content -LiteralPath $path -Raw
}

function Assert-Contains {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Content,

        [Parameter(Mandatory = $true)]
        [string]$Expected,

        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    if ($Content.IndexOf($Expected, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "$Label did not contain expected text: $Expected"
    }
}

$summary = Read-EvidenceFile -RelativePath "summary.txt"
Assert-Contains -Content $summary -Expected "Installed app: captured:" -Label "summary.txt"
Assert-Contains -Content $summary -Expected "Autostart registry: captured:" -Label "summary.txt"
Assert-Contains -Content $summary -Expected "Cotton process windows: captured:" -Label "summary.txt"
Assert-Contains -Content $summary -Expected "Cloud Files Explorer registrations: captured:" -Label "summary.txt"
Assert-Contains -Content $summary -Expected "Local root entries: captured:" -Label "summary.txt"
Assert-Contains -Content $summary -Expected "Log tails: captured" -Label "summary.txt"
Assert-Contains -Content $summary -Expected "Installed self-test: exitCode=0;" -Label "summary.txt"
Assert-Contains -Content $summary -Expected "Diagnostics export: exitCode=0;" -Label "summary.txt"

if ($summary.IndexOf("failed:", [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
    throw "VFS release evidence summary contains a failed capture."
}

$installedApp = Read-EvidenceFile -RelativePath "installed-app.txt"
Assert-Contains -Content $installedApp -Expected "ProductVersion" -Label "installed-app.txt"
Assert-Contains -Content $installedApp -Expected "FileVersion" -Label "installed-app.txt"
Assert-Contains -Content $installedApp -Expected "Sha256" -Label "installed-app.txt"

$registryRun = Read-EvidenceFile -RelativePath "registry-run.txt"
Assert-Contains -Content $registryRun -Expected "Cotton Sync" -Label "registry-run.txt"
Assert-Contains -Content $registryRun -Expected "--start-minimized" -Label "registry-run.txt"

$processWindows = Read-EvidenceFile -RelativePath "process-windows.txt"
if ($null -eq $processWindows) {
    throw "process-windows.txt could not be read."
}

$registryExplorer = Read-EvidenceFile -RelativePath "registry-cloud-files-explorer.txt"
Assert-Contains -Content $registryExplorer -Expected "MatchCount:" -Label "registry-cloud-files-explorer.txt"

$localRootEntries = Read-EvidenceFile -RelativePath "local-root-entries.csv"
Assert-Contains -Content $localRootEntries -Expected '"RelativePath","FullPath","Exists","Attributes","Length","LastWriteTimeUtc"' -Label "local-root-entries.csv"

$selfTest = Read-EvidenceFile -RelativePath "self-test.stdout.log"
Assert-Contains -Content $selfTest -Expected "Result: passed" -Label "self-test.stdout.log"

$diagnosticsExport = Read-EvidenceFile -RelativePath "diagnostics-export.stdout.log"
Assert-Contains -Content $diagnosticsExport -Expected "Diagnostics" -Label "diagnostics-export.stdout.log"

Write-Host "Verified VFS release evidence bundle: $resolvedEvidenceDirectory"
