# SPDX-License-Identifier: MIT
# Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $DesktopAssembly,
    [Parameter(Mandatory)] [string] $OutputDirectory,
    [Parameter(Mandatory)] [uri] $ServerUrl,
    [ValidateRange(1, 100)] [int] $SeedFileCount = 16,
    [ValidateRange(0, 3600)] [int] $SoakSeconds = 60,
    [ValidateSet('full-mirror', 'windows-virtual-files')] [string[]] $Modes = @('full-mirror', 'windows-virtual-files'),
    [ValidateRange(60, 1800)] [int] $ModeTimeoutSeconds = 600
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $IsWindows) {
    throw 'Live Cloud Files acceptance requires Windows.'
}
if ($ServerUrl.Scheme -ne 'https' -or $ServerUrl.AbsolutePath -ne '/' -or $ServerUrl.Query -or $ServerUrl.UserInfo) {
    throw 'An HTTPS server origin is required.'
}
if ([string]::IsNullOrWhiteSpace($env:COTTON_SYNC_QA_USERNAME) -or [string]::IsNullOrWhiteSpace($env:COTTON_SYNC_QA_PASSWORD)) {
    throw 'Dedicated QA username and password environment variables are required.'
}
$assemblyPath = (Resolve-Path -LiteralPath $DesktopAssembly).Path
$shellHelper = Join-Path (Split-Path -LiteralPath $assemblyPath) 'WindowsShell/Cotton.Sync.WindowsShell.exe'
if (-not (Test-Path -LiteralPath $shellHelper -PathType Leaf)) {
    throw 'The desktop candidate must include its Windows shell helper.'
}
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputPath) {
    throw 'The acceptance output directory must not exist.'
}
$server = $ServerUrl.AbsoluteUri.TrimEnd('/')
$webSession = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
$headers = @{}

function Invoke-AcceptanceApi {
    param([string] $Method, [string] $Path, [object] $Body, [switch] $AllowMissing)
    $request = @{
        Uri = $server + $Path
        Method = $Method
        Headers = $headers
        WebSession = $webSession
        TimeoutSec = 30
        MaximumRedirection = 0
        SkipHttpErrorCheck = $true
    }
    if ($null -ne $Body) {
        $request.Body = $Body | ConvertTo-Json -Depth 5 -Compress
        $request.ContentType = 'application/json'
    }
    $response = Invoke-WebRequest @request
    if ($AllowMissing -and $response.StatusCode -eq 404) {
        return $null
    }
    if ($response.StatusCode -lt 200 -or $response.StatusCode -ge 300) {
        throw "Acceptance API failed: $Method $Path HTTP $($response.StatusCode)."
    }
    if ($response.Content) {
        return $response.Content | ConvertFrom-Json
    }
}

function Invoke-LiveMode {
    param([string] $Mode, [string] $RemotePath, [string] $RuntimeDirectory)
    $modeOutput = Join-Path $outputPath $Mode
    [IO.Directory]::CreateDirectory($modeOutput) | Out-Null
    $modeRuntime = Join-Path $RuntimeDirectory $Mode
    $startInfo = [Diagnostics.ProcessStartInfo]::new((Get-Command dotnet -CommandType Application).Source)
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $arguments = @(
        $assemblyPath, '--desktop-live-sync-smoke', '--server', $server,
        '--username', $env:COTTON_SYNC_QA_USERNAME,
        '--live-sync-smoke-password-env', 'COTTON_SYNC_QA_PASSWORD',
        '--sync-mode', $Mode, '--remote-path', $RemotePath,
        '--local-root', (Join-Path $modeRuntime 'a'),
        '--second-local-root', (Join-Path $modeRuntime 'b'),
        '--data-dir', (Join-Path $modeRuntime 'state'),
        '--live-sync-smoke-preserve-existing-local-files',
        '--live-sync-smoke-seed-file-count', $SeedFileCount.ToString()
    )
    if ($SoakSeconds -gt 0) {
        $arguments += @('--live-sync-smoke-soak-seconds', $SoakSeconds.ToString())
    }
    foreach ($argument in $arguments) {
        $startInfo.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $timedOut = $false
    $started = $false
    $stdout = ''
    $stderr = ''
    try {
        if (-not $process.Start()) {
            throw 'Could not start the desktop acceptance process.'
        }
        $started = $true
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($ModeTimeoutSeconds * 1000)) {
            $timedOut = $true
            $process.Kill($true)
        }
        $process.WaitForExit()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $lines = @($stdout -split '\r?\n' | Where-Object { $_.Length -gt 0 })
        $passed = -not $timedOut -and $process.ExitCode -eq 0 -and $lines.Count -gt 0 `
            -and $lines[-1] -eq 'Result: passed' -and $lines.Contains('Failures: 0') `
            -and -not ($lines | Where-Object { $_ -match '^(FAIL:|Error:)' })
        return [pscustomobject]@{
            Mode = $Mode
            Passed = $passed
            ExitCode = $process.ExitCode
            TimedOut = $timedOut
            ElapsedSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 3)
            PassedChecks = @($lines | Where-Object { $_.StartsWith('PASS: ') }).Count
        }
    }
    finally {
        if ($started -and -not $process.HasExited) {
            $process.Kill($true)
            $process.WaitForExit()
        }
        [IO.File]::WriteAllText((Join-Path $modeOutput 'stdout.log'), $stdout)
        [IO.File]::WriteAllText((Join-Path $modeOutput 'stderr.log'), $stderr)
        $process.Dispose()
    }
}

$info = Invoke-AcceptanceApi -Method Get -Path '/api/v1/server/info'
if ($info.isPublicInstance -ne $true) {
    throw 'Live acceptance is restricted to a public demo instance.'
}
[IO.Directory]::CreateDirectory($outputPath) | Out-Null
$runtimeDirectory = Join-Path $outputPath 'runtime'
$remoteRoot = $null
$login = $null
$signedIn = $false
$failure = $null
$results = [Collections.Generic.List[object]]::new()
$summary = [ordered]@{
    Passed = $false
    AssemblySha256 = (Get-FileHash -LiteralPath $assemblyPath -Algorithm SHA256).Hash
    RemoteCleanupPassed = $false
    SessionCleanupPassed = $false
    Modes = @()
}
try {
    $login = Invoke-AcceptanceApi -Method Post -Path '/api/v1/auth/login' -Body @{
        username = $env:COTTON_SYNC_QA_USERNAME
        password = $env:COTTON_SYNC_QA_PASSWORD
    }
    if ([string]::IsNullOrWhiteSpace($login.accessToken)) {
        throw 'The QA login did not return an access token.'
    }
    $signedIn = $true
    $headers.Authorization = 'Bearer ' + $login.accessToken
    $layoutRoot = Invoke-AcceptanceApi -Method Get -Path '/api/v1/layouts/resolver'
    $namespace = 'SyncReleaseAcceptance-' + [Guid]::NewGuid().ToString('N')
    $remoteRoot = Invoke-AcceptanceApi -Method Put -Path '/api/v1/layouts/nodes' -Body @{
        parentId = $layoutRoot.id
        name = $namespace
    }
    foreach ($mode in $Modes) {
        $modeRoot = Invoke-AcceptanceApi -Method Put -Path '/api/v1/layouts/nodes' -Body @{
            parentId = $remoteRoot.id
            name = $mode
        }
        Write-Output "Running live acceptance: $mode."
        $result = Invoke-LiveMode -Mode $mode -RemotePath "/$namespace/$mode" -RuntimeDirectory $runtimeDirectory
        $results.Add($result)
        Write-Output "$mode`: passed=$($result.Passed), checks=$($result.PassedChecks), elapsedSeconds=$($result.ElapsedSeconds)."
        if (-not $result.Passed) {
            throw "Live acceptance failed in $mode. See its stdout.log and stderr.log."
        }
    }
}
catch {
    $failure = $_
}
finally {
    if ($null -ne $remoteRoot) {
        try {
            Invoke-AcceptanceApi -Method Delete -Path "/api/v1/layouts/nodes/$($remoteRoot.id)?skipTrash=true" | Out-Null
            $remaining = Invoke-AcceptanceApi -Method Get -Path "/api/v1/layouts/resolver/$namespace" -AllowMissing
            if ($null -ne $remaining) {
                throw 'The acceptance remote namespace still exists after cleanup.'
            }
            $summary.RemoteCleanupPassed = $true
        }
        catch {
            Write-Error -Message 'The isolated acceptance remote namespace could not be cleaned up.' -ErrorAction Continue
            $failure = $_
        }
    }
    if ($signedIn) {
        try {
            Invoke-AcceptanceApi -Method Post -Path '/api/v1/auth/logout' | Out-Null
            $summary.SessionCleanupPassed = $true
        }
        catch {
            Write-Error -Message 'The acceptance API session could not be signed out.' -ErrorAction Continue
            $failure = $_
        }
    }
    $headers.Clear()
    $login = $null
    $summary.Modes = $results.ToArray()
    $summary.Passed = $null -eq $failure -and $results.Count -eq 2 `
        -and $summary.RemoteCleanupPassed -and $summary.SessionCleanupPassed
    if ($summary.Passed -and (Test-Path -LiteralPath $runtimeDirectory)) {
        $resolvedRuntime = (Resolve-Path -LiteralPath $runtimeDirectory).Path
        if (-not $resolvedRuntime.StartsWith($outputPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The runtime cleanup path is outside the acceptance output directory.'
        }
        Remove-Item -LiteralPath $resolvedRuntime -Recurse -Force
    }
    $summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputPath 'summary.json') -Encoding utf8
}
if ($null -ne $failure) {
    throw $failure
}
Write-Output "Live acceptance passed in $($Modes.Count) selected mode(s); remote namespace and API session cleaned up."
