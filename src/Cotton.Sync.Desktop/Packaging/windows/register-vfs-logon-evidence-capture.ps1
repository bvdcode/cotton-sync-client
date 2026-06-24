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

    [switch]$Remove
)

$ErrorActionPreference = "Stop"

function ConvertTo-SingleQuotedLiteral {
    param([string]$Value)

    return "'" + $Value.Replace("'", "''") + "'"
}

function ConvertTo-CommandLineArgument {
    param([string]$Value)

    return '"' + $Value.Replace('"', '\"') + '"'
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

if ($DelaySeconds -lt 0) {
    throw "DelaySeconds must not be negative."
}

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

if (-not (Test-Path -LiteralPath $InstallDirectory)) {
    throw "Install directory was not found: $InstallDirectory"
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
    $LocalRoot,
    "-DataDirectory",
    $DataDirectory,
    "-InstallDirectory",
    $InstallDirectory,
    "-RunProfileSelfTest",
    "-RunDiagnosticsExport"
)

if ($CaptureScreenshot) {
    $captureArguments += "-CaptureScreenshot"
}

$captureArgumentsLiteral = "@(" + (($captureArguments | ForEach-Object { ConvertTo-SingleQuotedLiteral $_ }) -join ", ") + ")"
$taskNameLiteral = ConvertTo-SingleQuotedLiteral $TaskName
$runnerLogPathLiteral = ConvertTo-SingleQuotedLiteral $runnerLogPath

$runnerScript = @"
`$ErrorActionPreference = "Stop"
`$exitCode = 1
Start-Sleep -Seconds $DelaySeconds
try {
    "RunnerStartedAt: `$((Get-Date).ToString('O'))" | Out-File -LiteralPath $runnerLogPathLiteral -Encoding utf8
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
