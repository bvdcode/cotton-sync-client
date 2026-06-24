param(
    [Parameter(Mandatory = $true)]
    [string]$AppExecutable,

    [string]$RunValueName = "Cotton Sync",

    [int]$TimeoutSeconds = 30,

    [int]$ObservationSeconds = 6,

    [string]$ReportPath = "",

    [string]$DataDirectory = "",

    [switch]$AttachExistingProcess
)

$ErrorActionPreference = "Stop"

if ($TimeoutSeconds -le 0) {
    throw "TimeoutSeconds must be greater than zero."
}

if ($ObservationSeconds -le 0) {
    throw "ObservationSeconds must be greater than zero."
}

if ($AttachExistingProcess -and -not [string]::IsNullOrWhiteSpace($DataDirectory)) {
    throw "DataDirectory cannot be used when attaching to an existing installer-launched process."
}

function Write-AutostartReport {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string[]]$Lines
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return
    }

    $directory = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    $Lines | Set-Content -LiteralPath $Path -Encoding utf8
}

$resolvedExecutable = (Resolve-Path -LiteralPath $AppExecutable).Path
$expectedProcessName = [System.IO.Path]::GetFileName($resolvedExecutable)
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$runValue = (Get-ItemProperty -Path $runKey -Name $RunValueName -ErrorAction SilentlyContinue).$RunValueName
$expectedRunValue = "`"$resolvedExecutable`" --start-minimized"
if (-not [string]::IsNullOrWhiteSpace($DataDirectory)) {
    $expectedRunValue += " --data-dir `"$DataDirectory`""
}
if ($runValue -ne $expectedRunValue) {
    throw "Autostart registry value was not installed correctly. Expected '$expectedRunValue', got '$runValue'."
}

Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class CottonAutostartWindowProbe
{
    public sealed class WindowSnapshot
    {
        public IntPtr Handle { get; set; }
        public string Title { get; set; }
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    public static WindowSnapshot[] GetVisibleWindowsForProcess(int targetProcessId)
    {
        var windows = new List<WindowSnapshot>();
        EnumWindows((hWnd, lParam) =>
        {
            if (!IsWindowVisible(hWnd))
            {
                return true;
            }

            uint processId;
            GetWindowThreadProcessId(hWnd, out processId);
            if (processId != targetProcessId)
            {
                return true;
            }

            var title = new StringBuilder(512);
            GetWindowText(hWnd, title, title.Capacity);
            windows.Add(new WindowSnapshot { Handle = hWnd, Title = title.ToString() });
            return true;
        }, IntPtr.Zero);
        return windows.ToArray();
    }

    public static int GetForegroundProcessId()
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return 0;
        }

        uint processId;
        GetWindowThreadProcessId(foreground, out processId);
        return (int)processId;
    }
}
"@

function Get-TargetProcess {
    $processNameWithoutExtension = [System.IO.Path]::GetFileNameWithoutExtension($expectedProcessName)
    @(Get-Process -Name $processNameWithoutExtension -ErrorAction SilentlyContinue |
        Where-Object {
            -not [string]::IsNullOrWhiteSpace($_.Path) -and
            [string]::Equals($_.Path, $resolvedExecutable, [StringComparison]::OrdinalIgnoreCase)
        } |
        ForEach-Object {
            [pscustomobject]@{
                ProcessId = $_.Id
                ExecutablePath = $_.Path
                CreationDate = $_.StartTime
            }
        })
}

function Get-ProcessCommandLine {
    param(
        [Parameter(Mandatory = $true)]
        [int]$ProcessId
    )

    $powerShellPath = (Get-Process -Id $PID).Path
    if ([string]::IsNullOrWhiteSpace($powerShellPath) -or -not (Test-Path -LiteralPath $powerShellPath)) {
        $powerShellPath = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
    }

    $stdoutPath = [System.IO.Path]::GetTempFileName()
    $stderrPath = [System.IO.Path]::GetTempFileName()
    try {
        $command = "try { `$process = Get-CimInstance Win32_Process -Filter `"ProcessId = $ProcessId`" -OperationTimeoutSec 2 -ErrorAction Stop; if (`$null -ne `$process) { `$process.CommandLine } } catch { }"
        $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
        $probe = Start-Process `
            -FilePath $powerShellPath `
            -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-EncodedCommand", $encodedCommand) `
            -RedirectStandardOutput $stdoutPath `
            -RedirectStandardError $stderrPath `
            -WindowStyle Hidden `
            -PassThru

        if (-not $probe.WaitForExit(5000)) {
            Stop-Process -Id $probe.Id -Force -ErrorAction SilentlyContinue
            return ""
        }

        return (Get-Content -LiteralPath $stdoutPath -Raw -ErrorAction SilentlyContinue).Trim()
    }
    finally {
        Remove-Item -LiteralPath $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue
    }
}

$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
$targetProcess = $null
$process = $null

if ($AttachExistingProcess) {
    Write-Host "Waiting for existing hidden startup process: $resolvedExecutable"
    do {
        $targetProcess = Get-TargetProcess |
            Sort-Object CreationDate -Descending |
            Select-Object -First 1
        if ($null -ne $targetProcess) {
            $process = Get-Process -Id $targetProcess.ProcessId -ErrorAction SilentlyContinue
            if ($null -ne $process) {
                break
            }
        }

        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
} else {
    $existingProcesses = Get-TargetProcess
    foreach ($existingProcess in $existingProcesses) {
        Stop-Process -Id $existingProcess.ProcessId -Force -ErrorAction SilentlyContinue
    }

    $stopDeadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $remaining = Get-TargetProcess
        if ($remaining.Count -eq 0) {
            break
        }

        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $stopDeadline)

    if ($remaining.Count -ne 0) {
        $remaining | ForEach-Object { Write-Host "Existing process remained: $($_.ProcessId) $($_.ExecutablePath)" }
        throw "Existing Cotton Sync process did not exit before autostart smoke."
    }

    $launchArguments = New-Object System.Collections.Generic.List[string]
    $launchArguments.Add("--start-minimized")
    if (-not [string]::IsNullOrWhiteSpace($DataDirectory)) {
        New-Item -ItemType Directory -Path $DataDirectory -Force | Out-Null
        $launchArguments.Add("--data-dir")
        $launchArguments.Add($DataDirectory)
    }

    Write-Host "Launching installed autostart command: $expectedRunValue"
    $process = Start-Process `
        -FilePath $resolvedExecutable `
        -ArgumentList $launchArguments `
        -PassThru

    do {
        $candidates = Get-TargetProcess
        $targetProcess = $candidates | Where-Object { [int]$_.ProcessId -eq $process.Id } | Select-Object -First 1
        if ($null -ne $targetProcess) {
            break
        }

        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
}

if ($null -eq $targetProcess) {
    if ($AttachExistingProcess) {
        throw "Hidden startup process did not appear for $resolvedExecutable within $TimeoutSeconds second(s)."
    }

    throw "Autostart command did not launch $resolvedExecutable within $TimeoutSeconds second(s)."
}

if ($null -eq $process) {
    $process = Get-Process -Id $targetProcess.ProcessId -ErrorAction SilentlyContinue
}

if ($null -eq $process) {
    throw "Startup-launched process exited before observation started."
}

$targetCommandLine = Get-ProcessCommandLine -ProcessId $process.Id
if ([string]::IsNullOrWhiteSpace($targetCommandLine)) {
    throw "Autostart-launched process command line could not be captured within the bounded observation window."
}

if ($targetCommandLine -notmatch "(^|\s)--start-minimized($|\s)") {
    throw "Autostart-launched process command line did not include --start-minimized: $targetCommandLine"
}

$observedVisibleWindows = @()
$observedForeground = $false
$observationDeadline = [DateTime]::UtcNow.AddSeconds($ObservationSeconds)
do {
    $runningProcess = Get-Process -Id $process.Id -ErrorAction SilentlyContinue
    if ($null -eq $runningProcess) {
        throw "Autostart-launched process exited during the observation window."
    }

    $visibleWindows = @([CottonAutostartWindowProbe]::GetVisibleWindowsForProcess($process.Id))
    if ($visibleWindows.Count -gt 0) {
        $observedVisibleWindows += $visibleWindows
    }

    $foregroundProcessId = [CottonAutostartWindowProbe]::GetForegroundProcessId()
    if ($foregroundProcessId -eq $process.Id) {
        $observedForeground = $true
    }

    Start-Sleep -Milliseconds 200
} while ([DateTime]::UtcNow -lt $observationDeadline)

if ($observedVisibleWindows.Count -gt 0) {
    $observedVisibleWindows |
        Select-Object -First 10 |
        ForEach-Object { Write-Host ("Visible window: handle=0x{0:x} title='{1}'" -f $_.Handle.ToInt64(), $_.Title) }
    throw "Autostart-launched process created a visible top-level window."
}

if ($observedForeground) {
    throw "Autostart-launched process became the foreground window."
}

$cleanupDeadline = [DateTime]::UtcNow.AddSeconds(10)
do {
    $cleanupProcesses = Get-TargetProcess
    foreach ($cleanupProcess in $cleanupProcesses) {
        Stop-Process -Id $cleanupProcess.ProcessId -Force -ErrorAction SilentlyContinue
    }

    if ($cleanupProcesses.Count -eq 0) {
        break
    }

    Start-Sleep -Milliseconds 250
} while ([DateTime]::UtcNow -lt $cleanupDeadline)

$cleanupProcesses = Get-TargetProcess
if ($cleanupProcesses.Count -ne 0) {
    $cleanupProcesses | ForEach-Object { Write-Host "Warning: autostart-launched process remained after cleanup: $($_.ProcessId) $($_.ExecutablePath)" }
}

Write-AutostartReport `
    -Path $ReportPath `
    -Lines @(
        "Result: passed",
        "Executable: $resolvedExecutable",
        "ExpectedRunValue: $expectedRunValue",
        "LaunchMode: $(if ($AttachExistingProcess) { "attached-existing" } else { "started-command" })",
        "IsolationDataDirectory: $DataDirectory",
        "ProcessId: $($process.Id)",
        "CommandLine: $targetCommandLine",
        "ObservedForeground: $observedForeground",
        "VisibleWindowCount: $($observedVisibleWindows.Count)",
        "CleanupRemaining: $($cleanupProcesses.Count)")

Write-Host "Verified installed autostart launch stayed hidden to tray: $resolvedExecutable"
