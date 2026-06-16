param(
    [Parameter(Mandatory = $true)]
    [string]$ExpectedAppVersion,

    [string]$ReleaseTag = "",

    [string]$Repository = $env:GITHUB_REPOSITORY,

    [string]$InnoSetupCompiler = $env:INNO_SETUP_COMPILER,

    [string]$Workspace = (Get-Location).Path
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($Repository)) {
    throw "Repository is required."
}

if ([string]::IsNullOrWhiteSpace($ReleaseTag)) {
    $ReleaseTag = "v$ExpectedAppVersion"
}

if ([string]::IsNullOrWhiteSpace($InnoSetupCompiler) -or -not (Test-Path $InnoSetupCompiler)) {
    throw "Inno Setup compiler was not found: $InnoSetupCompiler"
}

$oldAppVersion = $ExpectedAppVersion + "-ci-github-upgrade"
$root = Join-Path $env:RUNNER_TEMP "cotton-sync-github-release-upgrade"
$releaseDir = Join-Path $root "release"
$extractDir = Join-Path $root "extract"
$oldOutputDir = Join-Path $root "old-installer"
$installDir = Join-Path $root "installed"
$dataDir = Join-Path $root "data"
$oldInstallLog = Join-Path $root "old-install.log"
$upgradeLog = Join-Path $root "github-release-upgrade.log"
$uninstallLog = Join-Path $root "uninstall.log"

Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $releaseDir, $extractDir, $oldOutputDir, $installDir, $dataDir -Force | Out-Null

function Invoke-Process {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string[]]$ArgumentList,

        [Parameter(Mandatory = $true)]
        [string]$FailureMessage,

        [string]$LogPath = ""
    )

    $process = Start-Process -FilePath $FilePath -ArgumentList $ArgumentList -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        if (-not [string]::IsNullOrWhiteSpace($LogPath)) {
            Get-Content $LogPath -ErrorAction SilentlyContinue
        }

        throw "$FailureMessage Exit code: $($process.ExitCode)."
    }
}

function Download-ReleaseAssets {
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            gh release download $ReleaseTag `
                --repo $Repository `
                --pattern CottonSync-Windows.zip `
                --pattern CottonSync-Windows-Setup.exe `
                --dir $releaseDir `
                --clobber
            return
        } catch {
            if ($attempt -eq 5) {
                throw
            }

            Write-Host "GitHub release asset download failed on attempt $attempt; retrying."
            Start-Sleep -Seconds ([Math]::Min(20, 2 * $attempt))
        }
    }
}

Download-ReleaseAssets

$releaseZip = Join-Path $releaseDir "CottonSync-Windows.zip"
$releaseInstaller = Join-Path $releaseDir "CottonSync-Windows-Setup.exe"
if (-not (Test-Path $releaseZip)) {
    throw "GitHub release Windows zip was not downloaded: $releaseZip"
}

if (-not (Test-Path $releaseInstaller)) {
    throw "GitHub release Windows installer was not downloaded: $releaseInstaller"
}

Expand-Archive -LiteralPath $releaseZip -DestinationPath $extractDir -Force
$releaseExecutable = Join-Path $extractDir "Cotton.Sync.Desktop.exe"
if (-not (Test-Path $releaseExecutable)) {
    throw "GitHub release Windows zip did not contain Cotton.Sync.Desktop.exe."
}

$installerScript = Join-Path $Workspace "src/Cotton.Sync.Desktop/Packaging/windows/cotton-sync.iss"
$iconFile = Join-Path $Workspace "src/Cotton.Sync.Desktop/Assets/app.ico"
& $InnoSetupCompiler `
    "/DSourceDir=$extractDir" `
    "/DIconFile=$iconFile" `
    "/DOutputDir=$oldOutputDir" `
    "/DAppVersion=$oldAppVersion" `
    "/DOutputBaseFilename=cotton-sync-desktop-win-x64-$oldAppVersion-setup" `
    $installerScript

$oldInstaller = Join-Path $oldOutputDir "cotton-sync-desktop-win-x64-$oldAppVersion-setup.exe"
if (-not (Test-Path $oldInstaller)) {
    throw "Old Windows installer was not created: $oldInstaller"
}

Invoke-Process `
    -FilePath $oldInstaller `
    -ArgumentList @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/TASKS=", "/DIR=$installDir", "/LOG=$oldInstallLog") `
    -FailureMessage "Old Windows installer failed." `
    -LogPath $oldInstallLog

$installedExe = Join-Path $installDir "Cotton.Sync.Desktop.exe"
if (-not (Test-Path $installedExe)) {
    throw "Old installed desktop executable was not found."
}

Invoke-Process `
    -FilePath $releaseInstaller `
    -ArgumentList @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/TASKS=", "/DIR=$installDir", "/LOG=$upgradeLog") `
    -FailureMessage "GitHub release Windows installer upgrade failed." `
    -LogPath $upgradeLog

if (-not (Test-Path $installedExe)) {
    throw "Upgraded desktop executable was not found."
}

$versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($installedExe)
$actualVersion = ([string]$versionInfo.ProductVersion).Trim()
$metadataStart = $actualVersion.IndexOf('+')
if ($metadataStart -gt 0) {
    $actualVersion = $actualVersion.Substring(0, $metadataStart)
}

if ($actualVersion -ne $ExpectedAppVersion) {
    throw "Upgraded desktop executable product version was '$actualVersion', expected '$ExpectedAppVersion'."
}

& (Join-Path $Workspace "src/Cotton.Sync.Desktop/Packaging/windows/smoke-diagnostics-export.ps1") `
    -AppExecutable $installedExe `
    -DataDirectory $dataDir `
    -ExpectedAppVersion $ExpectedAppVersion

$uninstaller = Join-Path $installDir "unins000.exe"
if (-not (Test-Path $uninstaller)) {
    throw "Windows uninstaller was not found after GitHub release upgrade."
}

Invoke-Process `
    -FilePath $uninstaller `
    -ArgumentList @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG=$uninstallLog") `
    -FailureMessage "Windows uninstaller failed after GitHub release upgrade." `
    -LogPath $uninstallLog

$remainingInstallItems = @(Get-ChildItem -Path $installDir -Force -Recurse -ErrorAction SilentlyContinue)
if ($remainingInstallItems.Count -ne 0) {
    $remainingInstallItems | ForEach-Object { Write-Host $_.FullName }
    throw "Install directory was not empty after GitHub release upgrade cleanup."
}

Write-Host "Verified GitHub release Windows installer upgrade from $oldAppVersion to $ExpectedAppVersion."
