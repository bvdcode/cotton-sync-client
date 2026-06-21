param(
    [Parameter(Mandatory = $true)]
    [string]$AppExecutable,

    [Parameter(Mandatory = $true)]
    [string]$DataDirectory,

    [string]$VfsSmokeDataDirectory = "",

    [string]$LocalRoot = "S:\CottonSyncVfsQa\root"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $AppExecutable)) {
    throw "Desktop app executable was not found: $AppExecutable."
}

if ([string]::IsNullOrWhiteSpace($VfsSmokeDataDirectory)) {
    $VfsSmokeDataDirectory = Join-Path $DataDirectory "vfs-self-test-truthfulness"
}

New-Item -ItemType Directory -Path $DataDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $VfsSmokeDataDirectory -Force | Out-Null

function Invoke-CottonDesktopCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [Parameter(Mandatory = $true)]
        [string]$StdoutPath,

        [Parameter(Mandatory = $true)]
        [string]$StderrPath
    )

    $process = Start-Process `
        -FilePath $AppExecutable `
        -ArgumentList $Arguments `
        -RedirectStandardOutput $StdoutPath `
        -RedirectStandardError $StderrPath `
        -Wait `
        -PassThru

    $stdout = if (Test-Path -LiteralPath $StdoutPath) { Get-Content -LiteralPath $StdoutPath } else { @() }
    $stderr = if (Test-Path -LiteralPath $StderrPath) { Get-Content -LiteralPath $StderrPath } else { @() }

    return [PSCustomObject]@{
        ExitCode = $process.ExitCode
        Stdout = @($stdout)
        Stderr = @($stderr)
    }
}

function Write-CommandOutput {
    param(
        [Parameter(Mandatory = $true)]
        [PSCustomObject]$Result
    )

    $Result.Stdout | ForEach-Object { Write-Host $_ }
    $Result.Stderr | ForEach-Object { Write-Host $_ }
}

$selfTestResult = Invoke-CottonDesktopCommand `
    -Arguments @("--self-test", "--data-dir", $DataDirectory) `
    -StdoutPath (Join-Path $DataDirectory "cloud-files-self-test.stdout.log") `
    -StderrPath (Join-Path $DataDirectory "cloud-files-self-test.stderr.log")

$windowsVirtualFilesLine = $selfTestResult.Stdout |
    Where-Object { $_ -match '^\[(OK|SKIP|FAIL)\] Windows virtual files - ' } |
    Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($windowsVirtualFilesLine)) {
    Write-CommandOutput -Result $selfTestResult
    throw "Windows virtual files self-test item was not found."
}

$windowsVirtualFilesStatus = [regex]::Match($windowsVirtualFilesLine, '^\[(OK|SKIP|FAIL)\]').Groups[1].Value

if ($selfTestResult.ExitCode -ne 0) {
    Write-CommandOutput -Result $selfTestResult
    throw "Desktop self-test exited with code $($selfTestResult.ExitCode)."
}

$vfsSmokeResult = Invoke-CottonDesktopCommand `
    -Arguments @("--windows-virtual-files-smoke", "--data-dir", $VfsSmokeDataDirectory, "--local-root", $LocalRoot) `
    -StdoutPath (Join-Path $VfsSmokeDataDirectory "cloud-files-vfs-smoke.stdout.log") `
    -StderrPath (Join-Path $VfsSmokeDataDirectory "cloud-files-vfs-smoke.stderr.log")

$vfsSmokePassed = $vfsSmokeResult.ExitCode -eq 0 -and ($vfsSmokeResult.Stdout | Where-Object { $_ -eq "Result: passed" } | Select-Object -First 1)
$vfsSmokeFailed = $vfsSmokeResult.ExitCode -ne 0 -or ($vfsSmokeResult.Stdout | Where-Object { $_ -eq "Result: failed" } | Select-Object -First 1)

if ($vfsSmokePassed -and $windowsVirtualFilesStatus -ne "OK") {
    Write-CommandOutput -Result $selfTestResult
    Write-CommandOutput -Result $vfsSmokeResult
    throw "Windows virtual files self-test reported '$windowsVirtualFilesStatus' even though the VFS smoke passed."
}

if ($vfsSmokeFailed -and $windowsVirtualFilesStatus -eq "OK") {
    Write-CommandOutput -Result $selfTestResult
    Write-CommandOutput -Result $vfsSmokeResult
    throw "Windows virtual files self-test reported OK even though the VFS smoke failed."
}

if (-not $vfsSmokePassed -and -not $vfsSmokeFailed) {
    Write-CommandOutput -Result $vfsSmokeResult
    throw "Windows virtual files smoke result was inconclusive."
}

Write-Host "Verified Cloud Files self-test truthfulness: selfTest=$windowsVirtualFilesStatus; vfsSmokeExit=$($vfsSmokeResult.ExitCode)."
