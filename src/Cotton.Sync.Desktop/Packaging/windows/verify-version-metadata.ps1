param(
    [Parameter(Mandatory = $true)]
    [string]$Executable,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedProductVersion,

    [string]$Label = "Executable"
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ExpectedProductVersion)) {
    throw "ExpectedProductVersion must not be empty."
}

if (-not (Test-Path -LiteralPath $Executable)) {
    throw "$Label was not found: $Executable."
}

function Remove-VersionMetadata {
    param([Parameter(Mandatory = $true)][string]$Version)

    $trimmed = $Version.Trim()
    $metadataStart = $trimmed.IndexOf('+')
    if ($metadataStart -gt 0) {
        return $trimmed.Substring(0, $metadataStart)
    }

    return $trimmed
}

function Get-SemVerCore {
    param([Parameter(Mandatory = $true)][string]$Version)

    $withoutMetadata = Remove-VersionMetadata $Version
    $prereleaseStart = $withoutMetadata.IndexOf('-')
    if ($prereleaseStart -gt 0) {
        $withoutMetadata = $withoutMetadata.Substring(0, $prereleaseStart)
    }

    if ($withoutMetadata -notmatch '^(\d+)\.(\d+)\.(\d+)$') {
        throw "Version '$Version' does not contain a three-part semantic version core."
    }

    return $withoutMetadata
}

$resolvedExecutable = (Resolve-Path -LiteralPath $Executable).Path
$versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($resolvedExecutable)

$actualProductVersion = Remove-VersionMetadata ([string]$versionInfo.ProductVersion)
$expectedProductVersionWithoutMetadata = Remove-VersionMetadata $ExpectedProductVersion
if ($actualProductVersion -ne $expectedProductVersionWithoutMetadata) {
    throw "$Label ProductVersion was '$actualProductVersion', expected '$expectedProductVersionWithoutMetadata'."
}

$expectedFileVersionCore = Get-SemVerCore $ExpectedProductVersion
$actualFileVersionCore = "$($versionInfo.FileMajorPart).$($versionInfo.FileMinorPart).$($versionInfo.FileBuildPart)"
if ($actualFileVersionCore -ne $expectedFileVersionCore) {
    throw "$Label FileVersion was '$($versionInfo.FileVersion)', expected core '$expectedFileVersionCore'."
}

Write-Host "Verified $Label version metadata: ProductVersion=$($versionInfo.ProductVersion); FileVersion=$($versionInfo.FileVersion)"
