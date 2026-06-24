param(
    [string]$OutputDirectory = "",
    [string]$LocalRoot = (Join-Path $env:USERPROFILE "Desktop"),
    [string]$DataDirectory = (Join-Path $env:APPDATA "Cotton"),
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA "Programs\Cotton Sync"),
    [int]$MaxRootEntries = 500,
    [switch]$CaptureScreenshot,
    [switch]$RunSelfTest,
    [switch]$RunDiagnosticsExport
)

$ErrorActionPreference = "Stop"

if ($MaxRootEntries -le 0) {
    throw "MaxRootEntries must be greater than zero."
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputDirectory = Join-Path (Get-Location) "cotton-vfs-release-evidence-$timestamp"
}

$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

$summary = New-Object System.Collections.Generic.List[string]

function Add-Summary {
    param(
        [string]$Name,
        [string]$Value
    )

    $summary.Add(("{0}: {1}" -f $Name, $Value))
}

function ConvertTo-SafeFileName {
    param([string]$Value)

    $safe = $Value
    foreach ($character in [System.IO.Path]::GetInvalidFileNameChars()) {
        $safe = $safe.Replace([string]$character, "_")
    }

    if ($safe.Length -gt 80) {
        $safe = $safe.Substring(0, 80)
    }

    return $safe
}

function Redact-Text {
    param([AllowNull()][string]$Value)

    if ($null -eq $Value) {
        return ""
    }

    $redacted = $Value -replace "(?i)Bearer\s+[A-Za-z0-9._~+/=-]+", "Bearer <redacted>"
    $redacted = $redacted -replace "(?i)(access[_-]?token|refresh[_-]?token|id[_-]?token|password|totp|authorization)\s*[:=]\s*['""]?[^,'""\s}]+", '$1=<redacted>'
    return $redacted
}

function Invoke-Capture {
    param(
        [string]$Name,
        [string]$FileName,
        [scriptblock]$Action
    )

    $path = Join-Path $outputRoot $FileName
    try {
        & $Action | Out-File -LiteralPath $path -Encoding utf8
        Add-Summary $Name "captured: $path"
    }
    catch {
        $message = $_.Exception.Message
        "ERROR: $message" | Out-File -LiteralPath $path -Encoding utf8
        Add-Summary $Name "failed: $message"
    }
}

function Get-CottonProcess {
    @(Get-CimInstance Win32_Process -Filter "Name = 'Cotton.Sync.Desktop.exe'" -ErrorAction SilentlyContinue)
}

function Capture-RootEntries {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        [pscustomobject]@{
            RelativePath = "."
            FullPath = $Path
            Exists = $false
            Attributes = ""
            Length = $null
            LastWriteTimeUtc = $null
        }
        return
    }

    $root = (Resolve-Path -LiteralPath $Path).Path.TrimEnd('\')
    [pscustomobject]@{
        RelativePath = "."
        FullPath = $root
        Exists = $true
        Attributes = [string](Get-Item -LiteralPath $root -Force).Attributes
        Length = $null
        LastWriteTimeUtc = (Get-Item -LiteralPath $root -Force).LastWriteTimeUtc.ToString("O")
    }

    Get-ChildItem -LiteralPath $root -Force -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First $MaxRootEntries |
        ForEach-Object {
            $relative = $_.FullName.Substring($root.Length).TrimStart('\')
            [pscustomobject]@{
                RelativePath = $relative
                FullPath = $_.FullName
                Exists = $true
                Attributes = [string]$_.Attributes
                Length = if ($_.PSIsContainer) { $null } else { $_.Length }
                LastWriteTimeUtc = $_.LastWriteTimeUtc.ToString("O")
            }
        }
}

function Capture-LogTails {
    if (-not (Test-Path -LiteralPath $DataDirectory)) {
        Add-Summary "Log tails" "data directory not found: $DataDirectory"
        return
    }

    $logDirectory = Join-Path $outputRoot "log-tails"
    New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
    $logs = @(Get-ChildItem -LiteralPath $DataDirectory -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -eq ".log" } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 12)

    foreach ($log in $logs) {
        $safeName = ConvertTo-SafeFileName $log.FullName
        $target = Join-Path $logDirectory ($safeName + ".tail.log")
        Get-Content -LiteralPath $log.FullName -Tail 1000 -ErrorAction SilentlyContinue |
            ForEach-Object { Redact-Text $_ } |
            Out-File -LiteralPath $target -Encoding utf8
    }

    Add-Summary "Log tails" ("captured {0} log file(s)" -f $logs.Count)
}

function Capture-Screenshot {
    $path = Join-Path $outputRoot "desktop-screenshot.png"
    try {
        Add-Type -AssemblyName System.Windows.Forms
        Add-Type -AssemblyName System.Drawing
        $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
            $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            $graphics.Dispose()
            $bitmap.Dispose()
        }

        Add-Summary "Screenshot" "captured: $path"
    }
    catch {
        Add-Summary "Screenshot" ("failed: " + $_.Exception.Message)
    }
}

function Initialize-WindowProbe {
    if ("CottonReleaseEvidenceWindowProbe" -as [type]) {
        return
    }

    Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class CottonReleaseEvidenceWindowProbe
{
    public sealed class WindowSnapshot
    {
        public IntPtr Handle { get; set; }
        public string Title { get; set; }
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    public static WindowSnapshot[] GetVisibleWindowsForProcess(int targetProcessId)
    {
        var windows = new List<WindowSnapshot>();
        EnumWindows((hWnd, lParam) =>
        {
            if (!IsWindowVisible(hWnd))
            {
                return true;
            }

            uint processId;
            GetWindowThreadProcessId(hWnd, out processId);
            if (processId != targetProcessId)
            {
                return true;
            }

            var title = new StringBuilder(512);
            GetWindowText(hWnd, title, title.Capacity);
            windows.Add(new WindowSnapshot { Handle = hWnd, Title = title.ToString() });
            return true;
        }, IntPtr.Zero);
        return windows.ToArray();
    }

    public static int GetForegroundProcessId()
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return 0;
        }

        uint processId;
        GetWindowThreadProcessId(foreground, out processId);
        return (int)processId;
    }
}
"@
}

function Capture-ProcessWindows {
    Initialize-WindowProbe
    $foregroundProcessId = [CottonReleaseEvidenceWindowProbe]::GetForegroundProcessId()
    foreach ($process in Get-CottonProcess) {
        $windows = @([CottonReleaseEvidenceWindowProbe]::GetVisibleWindowsForProcess([int]$process.ProcessId))
        [pscustomobject]@{
            ProcessId = $process.ProcessId
            IsForeground = ([int]$process.ProcessId -eq $foregroundProcessId)
            VisibleWindowCount = $windows.Count
            VisibleWindowTitles = (($windows | ForEach-Object { $_.Title }) -join " | ")
        }
    }
}

function Capture-CloudFilesExplorerRegistrations {
    $patterns = @(
        "Cotton.Sync.Desktop",
        "Cotton Cloud",
        "Cotton Sync"
    )
    $roots = @(
        "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager",
        "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace",
        "HKCU\Software\Classes\CLSID",
        "HKCU\Software\Classes\WOW6432Node\CLSID"
    )
    $matches = New-Object System.Collections.Generic.List[string]
    foreach ($root in $roots) {
        foreach ($pattern in $patterns) {
            foreach ($mode in @("/d", "/k")) {
                $output = & reg.exe query $root /s /f $pattern $mode 2>$null
                if ($LASTEXITCODE -ne 0) {
                    continue
                }

                foreach ($line in $output) {
                    if (-not [string]::IsNullOrWhiteSpace($line)) {
                        $matches.Add($root + " :: " + $pattern + " :: " + $line)
                    }
                }
            }
        }
    }

    "MatchCount: $($matches.Count)"
    foreach ($match in $matches) {
        Redact-Text $match
    }
}

function Run-InstalledAppCommand {
    param(
        [string]$Name,
        [string[]]$Arguments,
        [string]$StdoutName,
        [string]$StderrName
    )

    $appExecutable = Join-Path $InstallDirectory "Cotton.Sync.Desktop.exe"
    if (-not (Test-Path -LiteralPath $appExecutable)) {
        Add-Summary $Name "installed executable not found: $appExecutable"
        return
    }

    $stdoutPath = Join-Path $outputRoot $StdoutName
    $stderrPath = Join-Path $outputRoot $StderrName
    $process = Start-Process `
        -FilePath $appExecutable `
        -ArgumentList $Arguments `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -Wait `
        -PassThru
    Add-Summary $Name ("exitCode={0}; stdout={1}; stderr={2}" -f $process.ExitCode, $stdoutPath, $stderrPath)
}

Add-Summary "CapturedAt" (Get-Date -Format "O")
Add-Summary "OutputDirectory" $outputRoot
Add-Summary "User" ([System.Security.Principal.WindowsIdentity]::GetCurrent().Name)
Add-Summary "Computer" $env:COMPUTERNAME
Add-Summary "LocalRoot" $LocalRoot
Add-Summary "DataDirectory" $DataDirectory
Add-Summary "InstallDirectory" $InstallDirectory

Invoke-Capture "OS" "os.txt" {
    Get-CimInstance Win32_OperatingSystem |
        Select-Object Caption, Version, BuildNumber, OSArchitecture, InstallDate, LastBootUpTime |
        Format-List
    "PowerShell: $($PSVersionTable.PSVersion)"
}

Invoke-Capture "Installed app" "installed-app.txt" {
    $appExecutable = Join-Path $InstallDirectory "Cotton.Sync.Desktop.exe"
    if (Test-Path -LiteralPath $appExecutable) {
        $item = Get-Item -LiteralPath $appExecutable
        $version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($appExecutable)
        [pscustomobject]@{
            Path = $appExecutable
            Length = $item.Length
            LastWriteTimeUtc = $item.LastWriteTimeUtc.ToString("O")
            ProductVersion = $version.ProductVersion
            FileVersion = $version.FileVersion
            Sha256 = (Get-FileHash -LiteralPath $appExecutable -Algorithm SHA256).Hash.ToLowerInvariant()
        } | Format-List
    }
    else {
        "Missing: $appExecutable"
    }
}

Invoke-Capture "Autostart registry" "registry-run.txt" {
    $runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
    $runValue = (Get-ItemProperty -Path $runKey -Name "Cotton Sync" -ErrorAction SilentlyContinue)."Cotton Sync"
    [pscustomobject]@{
        Key = $runKey
        Name = "Cotton Sync"
        Value = $runValue
    } | Format-List
}

Invoke-Capture "Cloud Files registry" "registry-syncrootmanager.txt" {
    $output = & reg.exe query "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager" /s 2>&1
    $exitCode = $LASTEXITCODE
    $output
    "reg.exe exitCode=$exitCode"
}

Invoke-Capture "Cotton processes" "processes.txt" {
    Get-CottonProcess |
        Select-Object ProcessId, ExecutablePath, CommandLine, CreationDate |
        Format-List
}

Invoke-Capture "Cotton process windows" "process-windows.txt" {
    Capture-ProcessWindows | Format-List
}

Invoke-Capture "Cloud Files Explorer registrations" "registry-cloud-files-explorer.txt" {
    Capture-CloudFilesExplorerRegistrations
}

Invoke-Capture "Local root entries" "local-root-entries.csv" {
    Capture-RootEntries -Path $LocalRoot | ConvertTo-Csv -NoTypeInformation
}

Capture-LogTails

if ($CaptureScreenshot) {
    Capture-Screenshot
}

if ($RunSelfTest) {
    $selfTestDataDirectory = Join-Path $outputRoot "self-test-data"
    New-Item -ItemType Directory -Path $selfTestDataDirectory -Force | Out-Null
    Run-InstalledAppCommand `
        -Name "Installed self-test" `
        -Arguments @("--self-test", "--data-dir", $selfTestDataDirectory) `
        -StdoutName "self-test.stdout.log" `
        -StderrName "self-test.stderr.log"
}

if ($RunDiagnosticsExport) {
    Run-InstalledAppCommand `
        -Name "Diagnostics export" `
        -Arguments @("--export-diagnostics", "--data-dir", $DataDirectory) `
        -StdoutName "diagnostics-export.stdout.log" `
        -StderrName "diagnostics-export.stderr.log"
}

$summaryPath = Join-Path $outputRoot "summary.txt"
$summary | Out-File -LiteralPath $summaryPath -Encoding utf8
Write-Host "Cotton VFS release evidence captured: $outputRoot"
