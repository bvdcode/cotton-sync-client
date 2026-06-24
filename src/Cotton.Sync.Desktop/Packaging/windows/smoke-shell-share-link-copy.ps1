param(
    [Parameter(Mandatory = $true)]
    [string]$AppExecutable,

    [Parameter(Mandatory = $true)]
    [string]$DataDirectory
)

$ErrorActionPreference = "Stop"

function Invoke-ProcessCapture {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$StandardOutputPath,
        [Parameter(Mandatory = $true)][string]$StandardErrorPath
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    [void]$process.Start()
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $timeoutSeconds = 120
    if (-not [string]::IsNullOrWhiteSpace($env:COTTON_SYNC_SHELL_SHARE_LINK_TIMEOUT_SECONDS)) {
        $timeoutSeconds = [int]$env:COTTON_SYNC_SHELL_SHARE_LINK_TIMEOUT_SECONDS
    }

    $timeoutMilliseconds = [Math]::Max(1, $timeoutSeconds) * 1000
    if (-not $process.WaitForExit($timeoutMilliseconds)) {
        try {
            $process.Kill($true)
        }
        catch {
            $process.Kill()
        }

        $process.WaitForExit()
        [System.IO.File]::WriteAllText($StandardOutputPath, $stdoutTask.GetAwaiter().GetResult())
        [System.IO.File]::WriteAllText($StandardErrorPath, $stderrTask.GetAwaiter().GetResult())
        return 124
    }

    [System.IO.File]::WriteAllText($StandardOutputPath, $stdoutTask.GetAwaiter().GetResult())
    [System.IO.File]::WriteAllText($StandardErrorPath, $stderrTask.GetAwaiter().GetResult())
    return $process.ExitCode
}

if (-not (Test-Path -LiteralPath $AppExecutable)) {
    throw "Desktop app executable was not found: $AppExecutable."
}

New-Item -ItemType Directory -Path $DataDirectory -Force | Out-Null
$stdoutPath = Join-Path $DataDirectory "shell-share-link.stdout.log"
$stderrPath = Join-Path $DataDirectory "shell-share-link.stderr.log"

$smokeExitCode = Invoke-ProcessCapture `
    -FilePath $AppExecutable `
    -Arguments @(
        "--shell-share-link-smoke",
        "--data-dir",
        $DataDirectory
    ) `
    -StandardOutputPath $stdoutPath `
    -StandardErrorPath $stderrPath

$smokeOutput = if (Test-Path -LiteralPath $stdoutPath) { Get-Content -LiteralPath $stdoutPath } else { @() }
$smokeError = if (Test-Path -LiteralPath $stderrPath) { Get-Content -LiteralPath $stderrPath } else { @() }
$smokeOutput | ForEach-Object { Write-Host $_ }

if ($smokeExitCode -ne 0) {
    $smokeError | ForEach-Object { Write-Host $_ }
    throw "Shell share-link copy smoke exited with code $smokeExitCode."
}

if (-not ($smokeOutput | Where-Object { $_ -eq "Result: passed" } | Select-Object -First 1)) {
    throw "Shell share-link copy smoke did not report a passed result."
}

Write-Host "Verified installed shell share-link copy flow."
