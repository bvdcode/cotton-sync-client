$ErrorActionPreference = "Stop"

$nextVersion = "0.0.1"
if (Test-Path "GitVersion.yml") {
    $settings = @{}
    Get-Content "GitVersion.yml" |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -match "^(?<name>[A-Za-z0-9_-]+):\s*(?<value>\S+)\s*$" } |
        ForEach-Object {
            $settings[$Matches.name] = $Matches.value
        }

    if ($settings.ContainsKey("next-version")) {
        $nextVersion = $settings["next-version"]
    }
}

if ($env:GITHUB_REF_TYPE -eq "tag" -and $env:GITHUB_REF_NAME -match "^v?(?<version>\d+\.\d+\.\d+([-.+][0-9A-Za-z.-]+)?)$") {
    $version = $Matches.version
}
else {
    $parts = $nextVersion.Split(".")
    if ($parts.Length -ne 3) {
        throw "GitVersion.yml next-version must use major.minor.patch format."
    }

    $version = $nextVersion
}

if ($env:GITHUB_OUTPUT) {
    "SemVer=$version" >> $env:GITHUB_OUTPUT
    "semVer=$version" >> $env:GITHUB_OUTPUT
}

Write-Host "Version: $version"
