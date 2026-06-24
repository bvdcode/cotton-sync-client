# SPDX-License-Identifier: MIT
# Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

param(
    [Parameter(Mandatory = $true)]
    [string]$EvidenceDirectory
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $EvidenceDirectory)) {
    throw "VFS logon evidence directory was not found: $EvidenceDirectory"
}

$resolvedEvidenceDirectory = (Resolve-Path -LiteralPath $EvidenceDirectory).Path

function Read-EvidenceFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RelativePath
    )

    $path = Join-Path $resolvedEvidenceDirectory $RelativePath
    if (-not (Test-Path -LiteralPath $path)) {
        throw "VFS logon evidence file was not found: $RelativePath"
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
Assert-Contains -Content $summary -Expected "OS: captured:" -Label "summary.txt"
Assert-Contains -Content $summary -Expected "CapturedAt:" -Label "summary.txt"
Assert-Contains -Content $summary -Expected "Installed app: captured:" -Label "summary.txt"
Assert-Contains -Content $summary -Expected "Autostart registry: captured:" -Label "summary.txt"
Assert-Contains -Content $summary -Expected "Cotton processes: captured:" -Label "summary.txt"
Assert-Contains -Content $summary -Expected "Cotton process windows: captured:" -Label "summary.txt"
Assert-Contains -Content $summary -Expected "Cloud Files Explorer registrations: captured:" -Label "summary.txt"
Assert-Contains -Content $summary -Expected "Local root entries: captured:" -Label "summary.txt"
Assert-Contains -Content $summary -Expected "Installed profile self-test: exitCode=0;" -Label "summary.txt"
Assert-Contains -Content $summary -Expected "Diagnostics export: exitCode=0;" -Label "summary.txt"

if ($summary.IndexOf("failed:", [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
    throw "VFS logon evidence summary contains a failed capture."
}

$os = Read-EvidenceFile -RelativePath "os.txt"
Assert-Contains -Content $os -Expected "LastBootUpTime" -Label "os.txt"

$installedApp = Read-EvidenceFile -RelativePath "installed-app.txt"
Assert-Contains -Content $installedApp -Expected "ProductVersion" -Label "installed-app.txt"
Assert-Contains -Content $installedApp -Expected "FileVersion" -Label "installed-app.txt"
Assert-Contains -Content $installedApp -Expected "Sha256" -Label "installed-app.txt"

$registryRun = Read-EvidenceFile -RelativePath "registry-run.txt"
Assert-Contains -Content $registryRun -Expected "Cotton Sync" -Label "registry-run.txt"
Assert-Contains -Content $registryRun -Expected "--start-minimized" -Label "registry-run.txt"

$processes = Read-EvidenceFile -RelativePath "processes.txt"
Assert-Contains -Content $processes -Expected "ProcessId" -Label "processes.txt"
Assert-Contains -Content $processes -Expected "Cotton.Sync.Desktop.exe" -Label "processes.txt"
Assert-Contains -Content $processes -Expected "--start-minimized" -Label "processes.txt"

$processWindows = Read-EvidenceFile -RelativePath "process-windows.txt"
Assert-DoesNotMatch `
    -Content $processWindows `
    -Pattern "^\s*IsForeground\s*:\s*True\b" `
    -Label "process-windows.txt" `
    -FailureMessage "Cotton Sync became the foreground window after logon."
Assert-DoesNotMatch `
    -Content $processWindows `
    -Pattern "^\s*VisibleWindowCount\s*:\s*[1-9]\d*\b" `
    -Label "process-windows.txt" `
    -FailureMessage "Cotton Sync had a visible window after logon."

$registryExplorer = Read-EvidenceFile -RelativePath "registry-cloud-files-explorer.txt"
Assert-Contains -Content $registryExplorer -Expected "MatchCount:" -Label "registry-cloud-files-explorer.txt"
Assert-DoesNotMatch `
    -Content $registryExplorer `
    -Pattern "^\s*MatchCount:\s*0\b" `
    -Label "registry-cloud-files-explorer.txt" `
    -FailureMessage "No Cloud Files or Explorer registration was captured after logon."

$localRootEntries = Read-EvidenceFile -RelativePath "local-root-entries.csv"
Assert-Contains -Content $localRootEntries -Expected '"RelativePath","FullPath","Exists","Attributes","Length","LastWriteTimeUtc"' -Label "local-root-entries.csv"
Assert-Contains -Content $localRootEntries -Expected '"."' -Label "local-root-entries.csv"

$profileSelfTest = Read-EvidenceFile -RelativePath "profile-self-test.stdout.log"
Assert-Contains -Content $profileSelfTest -Expected "[OK] Authentication state - Stored session available" -Label "profile-self-test.stdout.log"
Assert-Contains -Content $profileSelfTest -Expected "[OK] Autostart adapter - Enabled" -Label "profile-self-test.stdout.log"
Assert-Contains -Content $profileSelfTest -Expected "[OK] Windows virtual files" -Label "profile-self-test.stdout.log"
Assert-Contains -Content $profileSelfTest -Expected "[OK] Local root:" -Label "profile-self-test.stdout.log"
Assert-Contains -Content $profileSelfTest -Expected "Result: passed" -Label "profile-self-test.stdout.log"

$diagnosticsExport = Read-EvidenceFile -RelativePath "diagnostics-export.stdout.log"
Assert-Contains -Content $diagnosticsExport -Expected "Diagnostics" -Label "diagnostics-export.stdout.log"

$runnerLog = Read-EvidenceFile -RelativePath "run-vfs-logon-evidence-capture.log"
Assert-Contains -Content $runnerLog -Expected "RunnerStartedAt:" -Label "run-vfs-logon-evidence-capture.log"
Assert-Contains -Content $runnerLog -Expected "TaskName:" -Label "run-vfs-logon-evidence-capture.log"
Assert-Contains -Content $runnerLog -Expected "RunnerUser:" -Label "run-vfs-logon-evidence-capture.log"
Assert-Contains -Content $runnerLog -Expected "RunnerSessionId:" -Label "run-vfs-logon-evidence-capture.log"
Assert-Contains -Content $runnerLog -Expected "RunnerProcessId:" -Label "run-vfs-logon-evidence-capture.log"
Assert-Contains -Content $runnerLog -Expected "RunnerInteractive: True" -Label "run-vfs-logon-evidence-capture.log"
Assert-DoesNotMatch `
    -Content $runnerLog `
    -Pattern "^\s*RunnerSessionId:\s*0\b" `
    -Label "run-vfs-logon-evidence-capture.log" `
    -FailureMessage "VFS logon evidence runner executed in Windows session 0 instead of an interactive user session."
Assert-Contains -Content $runnerLog -Expected "Cotton VFS release evidence captured:" -Label "run-vfs-logon-evidence-capture.log"
Assert-Contains -Content $runnerLog -Expected "CaptureExitCode: 0" -Label "run-vfs-logon-evidence-capture.log"
Assert-Contains -Content $runnerLog -Expected "RunnerFinishedAt:" -Label "run-vfs-logon-evidence-capture.log"

Write-Host "Verified VFS logon evidence bundle: $resolvedEvidenceDirectory"
