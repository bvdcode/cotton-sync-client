param(
    [Parameter(Mandatory = $true)]
    [string]$ShortcutPath,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedExecutablePath,

    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = "Stop"

if ($TimeoutSeconds -le 0) {
    throw "TimeoutSeconds must be greater than zero."
}

$resolvedShortcut = (Resolve-Path -LiteralPath $ShortcutPath).Path
$resolvedExecutable = (Resolve-Path -LiteralPath $ExpectedExecutablePath).Path
$expectedProcessName = [System.IO.Path]::GetFileName($resolvedExecutable)

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($resolvedShortcut)
$shortcutTarget = [Environment]::ExpandEnvironmentVariables([string]$shortcut.TargetPath)
if ([string]::IsNullOrWhiteSpace($shortcutTarget)) {
    throw "Start Menu shortcut has no target path: $resolvedShortcut"
}

$resolvedShortcutTarget = [System.IO.Path]::GetFullPath($shortcutTarget)
if (-not [string]::Equals($resolvedShortcutTarget, $resolvedExecutable, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Start Menu shortcut target was '$resolvedShortcutTarget', expected '$resolvedExecutable'."
}

function Get-TargetProcess {
    @(Get-CimInstance Win32_Process -Filter "Name = '$expectedProcessName'" -ErrorAction SilentlyContinue |
        Where-Object {
            -not [string]::IsNullOrWhiteSpace($_.ExecutablePath) -and
            [string]::Equals($_.ExecutablePath, $resolvedExecutable, [StringComparison]::OrdinalIgnoreCase)
        })
}

$existingProcesses = Get-TargetProcess
foreach ($process in $existingProcesses) {
    Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
}

$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
Write-Host "Launching Start Menu shortcut: $resolvedShortcut"
$null = Start-Process -FilePath $resolvedShortcut -PassThru

do {
    $launchedProcesses = Get-TargetProcess
    if ($launchedProcesses.Count -gt 0) {
        break
    }

    Start-Sleep -Milliseconds 500
} while ([DateTime]::UtcNow -lt $deadline)

if ($launchedProcesses.Count -eq 0) {
    throw "Start Menu shortcut did not launch $resolvedExecutable within $TimeoutSeconds second(s)."
}

Start-Sleep -Seconds 2
$launchedProcessIds = @($launchedProcesses | ForEach-Object { [int]$_.ProcessId })
$stillRunning = @(Get-TargetProcess | Where-Object { $launchedProcessIds -contains [int]$_.ProcessId })
if ($stillRunning.Count -eq 0) {
    throw "Start Menu shortcut launched $resolvedExecutable, but the process exited immediately."
}

foreach ($process in $stillRunning) {
    Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
}

$stopDeadline = [DateTime]::UtcNow.AddSeconds(10)
do {
    $remaining = @(Get-TargetProcess | Where-Object { $launchedProcessIds -contains [int]$_.ProcessId })
    if ($remaining.Count -eq 0) {
        break
    }

    Start-Sleep -Milliseconds 250
} while ([DateTime]::UtcNow -lt $stopDeadline)

if ($remaining.Count -ne 0) {
    $remaining | ForEach-Object { Write-Host "Remaining process: $($_.ProcessId) $($_.ExecutablePath)" }
    throw "Start Menu shortcut-launched process did not exit during cleanup."
}

Write-Host "Verified Start Menu shortcut launch: $resolvedShortcut"
