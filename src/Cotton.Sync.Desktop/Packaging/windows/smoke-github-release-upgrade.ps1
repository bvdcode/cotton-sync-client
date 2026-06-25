param(
    [Parameter(Mandatory = $true)]
    [string]$ExpectedAppVersion,

    [string]$ReleaseTag = "",

    [string]$Repository = $env:GITHUB_REPOSITORY,

    [string]$InnoSetupCompiler = $env:INNO_SETUP_COMPILER,

    [string]$ExpectedCommit = $env:GITHUB_SHA,

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
$releaseManifest = Join-Path $releaseDir "release-manifest.json"
$releaseChecksums = Join-Path $releaseDir "release-artifact-checksums.sha256"
$expectedPrimaryAssetNames = @(
    "CottonSync-CLI-Windows.zip",
    "CottonSync-Linux.deb",
    "CottonSync-Linux.tar.gz",
    "CottonSync-Windows-Setup.exe",
    "CottonSync-Windows.tar.gz",
    "CottonSync-Windows.zip"
)
$expectedReleaseAssetNames = @($expectedPrimaryAssetNames + "release-artifact-checksums.sha256" + "release-manifest.json")

Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $releaseDir, $extractDir, $oldOutputDir, $installDir, $dataDir -Force | Out-Null

function Assert-Contains {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Content,

        [Parameter(Mandatory = $true)]
        [string]$Expected,

        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    if (-not $Content.Contains($Expected)) {
        throw "$Label did not contain expected text: $Expected"
    }
}

function Assert-StringSet {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Actual,

        [Parameter(Mandatory = $true)]
        [string[]]$Expected,

        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    $actualSorted = @($Actual | Sort-Object)
    $expectedSorted = @($Expected | Sort-Object)
    $differences = @(Compare-Object -ReferenceObject $expectedSorted -DifferenceObject $actualSorted)
    if ($differences.Count -ne 0) {
        Write-Host "$Label actual values:"
        $actualSorted | ForEach-Object { Write-Host "  $_" }
        Write-Host "$Label expected values:"
        $expectedSorted | ForEach-Object { Write-Host "  $_" }
        throw "$Label did not match the expected set."
    }
}

function Get-FileSha256 {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    return ([string](Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash).ToLowerInvariant()
}

function Get-ManifestAsset {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Manifest,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $matches = @($Manifest.assets | Where-Object { $_.name -eq $Name })
    if ($matches.Count -ne 1) {
        throw "release-manifest.json contained $($matches.Count) entries named '$Name'; expected 1."
    }

    return $matches[0]
}

function Assert-DownloadedAssetMatchesManifest {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Manifest,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path $Path)) {
        throw "Published release asset was not downloaded: $Path"
    }

    $asset = Get-ManifestAsset -Manifest $Manifest -Name $Name
    $actualHash = Get-FileSha256 -Path $Path
    $expectedHash = ([string]$asset.sha256).ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        throw "Downloaded release asset '$Name' SHA-256 was '$actualHash', expected '$expectedHash'."
    }

    $actualSize = (Get-Item -LiteralPath $Path).Length
    if ($actualSize -ne [int64]$asset.sizeBytes) {
        throw "Downloaded release asset '$Name' size was '$actualSize', expected '$($asset.sizeBytes)'."
    }

    $expectedUrlSuffix = "/releases/download/$ReleaseTag/$Name"
    if (-not ([string]$asset.url).EndsWith($expectedUrlSuffix, [System.StringComparison]::Ordinal)) {
        throw "release-manifest.json asset '$Name' URL was '$($asset.url)', expected suffix '$expectedUrlSuffix'."
    }
}

function Assert-ReleaseMetadata {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Manifest,

        [Parameter(Mandatory = $true)]
        [object]$ReleaseDetails
    )

    if ([int]$Manifest.schemaVersion -ne 1) {
        throw "release-manifest.json schemaVersion was '$($Manifest.schemaVersion)', expected '1'."
    }

    if ([string]$Manifest.version -ne $ExpectedAppVersion) {
        throw "release-manifest.json version was '$($Manifest.version)', expected '$ExpectedAppVersion'."
    }

    if ([string]$Manifest.tag -ne $ReleaseTag) {
        throw "release-manifest.json tag was '$($Manifest.tag)', expected '$ReleaseTag'."
    }

    if (-not [string]::IsNullOrWhiteSpace($ExpectedCommit) -and [string]$Manifest.commit -ne $ExpectedCommit) {
        throw "release-manifest.json commit was '$($Manifest.commit)', expected '$ExpectedCommit'."
    }

    Assert-StringSet `
        -Actual @($Manifest.assets | ForEach-Object { [string]$_.name }) `
        -Expected @($expectedReleaseAssetNames | Where-Object { $_ -ne "release-manifest.json" }) `
        -Label "release-manifest.json assets"

    Assert-StringSet `
        -Actual @($ReleaseDetails.assets | ForEach-Object { [string]$_.name }) `
        -Expected $expectedReleaseAssetNames `
        -Label "GitHub release assets"

    $body = [string]$ReleaseDetails.body
    Assert-Contains -Content $body -Expected "## Cotton Sync Client $ExpectedAppVersion" -Label "GitHub release body"
    Assert-Contains -Content $body -Expected "## Changes" -Label "GitHub release body"
    Assert-Contains -Content $body -Expected "## Assets" -Label "GitHub release body"
    Assert-Contains -Content $body -Expected "CottonSync-Windows-Setup.exe" -Label "GitHub release body"
    if (-not [string]::IsNullOrWhiteSpace($ExpectedCommit)) {
        Assert-Contains -Content $body -Expected "- Commit: ``$ExpectedCommit``" -Label "GitHub release body"
    }
}

function Assert-ChecksumFile {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Manifest,

        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path $Path)) {
        throw "Published release checksums file was not downloaded: $Path"
    }

    $checksumRows = @{}
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        if ($line -notmatch '^(?<hash>[a-fA-F0-9]{64})\s+\./(?<name>.+)$') {
            throw "release-artifact-checksums.sha256 contained an invalid line: $line"
        }

        $name = [string]$Matches["name"]
        $checksumRows[$name] = ([string]$Matches["hash"]).ToLowerInvariant()
    }

    Assert-StringSet -Actual ([string[]]$checksumRows.Keys) -Expected $expectedPrimaryAssetNames -Label "release-artifact-checksums.sha256 entries"

    foreach ($assetName in $expectedPrimaryAssetNames) {
        $manifestAsset = Get-ManifestAsset -Manifest $Manifest -Name $assetName
        $manifestHash = ([string]$manifestAsset.sha256).ToLowerInvariant()
        if ($checksumRows[$assetName] -ne $manifestHash) {
            throw "release-artifact-checksums.sha256 hash for '$assetName' was '$($checksumRows[$assetName])', expected '$manifestHash'."
        }
    }
}

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
                --pattern release-artifact-checksums.sha256 `
                --pattern release-manifest.json `
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

if (-not (Test-Path $releaseManifest)) {
    throw "GitHub release manifest was not downloaded: $releaseManifest"
}

if (-not (Test-Path $releaseChecksums)) {
    throw "GitHub release checksums file was not downloaded: $releaseChecksums"
}

$releaseDetailsJson = & gh release view $ReleaseTag --repo $Repository --json body,assets
if ($LASTEXITCODE -ne 0) {
    throw "GitHub release metadata lookup failed for $ReleaseTag."
}

$releaseDetails = $releaseDetailsJson | ConvertFrom-Json
$manifest = Get-Content -LiteralPath $releaseManifest -Raw | ConvertFrom-Json
Assert-ReleaseMetadata -Manifest $manifest -ReleaseDetails $releaseDetails
Assert-ChecksumFile -Manifest $manifest -Path $releaseChecksums
Assert-DownloadedAssetMatchesManifest -Manifest $manifest -Name "CottonSync-Windows.zip" -Path $releaseZip
Assert-DownloadedAssetMatchesManifest -Manifest $manifest -Name "CottonSync-Windows-Setup.exe" -Path $releaseInstaller
Assert-DownloadedAssetMatchesManifest -Manifest $manifest -Name "release-artifact-checksums.sha256" -Path $releaseChecksums

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
