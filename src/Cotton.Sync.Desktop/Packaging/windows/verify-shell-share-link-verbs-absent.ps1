$ErrorActionPreference = "Stop"

$verbSubKeys = @(
    "Software\Classes\*\shell\CottonSyncCopyShareLink",
    "Software\Classes\Directory\shell\CottonSyncCopyShareLink"
)

foreach ($subKey in $verbSubKeys) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey)
    if ($null -eq $key) {
        continue
    }

    $key.Dispose()
    throw "Global shell share-link verb is registered: HKCU\$subKey"
}

Write-Host "Verified global shell share-link verbs are absent."
