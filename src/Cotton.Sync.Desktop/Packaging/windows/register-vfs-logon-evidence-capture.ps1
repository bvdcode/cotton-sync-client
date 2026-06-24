# SPDX-License-Identifier: MIT
# Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

param(
    [string]$TaskName = "Cotton Sync VFS Logon Evidence Capture",

    [string]$OutputDirectory = "",

    [string]$LocalRoot = "",

    [string]$DataDirectory = "",

    [string]$InstallDirectory = "",

    [int]$DelaySeconds = 45,

    [switch]$CaptureScreenshot,

    [switch]$ValidateOnly,

    [switch]$Remove
)

$ErrorActionPreference = "Stop"
$allowedTaskNamePrefix = "Cotton Sync VFS Logon Evidence Capture"

function ConvertTo-SingleQuotedLiteral {
    param([string]$Value)

    return "'" + $Value.Replace("'", "''") + "'"
}

function ConvertTo-CommandLineArgument {
    param([string]$Value)

    return '"' + $Value.Replace('"', '\"') + '"'
}

function Assert-SafeTaskName {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "TaskName is required."
    }

    if (-not $Value.StartsWith($allowedTaskNamePrefix, [System.StringComparison]::Ordinal)) {
        throw "TaskName must start with '$allowedTaskNamePrefix'."
    }
}

function Assert-RequiredValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "$Name is required unless -Remove is used."
    }
}

function Resolve-RequiredDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "$Name was not found: $Path"
    }

    return (Resolve-Path -LiteralPath $Path).Path
}

function Assert-InstalledAutostart {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExecutablePath,

        [string]$ProfileDataDirectory = ""
    )

    $runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
    $runValue = (Get-ItemProperty -Path $runKey -Name "Cotton Sync" -ErrorAction SilentlyContinue)."Cotton Sync"
    $expectedRunValue = "`"$ExecutablePath`" --start-minimized"
    if (-not [string]::IsNullOrWhiteSpace($ProfileDataDirectory)) {
        $expectedRunValue += " --data-dir `"$ProfileDataDirectory`""
    }

    if ($runValue -ne $expectedRunValue) {
        throw "Autostart registry value was not ready for logon capture. Expected '$expectedRunValue', got '$runValue'."
    }
}

function Assert-TextContains {
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

function Invoke-ProfileSelfTestPreflight {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExecutablePath,

        [Parameter(Mandatory = $true)]
        [string]$ProfileDataDirectory
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $ExecutablePath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.ArgumentList.Add("--self-test")
    $startInfo.ArgumentList.Add("--data-dir")
    $startInfo.ArgumentList.Add($ProfileDataDirectory)

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    [void]$process.Start()
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(60000)) {
        try {
            $process.Kill()
        }
        catch {
        }

        throw "Installed profile self-test timed out before logon capture could be armed."
    }

    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0) {
        throw "Installed profile self-test was not ready for logon capture. Exit code: $($process.ExitCode). $stderr"
    }

    Assert-TextContains -Content $stdout -Expected "[OK] Authentication state - Stored session available" -Label "Installed profile self-test"
    Assert-TextContains -Content $stdout -Expected "[OK] Autostart adapter - Enabled" -Label "Installed profile self-test"
    Assert-TextContains -Content $stdout -Expected "[OK] Windows virtual files" -Label "Installed profile self-test"
    Assert-TextContains -Content $stdout -Expected "[OK] Local root:" -Label "Installed profile self-test"
    Assert-TextContains -Content $stdout -Expected "Result: passed" -Label "Installed profile self-test"
}

if ($DelaySeconds -lt 0) {
    throw "DelaySeconds must not be negative."
}

Assert-SafeTaskName -Value $TaskName

if ($Remove) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Host "Removed VFS logon evidence capture task: $TaskName"
    return
}

Assert-RequiredValue -Name "OutputDirectory" -Value $OutputDirectory
Assert-RequiredValue -Name "LocalRoot" -Value $LocalRoot
Assert-RequiredValue -Name "DataDirectory" -Value $DataDirectory
Assert-RequiredValue -Name "InstallDirectory" -Value $InstallDirectory

$captureScript = Join-Path $PSScriptRoot "capture-vfs-release-evidence.ps1"
if (-not (Test-Path -LiteralPath $captureScript)) {
    throw "VFS release evidence capture script was not found: $captureScript"
}

$resolvedLocalRoot = Resolve-RequiredDirectory -Name "Local root" -Path $LocalRoot
$resolvedDataDirectory = Resolve-RequiredDirectory -Name "Data directory" -Path $DataDirectory
$resolvedInstallDirectory = Resolve-RequiredDirectory -Name "Install directory" -Path $InstallDirectory
$installedExecutable = Join-Path $resolvedInstallDirectory "Cotton.Sync.Desktop.exe"
if (-not (Test-Path -LiteralPath $installedExecutable -PathType Leaf)) {
    throw "Installed desktop executable was not found: $installedExecutable"
}

Assert-InstalledAutostart -ExecutablePath $installedExecutable -ProfileDataDirectory $resolvedDataDirectory
Invoke-ProfileSelfTestPreflight -ExecutablePath $installedExecutable -ProfileDataDirectory $resolvedDataDirectory

if ($ValidateOnly) {
    Write-Host "Validated VFS logon evidence capture inputs."
    Write-Host "Executable: $installedExecutable"
    Write-Host "LocalRoot: $resolvedLocalRoot"
    Write-Host "DataDirectory: $resolvedDataDirectory"
    Write-Host "InstallDirectory: $resolvedInstallDirectory"
    return
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$resolvedOutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$runnerPath = Join-Path $resolvedOutputDirectory "run-vfs-logon-evidence-capture.ps1"
$runnerLogPath = Join-Path $resolvedOutputDirectory "run-vfs-logon-evidence-capture.log"

$captureArguments = @(
    "-NoProfile",
    "-ExecutionPolicy",
    "Bypass",
    "-File",
    $captureScript,
    "-OutputDirectory",
    $resolvedOutputDirectory,
    "-LocalRoot",
    $resolvedLocalRoot,
    "-DataDirectory",
    $resolvedDataDirectory,
    "-InstallDirectory",
    $resolvedInstallDirectory,
    "-RunProfileSelfTest",
    "-RunDiagnosticsExport"
)

if ($CaptureScreenshot) {
    $captureArguments += "-CaptureScreenshot"
}

$captureArgumentsLiteral = "@(" + (($captureArguments | ForEach-Object { ConvertTo-SingleQuotedLiteral $_ }) -join ", ") + ")"
$taskNameLiteral = ConvertTo-SingleQuotedLiteral $TaskName
$runnerLogPathLiteral = ConvertTo-SingleQuotedLiteral $runnerLogPath
$taskRegisteredAt = (Get-Date).ToString("O")

$runnerScript = @"
`$ErrorActionPreference = "Stop"
`$exitCode = 1
Start-Sleep -Seconds $DelaySeconds
try {
    "RunnerStartedAt: `$((Get-Date).ToString('O'))" | Out-File -LiteralPath $runnerLogPathLiteral -Encoding utf8
    "TaskRegisteredAt: $taskRegisteredAt" | Out-File -LiteralPath $runnerLogPathLiteral -Encoding utf8 -Append
    `$latestInteractiveLogon = Get-CimInstance Win32_LogonSession -OperationTimeoutSec 2 -ErrorAction SilentlyContinue | Where-Object { `$_.LogonType -eq 2 -or `$_.LogonType -eq 10 } | Sort-Object StartTime -Descending | Select-Object -First 1
    if (`$null -ne `$latestInteractiveLogon -and `$null -ne `$latestInteractiveLogon.StartTime) {
        "LatestInteractiveLogonAt: `$((([datetime]`$latestInteractiveLogon.StartTime).ToString('O')))" | Out-File -LiteralPath $runnerLogPathLiteral -Encoding utf8 -Append
    } else {
        "LatestInteractiveLogonAt: <unavailable>" | Out-File -LiteralPath $runnerLogPathLiteral -Encoding utf8 -Append
    }
    "TaskName: $TaskName" | Out-File -LiteralPath $runnerLogPathLiteral -Encoding utf8 -Append
    "RunnerUser: `$([System.Security.Principal.WindowsIdentity]::GetCurrent().Name)" | Out-File -LiteralPath $runnerLogPathLiteral -Encoding utf8 -Append
    "RunnerSessionId: `$([System.Diagnostics.Process]::GetCurrentProcess().SessionId)" | Out-File -LiteralPath $runnerLogPathLiteral -Encoding utf8 -Append
    "RunnerProcessId: `$PID" | Out-File -LiteralPath $runnerLogPathLiteral -Encoding utf8 -Append
    "RunnerInteractive: `$([Environment]::UserInteractive)" | Out-File -LiteralPath $runnerLogPathLiteral -Encoding utf8 -Append
    `$arguments = $captureArgumentsLiteral
    & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" @arguments *>> $runnerLogPathLiteral
    `$exitCode = `$LASTEXITCODE
    "CaptureExitCode: `$exitCode" | Out-File -LiteralPath $runnerLogPathLiteral -Encoding utf8 -Append
    if (`$exitCode -ne 0) {
        throw "VFS logon evidence capture exited with code `$exitCode."
    }
    "RunnerFinishedAt: `$((Get-Date).ToString('O'))" | Out-File -LiteralPath $runnerLogPathLiteral -Encoding utf8 -Append
}
catch {
    `$exitCode = 1
    `$_.Exception.ToString() | Out-File -LiteralPath $runnerLogPathLiteral -Encoding utf8 -Append
}
finally {
    Unregister-ScheduledTask -TaskName $taskNameLiteral -Confirm:`$false -ErrorAction SilentlyContinue
    `$remainingTask = Get-ScheduledTask -TaskName $taskNameLiteral -ErrorAction SilentlyContinue
    "TaskUnregistered: `$(`$null -eq `$remainingTask)" | Out-File -LiteralPath $runnerLogPathLiteral -Encoding utf8 -Append
}

exit `$exitCode
"@

$runnerScript | Out-File -LiteralPath $runnerPath -Encoding utf8

$currentUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
$taskAction = New-ScheduledTaskAction `
    -Execute "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
    -Argument ("-NoProfile -ExecutionPolicy Bypass -File " + (ConvertTo-CommandLineArgument $runnerPath)) `
    -WorkingDirectory $resolvedOutputDirectory
$taskTrigger = New-ScheduledTaskTrigger -AtLogOn -User $currentUser
$taskPrincipal = New-ScheduledTaskPrincipal -UserId $currentUser -LogonType Interactive
$taskSettings = New-ScheduledTaskSettingsSet `
    -StartWhenAvailable `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 10) `
    -MultipleInstances IgnoreNew

Register-ScheduledTask `
    -TaskName $TaskName `
    -Action $taskAction `
    -Trigger $taskTrigger `
    -Principal $taskPrincipal `
    -Settings $taskSettings `
    -Description "Captures Cotton Sync VFS evidence after the next interactive Windows logon, then unregisters itself." `
    -Force | Out-Null

Write-Host "Registered VFS logon evidence capture task: $TaskName"
Write-Host "Runner: $runnerPath"
Write-Host "Output: $resolvedOutputDirectory"
