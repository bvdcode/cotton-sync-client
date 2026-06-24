param(
    [Parameter(Mandatory = $true)]
    [string]$AppExecutable,

    [Parameter(Mandatory = $true)]
    [string]$DataDirectory
)

$ErrorActionPreference = "Stop"

function Join-ProcessArguments {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    return (($Arguments | ForEach-Object {
        if ($_ -match '[\s"]') {
            '"' + $_.Replace('"', '\"') + '"'
        }
        else {
            $_
        }
    }) -join " ")
}

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
    $startInfo.Arguments = Join-ProcessArguments -Arguments $Arguments

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    [void]$process.Start()
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    [System.IO.File]::WriteAllText($StandardOutputPath, $stdoutTask.GetAwaiter().GetResult())
    [System.IO.File]::WriteAllText($StandardErrorPath, $stderrTask.GetAwaiter().GetResult())
    return $process.ExitCode
}

function Read-ZipEntryText {
    param(
        [Parameter(Mandatory = $true)]$Archive,
        [Parameter(Mandatory = $true)][string]$EntryName
    )

    $entry = $Archive.GetEntry($EntryName)
    if ($null -eq $entry) {
        throw "Zip entry was not found: $EntryName."
    }

    $stream = $entry.Open()
    try {
        $reader = [System.IO.StreamReader]::new($stream)
        try {
            return $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

if (-not (Test-Path -LiteralPath $AppExecutable)) {
    throw "Desktop app executable was not found: $AppExecutable."
}

New-Item -ItemType Directory -Path $DataDirectory -Force | Out-Null
$installerPath = Join-Path $DataDirectory "CottonSync-Windows-Setup.cmd"
$stdoutPath = Join-Path $DataDirectory "update-install.stdout.log"
$stderrPath = Join-Path $DataDirectory "update-install.stderr.log"
Set-Content -LiteralPath $installerPath -Encoding ASCII -Value @(
    "@echo off",
    "exit /b 0"
)

$smokeExitCode = Invoke-ProcessCapture `
    -FilePath $AppExecutable `
    -Arguments @(
        "--update-install-smoke",
        "--data-dir",
        $DataDirectory,
        "--update-installer-path",
        $installerPath
    ) `
    -StandardOutputPath $stdoutPath `
    -StandardErrorPath $stderrPath

$smokeOutput = if (Test-Path -LiteralPath $stdoutPath) { Get-Content -LiteralPath $stdoutPath } else { @() }
$smokeError = if (Test-Path -LiteralPath $stderrPath) { Get-Content -LiteralPath $stderrPath } else { @() }
$smokeOutput | ForEach-Object { Write-Host $_ }

if ($smokeExitCode -ne 0) {
    $smokeError | ForEach-Object { Write-Host $_ }
    throw "Update install smoke exited with code $smokeExitCode."
}

if (-not ($smokeOutput | Where-Object { $_ -eq "Result: passed" } | Select-Object -First 1)) {
    throw "Update install smoke did not report a passed result."
}

$bundleLine = $smokeOutput | Where-Object { $_.StartsWith("PASS: Diagnostics bundle records installer launch outcome") } | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($bundleLine)) {
    throw "Update install diagnostics bundle check was not reported."
}

$bundlePrefix = "bundle="
$bundleIndex = $bundleLine.IndexOf($bundlePrefix, [StringComparison]::Ordinal)
if ($bundleIndex -lt 0) {
    throw "Update install diagnostics bundle path was not reported."
}

$bundlePath = $bundleLine.Substring($bundleIndex + $bundlePrefix.Length)
if (-not (Test-Path -LiteralPath $bundlePath)) {
    throw "Update install diagnostics bundle was not created at $bundlePath."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($bundlePath)
try {
    $diagnosticsJson = Read-ZipEntryText -Archive $archive -EntryName "diagnostics.json"
    $diagnostics = $diagnosticsJson | ConvertFrom-Json
    if ($null -eq $diagnostics.update) {
        throw "Diagnostics update metadata was not found."
    }

    $update = $diagnostics.update
    if ($update.lastInstallLaunchStatus -ne "launched") {
        throw "Diagnostics lastInstallLaunchStatus was '$($update.lastInstallLaunchStatus)', expected 'launched'."
    }

    if ($null -eq $update.lastInstallProcessId -or $update.lastInstallProcessId -le 0) {
        throw "Diagnostics lastInstallProcessId was not a positive process id."
    }

    if ($update.lastInstallExitedDuringStartupProbe -ne $true) {
        throw "Diagnostics lastInstallExitedDuringStartupProbe was not true for the fake installer."
    }

    if ($update.lastInstallExitCode -ne 0) {
        throw "Diagnostics lastInstallExitCode was '$($update.lastInstallExitCode)', expected 0."
    }
}
finally {
    $archive.Dispose()
}

Write-Host "Verified installed update install handoff."
