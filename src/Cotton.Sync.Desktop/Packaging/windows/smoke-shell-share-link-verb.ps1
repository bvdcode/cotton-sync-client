param(
    [string]$ExpectedExecutablePath = "",

    [switch]$ExpectAbsent
)

$ErrorActionPreference = "Stop"

$verbSubKeys = @(
    "Software\Classes\*\shell\CottonSyncCopyShareLink",
    "Software\Classes\Directory\shell\CottonSyncCopyShareLink"
)

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

Write-Host "Verified installed shell share-link verbs."
