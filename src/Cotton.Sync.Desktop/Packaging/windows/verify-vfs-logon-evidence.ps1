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

function Read-LabeledValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Content,

        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    foreach ($line in $Content -split "\r?\n") {
        if ($line.StartsWith($Label, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $line.Substring($Label.Length).Trim()
        }
    }

    throw "run-vfs-logon-evidence-capture.log did not contain expected label: $Label"
}

function Read-FormatListRecords {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Content,

        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    $records = New-Object System.Collections.Generic.List[object]
    $current = @{}
    foreach ($line in $Content -split "\r?\n") {
        if ([string]::IsNullOrWhiteSpace($line)) {
            if ($current.Count -gt 0) {
                $records.Add($current)
                $current = @{}
            }

            continue
        }

        $match = [regex]::Match($line, "^\s*(?<key>[^:]+?)\s*:\s*(?<value>.*)$")
        if ($match.Success) {
            $current[$match.Groups["key"].Value.Trim()] = $match.Groups["value"].Value.Trim()
        }
    }

    if ($current.Count -gt 0) {
        $records.Add($current)
    }

    if ($records.Count -eq 0) {
        throw "$Label did not contain Format-List records."
    }

    return $records.ToArray()
}

function Read-RequiredRecordValue {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Records,

        [Parameter(Mandatory = $true)]
        [string]$Key,

        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    foreach ($record in $Records) {
        if ($record.ContainsKey($Key)) {
            $value = [string]$record[$Key]
            if (-not [string]::IsNullOrWhiteSpace($value)) {
                return $value.Trim()
            }
        }
    }

    throw "$Label did not contain expected value: $Key"
}

function Test-SamePathText {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Left,

        [Parameter(Mandatory = $true)]
        [string]$Right
    )

    $normalizedLeft = $Left.Trim().Trim('"')
    $normalizedRight = $Right.Trim().Trim('"')
    return [string]::Equals($normalizedLeft, $normalizedRight, [System.StringComparison]::OrdinalIgnoreCase)
}

function Read-EvidenceTimestamp {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Content,

        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    $value = Read-LabeledValue -Content $Content -Label $Label
    if ([string]::Equals($value, "<unavailable>", [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "run-vfs-logon-evidence-capture.log reported unavailable timestamp for $Label"
    }

    try {
        return [System.DateTimeOffset]::Parse(
            $value,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [System.Globalization.DateTimeStyles]::RoundtripKind)
    }
    catch {
        throw "run-vfs-logon-evidence-capture.log contained invalid timestamp for ${Label}: $value"
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
Assert-Contains -Content $installedApp -Expected "Path" -Label "installed-app.txt"
Assert-Contains -Content $installedApp -Expected "ProductVersion" -Label "installed-app.txt"
Assert-Contains -Content $installedApp -Expected "FileVersion" -Label "installed-app.txt"
Assert-Contains -Content $installedApp -Expected "Sha256" -Label "installed-app.txt"
$installedAppRecords = Read-FormatListRecords -Content $installedApp -Label "installed-app.txt"
$installedExecutablePath = Read-RequiredRecordValue -Records $installedAppRecords -Key "Path" -Label "installed-app.txt"
if (-not $installedExecutablePath.EndsWith("\Cotton.Sync.Desktop.exe", [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "installed-app.txt path was not the desktop executable: $installedExecutablePath"
}

$registryRun = Read-EvidenceFile -RelativePath "registry-run.txt"
Assert-Contains -Content $registryRun -Expected "Cotton Sync" -Label "registry-run.txt"
Assert-Contains -Content $registryRun -Expected "--start-minimized" -Label "registry-run.txt"
$registryRunRecords = Read-FormatListRecords -Content $registryRun -Label "registry-run.txt"
$registryRunValue = Read-RequiredRecordValue -Records $registryRunRecords -Key "Value" -Label "registry-run.txt"
if ($registryRunValue.IndexOf($installedExecutablePath, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
    throw "registry-run.txt did not reference the installed executable path: $installedExecutablePath"
}

$processes = Read-EvidenceFile -RelativePath "processes.txt"
Assert-Contains -Content $processes -Expected "ProcessId" -Label "processes.txt"
Assert-Contains -Content $processes -Expected "Cotton.Sync.Desktop.exe" -Label "processes.txt"
Assert-Contains -Content $processes -Expected "--start-minimized" -Label "processes.txt"
$processRecords = Read-FormatListRecords -Content $processes -Label "processes.txt"
$runningInstalledProcessFound = $false
foreach ($processRecord in $processRecords) {
    if (-not $processRecord.ContainsKey("ExecutablePath") -or -not $processRecord.ContainsKey("CommandLine")) {
        continue
    }

    $processExecutablePath = [string]$processRecord["ExecutablePath"]
    $processCommandLine = [string]$processRecord["CommandLine"]
    if ((Test-SamePathText -Left $processExecutablePath -Right $installedExecutablePath) -and
        $processCommandLine.IndexOf($installedExecutablePath, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
        $processCommandLine.IndexOf("--start-minimized", [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        $runningInstalledProcessFound = $true
        break
    }
}

if (-not $runningInstalledProcessFound) {
    throw "processes.txt did not contain a running installed executable with --start-minimized: $installedExecutablePath"
}

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
Assert-Contains -Content $runnerLog -Expected "TaskRegisteredAt:" -Label "run-vfs-logon-evidence-capture.log"
Assert-Contains -Content $runnerLog -Expected "LatestInteractiveLogonAt:" -Label "run-vfs-logon-evidence-capture.log"
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
Assert-Contains -Content $runnerLog -Expected "TaskUnregistered: True" -Label "run-vfs-logon-evidence-capture.log"

$taskRegisteredAt = Read-EvidenceTimestamp -Content $runnerLog -Label "TaskRegisteredAt:"
$latestInteractiveLogonAt = Read-EvidenceTimestamp -Content $runnerLog -Label "LatestInteractiveLogonAt:"
if ($latestInteractiveLogonAt -le $taskRegisteredAt) {
    throw "VFS logon evidence was not captured after a newer interactive Windows logon."
}

Write-Host "Verified VFS logon evidence bundle: $resolvedEvidenceDirectory"
