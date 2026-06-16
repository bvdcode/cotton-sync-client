$ErrorActionPreference = "Stop"

$nextVersion = "0.5.0"
if (Test-Path "GitVersion.yml") {
    $configuredVersion = Get-Content "GitVersion.yml" |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -match "^next-version:\s*(?<version>\S+)\s*$" } |
        Select-Object -First 1

    if ($configuredVersion -match "^next-version:\s*(?<version>\S+)\s*$") {
        $nextVersion = $Matches.version
    }
}

if ($env:GITHUB_REF_TYPE -eq "tag" -and $env:GITHUB_REF_NAME -match "^v?(?<version>\d+\.\d+\.\d+([-.+][0-9A-Za-z.-]+)?)$") {
    $version = $Matches.version
}
else {
    $parts = $nextVersion.Split(".")
    if ($parts.Length -lt 3) {
        throw "GitVersion.yml next-version must use major.minor.patch format."
    }

    $runNumber = $env:GITHUB_RUN_NUMBER
    if ([string]::IsNullOrWhiteSpace($runNumber)) {
        $runNumber = (git rev-list --count HEAD).Trim()
    }

    $version = "$($parts[0]).$($parts[1]).$runNumber"
}

if ($env:GITHUB_OUTPUT) {
    "SemVer=$version" >> $env:GITHUB_OUTPUT
    "semVer=$version" >> $env:GITHUB_OUTPUT
}

Write-Host "Version: $version"
