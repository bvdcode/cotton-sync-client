param(
    [Parameter(Mandatory = $true)]
    [string]$AppExecutable,

    [Parameter(Mandatory = $true)]
    [string]$DataDirectory
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $AppExecutable)) {
    throw "Desktop app executable was not found: $AppExecutable."
}

New-Item -ItemType Directory -Path $DataDirectory -Force | Out-Null

$stdoutPath = Join-Path $DataDirectory "notification-identity.stdout.log"
$stderrPath = Join-Path $DataDirectory "notification-identity.stderr.log"
$diagnosticsProcess = Start-Process `
    -FilePath $AppExecutable `
    -ArgumentList @("--export-diagnostics", "--data-dir", $DataDirectory) `
    -RedirectStandardOutput $stdoutPath `
    -RedirectStandardError $stderrPath `
    -Wait `
    -PassThru

$diagnosticsOutput = if (Test-Path -LiteralPath $stdoutPath) { Get-Content -LiteralPath $stdoutPath } else { @() }
$diagnosticsError = if (Test-Path -LiteralPath $stderrPath) { Get-Content -LiteralPath $stderrPath } else { @() }
if ($diagnosticsProcess.ExitCode -ne 0) {
    $diagnosticsOutput | ForEach-Object { Write-Host $_ }
    $diagnosticsError | ForEach-Object { Write-Host $_ }
    throw "Notification identity diagnostics export exited with code $($diagnosticsProcess.ExitCode)."
}

$bundleLine = $diagnosticsOutput | Where-Object { $_.StartsWith("Bundle: ") } | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($bundleLine)) {
    throw "Diagnostics bundle path was not reported."
}

$bundlePath = $bundleLine.Substring("Bundle: ".Length)
if (-not (Test-Path -LiteralPath $bundlePath)) {
    throw "Diagnostics bundle was not created at $bundlePath."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

$archive = [System.IO.Compression.ZipFile]::OpenRead($bundlePath)
try {
    $entry = $archive.GetEntry("diagnostics.json")
    if ($null -eq $entry) {
        throw "Diagnostics JSON entry was not found in the bundle."
    }

    $stream = $entry.Open()
    try {
        $reader = [System.IO.StreamReader]::new($stream)
        try {
            $diagnosticsJson = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }

    $diagnostics = $diagnosticsJson | ConvertFrom-Json
    if ($null -eq $diagnostics.notification) {
        throw "Notification diagnostics metadata was not found."
    }

    $notification = $diagnostics.notification
    if ($notification.platform -ne "Windows") {
        throw "Notification platform was '$($notification.platform)', expected 'Windows'."
    }

    if ($notification.appUserModelId -ne "Cotton.Sync.Desktop") {
        throw "Notification AppUserModelID was '$($notification.appUserModelId)', expected 'Cotton.Sync.Desktop'."
    }

    if ($notification.isDeliveryExecutableAvailable -ne $true) {
        throw "Notification delivery executable was not available."
    }

    if ($notification.isInstalledAppIdentityVerified -ne $true) {
        throw "Installed notification sender identity was not verified."
    }

    if ($notification.identityStatus -ne "installed-sender-identity") {
        throw "Notification identity status was '$($notification.identityStatus)', expected 'installed-sender-identity'."
    }

    $notificationDiagnosticsItem = @($diagnostics.selfTestItems) |
        Where-Object { $_.name -eq "Notification adapter" } |
        Select-Object -First 1
    if ($null -eq $notificationDiagnosticsItem) {
        throw "Notification adapter diagnostics item was not found."
    }

    if ($notificationDiagnosticsItem.passed -ne $true) {
        throw "Notification adapter diagnostics item did not pass."
    }

    if ($notificationDiagnosticsItem.skipped -eq $true) {
        throw "Notification adapter diagnostics item was skipped."
    }
}
finally {
    $archive.Dispose()
}

Write-Host "Verified installed notification sender identity: $bundlePath"
