# SPDX-License-Identifier: MIT
# Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

param(
    [string]$ReportPath = ""
)

$ErrorActionPreference = "Stop"

$providerId = "Cotton.Sync.Desktop"
$providerDisplayName = "Cotton Cloud"
$legacyProviderDisplayName = "Cotton Sync"
$patterns = @(
    $providerId,
    $providerDisplayName,
    $legacyProviderDisplayName
)

$remainingRegistrations = New-Object System.Collections.Generic.List[string]

function Write-CleanupReport {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Result,

        [Parameter(Mandatory = $true)]
        [System.Collections.Generic.List[string]]$Registrations
    )

    if ([string]::IsNullOrWhiteSpace($ReportPath)) {
        return
    }

    $directory = Split-Path -Parent $ReportPath
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("Result: $Result")
    $lines.Add("RemainingRegistrationCount: $($Registrations.Count)")
    foreach ($registration in $Registrations) {
        $lines.Add("RemainingRegistration: $registration")
    }

    $lines | Set-Content -LiteralPath $ReportPath -Encoding utf8
}

function Test-ContainsProviderPattern {
    param([object]$Value)

    if ($null -eq $Value) {
        return $false
    }

    $textValues = New-Object System.Collections.Generic.List[string]
    if ($Value -is [string[]]) {
        foreach ($item in $Value) {
            $textValues.Add([string]$item)
        }
    } elseif ($Value -is [byte[]]) {
        $textValues.Add([System.Text.Encoding]::UTF8.GetString($Value))
        $textValues.Add([System.Text.Encoding]::Unicode.GetString($Value))
    } else {
        $textValues.Add([string]$Value)
    }

    foreach ($text in $textValues) {
        foreach ($pattern in $patterns) {
            if ($text.IndexOf($pattern, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                return $true
            }
        }
    }

    return $false
}

function Add-RegistrationMatch {
    param(
        [string]$Path,
        [string]$Reason
    )

    $remainingRegistrations.Add($Path + " :: " + $Reason)
}

function Open-CurrentUserSubKey {
    param([string]$SubKey)

    return [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($SubKey)
}

function Test-RegistryValueMatches {
    param(
        [Microsoft.Win32.RegistryKey]$Key,
        [string]$DisplayPath
    )

    if (Test-ContainsProviderPattern -Value $Key.Name) {
        Add-RegistrationMatch -Path $DisplayPath -Reason "key name contains provider marker"
    }

    $defaultValue = $Key.GetValue("")
    if (Test-ContainsProviderPattern -Value $defaultValue) {
        Add-RegistrationMatch -Path $DisplayPath -Reason "default value '$defaultValue'"
    }

    foreach ($valueName in $Key.GetValueNames()) {
        $value = $Key.GetValue($valueName)
        if (Test-ContainsProviderPattern -Value $valueName) {
            Add-RegistrationMatch -Path $DisplayPath -Reason "value name '$valueName'"
        }

        if (Test-ContainsProviderPattern -Value $value) {
            Add-RegistrationMatch -Path $DisplayPath -Reason "value '$valueName' contains provider marker"
        }
    }
}

function Test-RegistryTree {
    param(
        [string]$SubKey,
        [string]$DisplayPath
    )

    $rootKey = Open-CurrentUserSubKey -SubKey $SubKey
    if ($null -eq $rootKey) {
        return
    }

    $pending = New-Object System.Collections.Generic.Stack[object]
    $pending.Push([pscustomobject]@{
        Key = $rootKey
        Path = $DisplayPath
    })

    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        $key = [Microsoft.Win32.RegistryKey]$current.Key
        $path = [string]$current.Path
        try {
            Test-RegistryValueMatches -Key $key -DisplayPath $path
            foreach ($subKeyName in $key.GetSubKeyNames()) {
                $subKey = $key.OpenSubKey($subKeyName)
                if ($null -eq $subKey) {
                    continue
                }

                $pending.Push([pscustomobject]@{
                    Key = $subKey
                    Path = $path + "\" + $subKeyName
                })
            }
        } finally {
            $key.Dispose()
        }
    }
}

function Test-ShellNamespaceRoots {
    $subKey = "Software\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace"
    $namespaceKey = Open-CurrentUserSubKey -SubKey $subKey
    if ($null -eq $namespaceKey) {
        return
    }

    try {
        foreach ($subKeyName in $namespaceKey.GetSubKeyNames()) {
            $child = $namespaceKey.OpenSubKey($subKeyName)
            if ($null -eq $child) {
                continue
            }

            try {
                Test-RegistryValueMatches -Key $child -DisplayPath ("HKCU:\" + $subKey + "\" + $subKeyName)
            } finally {
                $child.Dispose()
            }
        }
    } finally {
        $namespaceKey.Dispose()
    }
}

function Test-ClassIdRoots {
    param(
        [string]$SubKey,
        [string]$DisplayPath
    )

    $classIdRoot = Open-CurrentUserSubKey -SubKey $SubKey
    if ($null -eq $classIdRoot) {
        return
    }

    try {
        foreach ($classId in $classIdRoot.GetSubKeyNames()) {
            $classKey = $classIdRoot.OpenSubKey($classId)
            if ($null -eq $classKey) {
                continue
            }

            try {
                $classPath = $DisplayPath + "\" + $classId
                Test-RegistryValueMatches -Key $classKey -DisplayPath $classPath

                foreach ($relativeSubKey in @("DefaultIcon", "Instance", "Instance\InitPropertyBag")) {
                    $child = $classKey.OpenSubKey($relativeSubKey)
                    if ($null -eq $child) {
                        continue
                    }

                    try {
                        Test-RegistryValueMatches -Key $child -DisplayPath ($classPath + "\" + $relativeSubKey)
                    } finally {
                        $child.Dispose()
                    }
                }
            } finally {
                $classKey.Dispose()
            }
        }
    } finally {
        $classIdRoot.Dispose()
    }
}

Test-RegistryTree `
    -SubKey "Software\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager" `
    -DisplayPath "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager"
Test-ShellNamespaceRoots
Test-ClassIdRoots `
    -SubKey "Software\Classes\CLSID" `
    -DisplayPath "HKCU:\Software\Classes\CLSID"
Test-ClassIdRoots `
    -SubKey "Software\Classes\WOW6432Node\CLSID" `
    -DisplayPath "HKCU:\Software\Classes\WOW6432Node\CLSID"

if ($remainingRegistrations.Count -ne 0) {
    Write-CleanupReport -Result "failed" -Registrations $remainingRegistrations
    foreach ($registration in $remainingRegistrations) {
        Write-Host $registration
    }

    throw "Cloud Files or Explorer registration remained after uninstall."
}

Write-CleanupReport -Result "passed" -Registrations $remainingRegistrations
Write-Host "Verified Cloud Files and Explorer registrations were removed after uninstall."
