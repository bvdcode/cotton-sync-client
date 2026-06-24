param(
    [string]$ExpectedExecutablePath = "",

    [switch]$ExpectAbsent
)

$ErrorActionPreference = "Stop"

$verbSubKeys = @(
    "Software\Classes\*\shell\CottonSyncCopyShareLink",
    "Software\Classes\Directory\shell\CottonSyncCopyShareLink"
)

function Normalize-ShellVerbName {
    param([string]$Name)

    return ($Name -replace "&", "").Trim()
}

function Open-CurrentUserSubKey {
    param([string]$SubKey)

    return [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($SubKey)
}

function Assert-KeyAbsent {
    param([string]$SubKey)

    $key = Open-CurrentUserSubKey -SubKey $SubKey
    if ($null -ne $key) {
        $key.Dispose()
        throw "Shell share-link verb registry key remained after uninstall: HKCU\$SubKey"
    }
}

function Assert-KeyPresent {
    param(
        [string]$SubKey,
        [string]$ExpectedExecutablePath
    )

    $key = Open-CurrentUserSubKey -SubKey $SubKey
    if ($null -eq $key) {
        throw "Shell share-link verb registry key was not installed: HKCU\$SubKey"
    }

    try {
        $label = [string]$key.GetValue("")
        $icon = [string]$key.GetValue("Icon")
        if ($label -ne "Copy Cotton Cloud share link") {
            throw "Shell share-link verb label was '$label'."
        }

        if (-not [string]::Equals($icon, $ExpectedExecutablePath, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Shell share-link verb icon was '$icon', expected '$ExpectedExecutablePath'."
        }

        $command = $key.OpenSubKey("command")
        if ($null -eq $command) {
            throw "Shell share-link verb command key was not installed: HKCU\$SubKey\command"
        }

        try {
            $actualCommand = [string]$command.GetValue("")
            $expectedCommand = "`"$ExpectedExecutablePath`" --copy-shell-share-link `"%1`""
            if (-not [string]::Equals($actualCommand, $expectedCommand, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Shell share-link verb command was '$actualCommand', expected '$expectedCommand'."
            }
        } finally {
            $command.Dispose()
        }
    } finally {
        $key.Dispose()
    }
}

function Get-ShellVerbNames {
    param([string]$Path)

    $resolvedPath = (Resolve-Path -LiteralPath $Path).Path
    $parentPath = Split-Path -LiteralPath $resolvedPath -Parent
    $leafName = Split-Path -LiteralPath $resolvedPath -Leaf
    $shell = New-Object -ComObject Shell.Application
    $folder = $shell.Namespace($parentPath)
    if ($null -eq $folder) {
        throw "Shell namespace could not open probe parent."
    }

    $item = $folder.ParseName($leafName)
    if ($null -eq $item) {
        throw "Shell namespace could not resolve probe item."
    }

    $names = New-Object System.Collections.Generic.List[string]
    foreach ($verb in $item.Verbs()) {
        $names.Add((Normalize-ShellVerbName -Name ([string]$verb.Name)))
    }

    return $names
}

function Assert-ShellVerbVisible {
    param(
        [string]$Path,
        [string]$ExpectedLabel
    )

    $names = Get-ShellVerbNames -Path $Path
    foreach ($name in $names) {
        if ([string]::Equals($name, $ExpectedLabel, [StringComparison]::Ordinal)) {
            return
        }
    }

    throw "Explorer shell did not expose '$ExpectedLabel' for the probe item."
}

function Assert-ShellVerbVisibility {
    $probeRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("cotton-shell-share-link-" + [Guid]::NewGuid().ToString("N"))
    $probeFile = Join-Path $probeRoot "synced-file.txt"
    $probeDirectory = Join-Path $probeRoot "SyncedFolder"
    New-Item -ItemType Directory -Path $probeRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $probeDirectory -Force | Out-Null
    Set-Content -LiteralPath $probeFile -Value "share-link smoke" -Encoding UTF8

    try {
        Assert-ShellVerbVisible -Path $probeFile -ExpectedLabel "Copy Cotton Cloud share link"
        Assert-ShellVerbVisible -Path $probeDirectory -ExpectedLabel "Copy Cotton Cloud share link"
    } finally {
        Remove-Item -LiteralPath $probeRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($ExpectAbsent) {
    foreach ($subKey in $verbSubKeys) {
        Assert-KeyAbsent -SubKey $subKey
    }

    Write-Host "Verified installed shell share-link verbs were removed."
    return
}

if ([string]::IsNullOrWhiteSpace($ExpectedExecutablePath)) {
    throw "ExpectedExecutablePath is required unless ExpectAbsent is set."
}

$resolvedExecutable = (Resolve-Path -LiteralPath $ExpectedExecutablePath).Path
foreach ($subKey in $verbSubKeys) {
    Assert-KeyPresent -SubKey $subKey -ExpectedExecutablePath $resolvedExecutable
}

Assert-ShellVerbVisibility

Write-Host "Verified installed shell share-link verbs and Explorer visibility."
