$ErrorActionPreference = "Stop"

$nextVersion = "0.0.1"
$runNumberOffset = 0
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

    if ($settings.ContainsKey("version-run-number-offset")) {
        $runNumberOffset = [int]$settings["version-run-number-offset"]
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

    $runNumber = $env:GITHUB_RUN_NUMBER
    if ([string]::IsNullOrWhiteSpace($runNumber)) {
        $version = $nextVersion
    }
    else {
        $runNumberValue = [int]$runNumber
        $patch = [int]$parts[2] + [Math]::Max(0, $runNumberValue - $runNumberOffset - 1)
        $version = "$($parts[0]).$($parts[1]).$patch"
    }
}

if ($env:GITHUB_OUTPUT) {
    "SemVer=$version" >> $env:GITHUB_OUTPUT
    "semVer=$version" >> $env:GITHUB_OUTPUT
}

Write-Host "Version: $version"
