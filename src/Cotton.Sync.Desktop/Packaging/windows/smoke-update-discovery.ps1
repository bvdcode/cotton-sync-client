param(
    [Parameter(Mandatory = $true)]
    [string]$AppExecutable,

    [Parameter(Mandatory = $true)]
    [string]$DataDirectory,

    [string]$ExpectedUpdateVersion = ""
)

$ErrorActionPreference = "Stop"

function Get-NextPatchVersion {
    param([Parameter(Mandatory = $true)][string]$Executable)

    $productVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-Path -LiteralPath $Executable)).ProductVersion
    if ([string]::IsNullOrWhiteSpace($productVersion)) {
        $productVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-Path -LiteralPath $Executable)).FileVersion
    }

    $normalized = $productVersion.Split("+")[0].Split("-")[0]
    $parts = $normalized.Split(".")
    if ($parts.Length -lt 3) {
        throw "Cannot derive update smoke version from executable version '$productVersion'."
    }

    $major = [int]$parts[0]
    $minor = [int]$parts[1]
    $patch = [int]$parts[2]
    return "$major.$minor.$($patch + 1)"
}

function Get-FreeTcpPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

function Wait-ForManifest {
    param([Parameter(Mandatory = $true)][string]$ManifestUrl)

    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        try {
            $response = Invoke-WebRequest -Uri $ManifestUrl -UseBasicParsing -TimeoutSec 2
            if ($response.StatusCode -eq 200) {
                return
            }
        }
        catch {
            Start-Sleep -Milliseconds 200
        }
    }

    throw "Mock update manifest did not become reachable at $ManifestUrl."
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

function Start-ProcessNoShell {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.Arguments = Join-ProcessArguments -Arguments $Arguments

    return [System.Diagnostics.Process]::Start($startInfo)
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

if (-not (Test-Path -LiteralPath $AppExecutable)) {
    throw "Desktop app executable was not found: $AppExecutable."
}

if ([string]::IsNullOrWhiteSpace($ExpectedUpdateVersion)) {
    $ExpectedUpdateVersion = Get-NextPatchVersion -Executable $AppExecutable
}

New-Item -ItemType Directory -Path $DataDirectory -Force | Out-Null
$releaseDirectory = Join-Path $DataDirectory "mock-update-release"
$stdoutPath = Join-Path $DataDirectory "update-discovery.stdout.log"
$stderrPath = Join-Path $DataDirectory "update-discovery.stderr.log"
New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null

$port = Get-FreeTcpPort
$baseUrl = "http://127.0.0.1:$port"
$installerName = "CottonSync-Windows-Setup.exe"
$installerPath = Join-Path $releaseDirectory $installerName
$installerBytes = [System.Text.Encoding]::UTF8.GetBytes("Cotton Sync update discovery smoke installer $ExpectedUpdateVersion`n")
[System.IO.File]::WriteAllBytes($installerPath, $installerBytes)
$installerHash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
$installerSize = (Get-Item -LiteralPath $installerPath).Length
$manifestPath = Join-Path $releaseDirectory "release-manifest.json"
$manifestUrl = "$baseUrl/release-manifest.json"

$manifest = [ordered]@{
    schemaVersion = 1
    product = "Cotton Sync"
    version = $ExpectedUpdateVersion
    tag = "v$ExpectedUpdateVersion"
    commit = "0000000000000000000000000000000000000000"
    branch = "main"
    releaseUrl = "$baseUrl/releases/v$ExpectedUpdateVersion"
    assets = @(
        [ordered]@{
            name = $installerName
            sha256 = $installerHash
            sizeBytes = $installerSize
            url = "$baseUrl/$installerName"
        }
    )
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

$pythonCommand = Get-Command python -ErrorAction SilentlyContinue
if ($null -eq $pythonCommand) {
    $pythonCommand = Get-Command python3 -ErrorAction SilentlyContinue
}

if ($null -eq $pythonCommand) {
    throw "Python was not found; update discovery smoke needs python -m http.server for the local mock release."
}

$serverProcess = Start-ProcessNoShell `
    -FilePath $pythonCommand.Source `
    -Arguments @("-m", "http.server", $port.ToString(), "--bind", "127.0.0.1", "--directory", $releaseDirectory)

try {
    Wait-ForManifest -ManifestUrl $manifestUrl

    $smokeExitCode = Invoke-ProcessCapture `
        -FilePath $AppExecutable `
        -Arguments @(
            "--update-discovery-smoke",
            "--data-dir",
            $DataDirectory,
            "--update-manifest-url",
            $manifestUrl,
            "--expected-update-version",
            $ExpectedUpdateVersion
        ) `
        -StandardOutputPath $stdoutPath `
        -StandardErrorPath $stderrPath

    $smokeOutput = if (Test-Path -LiteralPath $stdoutPath) { Get-Content -LiteralPath $stdoutPath } else { @() }
    $smokeError = if (Test-Path -LiteralPath $stderrPath) { Get-Content -LiteralPath $stderrPath } else { @() }
    $smokeOutput | ForEach-Object { Write-Host $_ }

    if ($smokeExitCode -ne 0) {
        $smokeError | ForEach-Object { Write-Host $_ }
        throw "Update discovery smoke exited with code $smokeExitCode."
    }

    $bundleLine = $smokeOutput | Where-Object { $_.StartsWith("Bundle: ") } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($bundleLine)) {
        throw "Update discovery diagnostics bundle path was not reported."
    }

    $bundlePath = $bundleLine.Substring("Bundle: ".Length)
    if (-not (Test-Path -LiteralPath $bundlePath)) {
        throw "Update discovery diagnostics bundle was not created at $bundlePath."
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
        if ($update.lastCheckStatus -ne "succeeded") {
            throw "Diagnostics update lastCheckStatus was '$($update.lastCheckStatus)', expected 'succeeded'."
        }

        if ($update.lastCheckSource -ne "download") {
            throw "Diagnostics update lastCheckSource was '$($update.lastCheckSource)', expected 'download'."
        }

        if ($update.latestVersion -ne $ExpectedUpdateVersion) {
            throw "Diagnostics latestVersion was '$($update.latestVersion)', expected '$ExpectedUpdateVersion'."
        }

        if ($update.isUpdateAvailable -ne $true) {
            throw "Diagnostics did not report an available update."
        }

        if ($update.hasInstallerAsset -ne $true -or $update.isInstallerReady -ne $true) {
            throw "Diagnostics did not report a ready installer asset."
        }

        if ($update.hasPendingUpdate -ne $true -or $update.pendingVersion -ne $ExpectedUpdateVersion) {
            throw "Diagnostics did not report the pending update version."
        }

        if ([int64]$update.pendingInstallerSizeBytes -ne [int64]$installerSize) {
            throw "Diagnostics pending installer size was '$($update.pendingInstallerSizeBytes)', expected '$installerSize'."
        }

        $logText = ""
        foreach ($entry in $archive.Entries | Where-Object { $_.FullName.StartsWith("logs/", [System.StringComparison]::Ordinal) }) {
            $logText += Read-ZipEntryText -Archive $archive -EntryName $entry.FullName
        }

        if (-not $logText.Contains("Desktop update download completed")) {
            throw "Diagnostics logs did not include the update download completion trace."
        }
    }
    finally {
        $archive.Dispose()
    }
}
finally {
    if ($null -ne $serverProcess -and -not $serverProcess.HasExited) {
        Stop-Process -Id $serverProcess.Id -Force
        $serverProcess.WaitForExit()
    }
}

Write-Host "Verified update discovery smoke: $ExpectedUpdateVersion"
