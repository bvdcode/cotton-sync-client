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

function Assert-DoesNotMatch {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Content,

        [Parameter(Mandatory = $true)]
        [string]$Pattern,

        [Parameter(Mandatory = $true)]
        [string]$Label,

        [Parameter(Mandatory = $true)]
        [string]$FailureMessage
    )

    if ([regex]::IsMatch($Content, $Pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [System.Text.RegularExpressions.RegexOptions]::Multiline)) {
        throw "$Label failed: $FailureMessage"
    }
}

$summary = Read-EvidenceFile -RelativePath "summary.txt"
Assert-Contains -Content $summary -Expected "Installed app: captured:" -Label "summary.txt"
Assert-Contains -Content $summary -Expected "Autostart registry: captured:" -Label "summary.txt"
Assert-Contains -Content $summary -Expected "Cotton process windows: captured:" -Label "summary.txt"
Assert-Contains -Content $summary -Expected "Cloud Files Explorer registrations: captured:" -Label "summary.txt"
Assert-Contains -Content $summary -Expected "Local root entries: captured:" -Label "summary.txt"
Assert-Contains -Content $summary -Expected "Log tails: captured" -Label "summary.txt"
Assert-Contains -Content $summary -Expected "VFS smoke logs: captured:" -Label "summary.txt"
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

$autostartLaunch = Read-EvidenceFile -RelativePath "autostart-launch.txt"
Assert-Contains -Content $autostartLaunch -Expected "Result: passed" -Label "autostart-launch.txt"
Assert-Contains -Content $autostartLaunch -Expected "--start-minimized" -Label "autostart-launch.txt"
Assert-Contains -Content $autostartLaunch -Expected "ObservedForeground: False" -Label "autostart-launch.txt"
Assert-Contains -Content $autostartLaunch -Expected "VisibleWindowCount: 0" -Label "autostart-launch.txt"
Assert-Contains -Content $autostartLaunch -Expected "CleanupRemaining: 0" -Label "autostart-launch.txt"

$processWindows = Read-EvidenceFile -RelativePath "process-windows.txt"
if ($null -eq $processWindows) {
    throw "process-windows.txt could not be read."
}
Assert-DoesNotMatch `
    -Content $processWindows `
    -Pattern "^\s*IsForeground\s*:\s*True\b" `
    -Label "process-windows.txt" `
    -FailureMessage "Cotton Sync became the foreground window during evidence capture."
Assert-DoesNotMatch `
    -Content $processWindows `
    -Pattern "^\s*VisibleWindowCount\s*:\s*[1-9]\d*\b" `
    -Label "process-windows.txt" `
    -FailureMessage "Cotton Sync had visible windows during evidence capture."

$registryExplorer = Read-EvidenceFile -RelativePath "registry-cloud-files-explorer.txt"
Assert-Contains -Content $registryExplorer -Expected "MatchCount:" -Label "registry-cloud-files-explorer.txt"
Assert-DoesNotMatch `
    -Content $registryExplorer `
    -Pattern "^\s*MatchCount:\s*0\b" `
    -Label "registry-cloud-files-explorer.txt" `
    -FailureMessage "No Cloud Files or Explorer registration was captured before uninstall."

$localRootEntries = Read-EvidenceFile -RelativePath "local-root-entries.csv"
Assert-Contains -Content $localRootEntries -Expected '"RelativePath","FullPath","Exists","Attributes","Length","LastWriteTimeUtc"' -Label "local-root-entries.csv"
Assert-Contains -Content $localRootEntries -Expected '"."' -Label "local-root-entries.csv"
$localRootRows = @($localRootEntries | ConvertFrom-Csv)
$localRootRow = $localRootRows | Where-Object { $_.RelativePath -eq "." } | Select-Object -First 1
if ($null -eq $localRootRow -or $localRootRow.Exists -ne "True") {
    throw "local-root-entries.csv did not prove the local root existed during evidence capture."
}

$localRootAttributes = if ($null -eq $localRootRow.Attributes) { "" } else { [string]$localRootRow.Attributes }
if ($localRootAttributes.IndexOf("Directory", [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
    throw "local-root-entries.csv did not prove the local root was a directory during evidence capture."
}

$selfTest = Read-EvidenceFile -RelativePath "self-test.stdout.log"
Assert-Contains -Content $selfTest -Expected "Result: passed" -Label "self-test.stdout.log"

$diagnosticsExport = Read-EvidenceFile -RelativePath "diagnostics-export.stdout.log"
Assert-Contains -Content $diagnosticsExport -Expected "Diagnostics" -Label "diagnostics-export.stdout.log"

$vfsSmoke = Read-EvidenceFile -RelativePath "vfs-smoke\cloud-files-vfs-smoke.stdout.log"
Assert-Contains -Content $vfsSmoke -Expected "Result: passed" -Label "vfs-smoke\cloud-files-vfs-smoke.stdout.log"

$desktopSessionRestore = Read-EvidenceFile -RelativePath "vfs-smoke\phase-desktop-session-restore\cloud-files-vfs-smoke.stdout.log"
Assert-Contains -Content $desktopSessionRestore -Expected "Desktop startup restored the saved signed-in session." -Label "vfs-smoke\phase-desktop-session-restore\cloud-files-vfs-smoke.stdout.log"
Assert-Contains -Content $desktopSessionRestore -Expected "Desktop startup used the remembered server for session restore." -Label "vfs-smoke\phase-desktop-session-restore\cloud-files-vfs-smoke.stdout.log"
Assert-Contains -Content $desktopSessionRestore -Expected "Desktop startup reconnected the persisted Cloud Files sync root." -Label "vfs-smoke\phase-desktop-session-restore\cloud-files-vfs-smoke.stdout.log"
Assert-Contains -Content $desktopSessionRestore -Expected "Result: passed" -Label "vfs-smoke\phase-desktop-session-restore\cloud-files-vfs-smoke.stdout.log"

$shellShareLinkTargets = Read-EvidenceFile -RelativePath "vfs-smoke\phase-shell-share-link-targets\cloud-files-vfs-smoke.stdout.log"
Assert-Contains -Content $shellShareLinkTargets -Expected "Result: passed" -Label "vfs-smoke\phase-shell-share-link-targets\cloud-files-vfs-smoke.stdout.log"

$initialStreamingLogging = Read-EvidenceFile -RelativePath "vfs-smoke\phase-initial-streaming-logging\cloud-files-vfs-smoke.stdout.log"
Assert-Contains -Content $initialStreamingLogging -Expected "Initial VFS streaming run created a large placeholder baseline without per-placeholder activities." -Label "vfs-smoke\phase-initial-streaming-logging\cloud-files-vfs-smoke.stdout.log"
Assert-Contains -Content $initialStreamingLogging -Expected "Initial VFS trace log contains large-run metrics." -Label "vfs-smoke\phase-initial-streaming-logging\cloud-files-vfs-smoke.stdout.log"
Assert-Contains -Content $initialStreamingLogging -Expected "Metric excerpt:" -Label "vfs-smoke\phase-initial-streaming-logging\cloud-files-vfs-smoke.stdout.log"
Assert-Contains -Content $initialStreamingLogging -Expected "placeholders/sec" -Label "vfs-smoke\phase-initial-streaming-logging\cloud-files-vfs-smoke.stdout.log"
Assert-Contains -Content $initialStreamingLogging -Expected "state writes" -Label "vfs-smoke\phase-initial-streaming-logging\cloud-files-vfs-smoke.stdout.log"
Assert-Contains -Content $initialStreamingLogging -Expected "managed heap start=" -Label "vfs-smoke\phase-initial-streaming-logging\cloud-files-vfs-smoke.stdout.log"
Assert-Contains -Content $initialStreamingLogging -Expected "Result: passed" -Label "vfs-smoke\phase-initial-streaming-logging\cloud-files-vfs-smoke.stdout.log"

$steadyStateRepeat = Read-EvidenceFile -RelativePath "vfs-smoke\phase-steady-state-repeat\cloud-files-vfs-smoke.stdout.log"
Assert-Contains -Content $steadyStateRepeat -Expected "Steady-state repeat pass avoided local placeholder-tree scanning." -Label "vfs-smoke\phase-steady-state-repeat\cloud-files-vfs-smoke.stdout.log"
Assert-Contains -Content $steadyStateRepeat -Expected "files=100,000" -Label "vfs-smoke\phase-steady-state-repeat\cloud-files-vfs-smoke.stdout.log"
Assert-Contains -Content $steadyStateRepeat -Expected "fullLocalScans=0" -Label "vfs-smoke\phase-steady-state-repeat\cloud-files-vfs-smoke.stdout.log"
Assert-Contains -Content $steadyStateRepeat -Expected "metadataTreeScans=0" -Label "vfs-smoke\phase-steady-state-repeat\cloud-files-vfs-smoke.stdout.log"
Assert-Contains -Content $steadyStateRepeat -Expected "pathLookups=0" -Label "vfs-smoke\phase-steady-state-repeat\cloud-files-vfs-smoke.stdout.log"
Assert-Contains -Content $steadyStateRepeat -Expected "transfers=0" -Label "vfs-smoke\phase-steady-state-repeat\cloud-files-vfs-smoke.stdout.log"
Assert-Contains -Content $steadyStateRepeat -Expected "placeholderWrites=0" -Label "vfs-smoke\phase-steady-state-repeat\cloud-files-vfs-smoke.stdout.log"
Assert-Contains -Content $steadyStateRepeat -Expected "Result: passed" -Label "vfs-smoke\phase-steady-state-repeat\cloud-files-vfs-smoke.stdout.log"

Write-Host "Verified VFS release evidence bundle: $resolvedEvidenceDirectory"
