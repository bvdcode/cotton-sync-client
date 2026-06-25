param(
    [string]$ExpectedExecutablePath = "",

    [string]$InvocationDataDirectory = "",

    [switch]$InvokeInstalledVerb,

    [switch]$ExpectAbsent
)

$ErrorActionPreference = "Stop"

$verbSubKeys = @(
    "Software\Classes\*\shell\CottonSyncCopyShareLink",
    "Software\Classes\Directory\shell\CottonSyncCopyShareLink"
)

$verbCommandSubKeys = @(
    "Software\Classes\*\shell\CottonSyncCopyShareLink\command",
    "Software\Classes\Directory\shell\CottonSyncCopyShareLink\command"
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
    $parentPath = [System.IO.Path]::GetDirectoryName($resolvedPath)
    $leafName = [System.IO.Path]::GetFileName($resolvedPath)
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

function Invoke-ProcessCapture {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$StandardOutputPath,
        [Parameter(Mandatory = $true)][string]$StandardErrorPath
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    [void]$process.Start()
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(120000)) {
        try {
            $process.Kill($true)
        }
        catch {
            $process.Kill()
        }

        $process.WaitForExit()
        [System.IO.File]::WriteAllText($StandardOutputPath, $stdoutTask.GetAwaiter().GetResult())
        [System.IO.File]::WriteAllText($StandardErrorPath, $stderrTask.GetAwaiter().GetResult())
        return 124
    }

    [System.IO.File]::WriteAllText($StandardOutputPath, $stdoutTask.GetAwaiter().GetResult())
    [System.IO.File]::WriteAllText($StandardErrorPath, $stderrTask.GetAwaiter().GetResult())
    return $process.ExitCode
}

function Protect-TokenPayload {
    param([string]$Json)

    Add-Type -AssemblyName System.Security
    $entropy = [System.Text.Encoding]::UTF8.GetBytes("Cotton.Sync.Desktop.TokenStore.v1")
    $payload = [System.Text.Encoding]::UTF8.GetBytes($Json)
    return [System.Security.Cryptography.ProtectedData]::Protect(
        $payload,
        $entropy,
        [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
}

function Write-SmokeTokenStore {
    param([string]$DataDirectory)

    $tokensJson = @{
        accessToken = "shell-share-link-smoke-access"
        refreshToken = "shell-share-link-smoke-refresh"
    } | ConvertTo-Json -Compress
    $protectedPayload = Protect-TokenPayload -Json $tokensJson
    $envelope = @{
        scheme = "windows-dpapi-current-user-v1"
        payload = [Convert]::ToBase64String($protectedPayload)
    } | ConvertTo-Json -Compress
    Set-Content -LiteralPath (Join-Path $DataDirectory "tokens.json") -Value $envelope -Encoding UTF8
}

function Get-FreeLoopbackPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Parse("127.0.0.1"), 0)
    $listener.Start()
    try {
        return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    } finally {
        $listener.Stop()
    }
}

function Start-ShareLinkSmokeServer {
    param(
        [int]$Port,
        [string]$ReadyPath,
        [string]$RequestPath,
        [string]$ShareToken
    )

    return Start-Job -ScriptBlock {
        param(
            [int]$Port,
            [string]$ReadyPath,
            [string]$RequestPath,
            [string]$ShareToken
        )

        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Parse("127.0.0.1"), $Port)
        $listener.Start()
        [System.IO.File]::WriteAllText($ReadyPath, "ready")
        try {
            $client = $listener.AcceptTcpClient()
            try {
                $stream = $client.GetStream()
                $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::ASCII, $false, 1024, $true)
                $builder = [System.Text.StringBuilder]::new()
                while ($true) {
                    $line = $reader.ReadLine()
                    if ($null -eq $line -or $line.Length -eq 0) {
                        break
                    }

                    [void]$builder.AppendLine($line)
                }

                [System.IO.File]::WriteAllText($RequestPath, $builder.ToString())
                $body = ConvertTo-Json ("http://127.0.0.1:$Port/download/$ShareToken") -Compress
                $bodyBytes = [System.Text.Encoding]::UTF8.GetBytes($body)
                $header = "HTTP/1.1 200 OK`r`nContent-Type: application/json; charset=utf-8`r`nContent-Length: $($bodyBytes.Length)`r`nConnection: close`r`n`r`n"
                $headerBytes = [System.Text.Encoding]::ASCII.GetBytes($header)
                $stream.Write($headerBytes, 0, $headerBytes.Length)
                $stream.Write($bodyBytes, 0, $bodyBytes.Length)
                $stream.Flush()
            } finally {
                $client.Dispose()
            }
        } finally {
            $listener.Stop()
        }
    } -ArgumentList $Port, $ReadyPath, $RequestPath, $ShareToken
}

function Get-DefaultRegistryValue {
    param([string]$SubKey)

    $key = Open-CurrentUserSubKey -SubKey $SubKey
    if ($null -eq $key) {
        throw "Shell share-link verb command key was not installed: HKCU\$SubKey"
    }

    try {
        return [string]$key.GetValue("")
    } finally {
        $key.Dispose()
    }
}

function Set-DefaultRegistryValue {
    param(
        [string]$SubKey,
        [string]$Value
    )

    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($SubKey, $true)
    if ($null -eq $key) {
        throw "Shell share-link verb command key was not installed: HKCU\$SubKey"
    }

    try {
        $key.SetValue("", $Value, [Microsoft.Win32.RegistryValueKind]::String)
    } finally {
        $key.Dispose()
    }
}

function Wait-File {
    param(
        [string]$Path,
        [int]$TimeoutSeconds = 30
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $Path) {
            return
        }

        Start-Sleep -Milliseconds 250
    }

    throw "Timed out waiting for '$Path'."
}

function Ensure-ShellShareLinkSmokeData {
    param(
        [string]$AppExecutable,
        [string]$DataDirectory
    )

    New-Item -ItemType Directory -Path $DataDirectory -Force | Out-Null
    $targetPath = Join-Path $DataDirectory "shell-share-link-root\synced-file.txt"
    if (-not (Test-Path -LiteralPath $targetPath)) {
        $prepareStdout = Join-Path $DataDirectory "shell-share-link-prepare.stdout.log"
        $prepareStderr = Join-Path $DataDirectory "shell-share-link-prepare.stderr.log"
        $exitCode = Invoke-ProcessCapture `
            -FilePath $AppExecutable `
            -Arguments @("--shell-share-link-smoke", "--data-dir", $DataDirectory) `
            -StandardOutputPath $prepareStdout `
            -StandardErrorPath $prepareStderr
        $prepareOutput = if (Test-Path -LiteralPath $prepareStdout) { Get-Content -LiteralPath $prepareStdout } else { @() }
        if ($exitCode -ne 0 -or -not ($prepareOutput | Where-Object { $_ -eq "Result: passed" } | Select-Object -First 1)) {
            throw "Shell share-link smoke data preparation failed."
        }
    }

    Write-SmokeTokenStore -DataDirectory $DataDirectory
    return $targetPath
}

function Assert-InstalledShellVerbInvocation {
    param(
        [string]$AppExecutable,
        [string]$DataDirectory
    )

    $targetPath = Ensure-ShellShareLinkSmokeData -AppExecutable $AppExecutable -DataDirectory $DataDirectory
    $port = Get-FreeLoopbackPort
    $serverUrl = "http://127.0.0.1:$port/"
    $shareToken = "shell-verb-smoke-token"
    $serverReadyPath = Join-Path $DataDirectory "shell-share-link-server-ready.txt"
    $requestPath = Join-Path $DataDirectory "shell-share-link-server-request.txt"
    $stdoutPath = Join-Path $DataDirectory "shell-share-link-verb.stdout.log"
    $stderrPath = Join-Path $DataDirectory "shell-share-link-verb.stderr.log"
    $commandStdoutPath = Join-Path $DataDirectory "shell-share-link-command.stdout.log"
    $commandStderrPath = Join-Path $DataDirectory "shell-share-link-command.stderr.log"
    $exitPath = Join-Path $DataDirectory "shell-share-link-verb.exit"
    $wrapperPath = Join-Path $DataDirectory "invoke-shell-share-link.ps1"

    Remove-Item -LiteralPath $serverReadyPath, $requestPath, $stdoutPath, $stderrPath, $commandStdoutPath, $commandStderrPath, $exitPath -Force -ErrorAction SilentlyContinue
    $wrapperLines = @(
        'param([string]$TargetPath)',
        '$ErrorActionPreference = "Stop"',
        '$process = Start-Process -FilePath ' + (ConvertTo-Json $AppExecutable -Compress) + ' -ArgumentList @("--server-url", ' + (ConvertTo-Json $serverUrl -Compress) + ', "--data-dir", ' + (ConvertTo-Json $DataDirectory -Compress) + ', "--copy-shell-share-link", $TargetPath) -Wait -PassThru -RedirectStandardOutput ' + (ConvertTo-Json $stdoutPath -Compress) + ' -RedirectStandardError ' + (ConvertTo-Json $stderrPath -Compress),
        '[System.IO.File]::WriteAllText(' + (ConvertTo-Json $exitPath -Compress) + ', [string]$process.ExitCode)'
    )
    Set-Content -LiteralPath $wrapperPath -Value $wrapperLines -Encoding UTF8

    $serverJob = Start-ShareLinkSmokeServer -Port $port -ReadyPath $serverReadyPath -RequestPath $requestPath -ShareToken $shareToken
    $originalCommands = @{}
    try {
        Wait-File -Path $serverReadyPath -TimeoutSeconds 10
        foreach ($commandSubKey in $verbCommandSubKeys) {
            $originalCommands[$commandSubKey] = Get-DefaultRegistryValue -SubKey $commandSubKey
            Set-DefaultRegistryValue `
                -SubKey $commandSubKey `
                -Value ("powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$wrapperPath`" `"%1`"")
        }

        $registeredCommand = Get-DefaultRegistryValue -SubKey $verbCommandSubKeys[0]
        if ($registeredCommand.IndexOf($wrapperPath, [StringComparison]::OrdinalIgnoreCase) -lt 0 -or
            $registeredCommand.IndexOf("%1", [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            throw "Installed shell share-link verb command did not reference the smoke wrapper and target placeholder."
        }

        $commandExitCode = Invoke-ProcessCapture `
            -FilePath "powershell.exe" `
            -Arguments @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $wrapperPath, $targetPath) `
            -StandardOutputPath $commandStdoutPath `
            -StandardErrorPath $commandStderrPath
        if ($commandExitCode -ne 0) {
            $commandError = if (Test-Path -LiteralPath $commandStderrPath) { Get-Content -LiteralPath $commandStderrPath -Raw } else { "" }
            throw "Installed shell share-link verb command wrapper exited with code $commandExitCode. $commandError"
        }

        Wait-File -Path $exitPath -TimeoutSeconds 5
        Wait-File -Path $requestPath
        $exitCode = [int](Get-Content -LiteralPath $exitPath -Raw)
        $stdout = if (Test-Path -LiteralPath $stdoutPath) { Get-Content -LiteralPath $stdoutPath } else { @() }
        $request = Get-Content -LiteralPath $requestPath -Raw
        if ($exitCode -ne 0) {
            $stderr = if (Test-Path -LiteralPath $stderrPath) { Get-Content -LiteralPath $stderrPath -Raw } else { "" }
            throw "Installed shell share-link verb exited with code $exitCode. $stderr"
        }

        if (-not ($stdout | Where-Object { $_ -eq "ShareLinkCopied: true" } | Select-Object -First 1)) {
            throw "Installed shell share-link verb did not report clipboard success."
        }

        if (-not ($stdout | Where-Object { $_ -eq "Result: passed" } | Select-Object -First 1)) {
            throw "Installed shell share-link verb did not report a passed result."
        }

        if (-not $request.Contains("Authorization: Bearer shell-share-link-smoke-access")) {
            throw "Installed shell share-link verb did not send the isolated smoke access token."
        }

        if (-not $request.Contains("download-link")) {
            throw "Installed shell share-link verb did not call the file share-link endpoint."
        }
    } finally {
        foreach ($entry in $originalCommands.GetEnumerator()) {
            Set-DefaultRegistryValue -SubKey $entry.Key -Value $entry.Value
        }

        if ($serverJob.State -eq "Running") {
            Stop-Job -Job $serverJob -ErrorAction SilentlyContinue
        }

        Receive-Job -Job $serverJob -ErrorAction SilentlyContinue | Out-Null
        Remove-Job -Job $serverJob -Force -ErrorAction SilentlyContinue
    }
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

if ($InvokeInstalledVerb) {
    if ([string]::IsNullOrWhiteSpace($InvocationDataDirectory)) {
        throw "InvocationDataDirectory is required when InvokeInstalledVerb is set."
    }

    Assert-InstalledShellVerbInvocation -AppExecutable $resolvedExecutable -DataDirectory $InvocationDataDirectory
}

if ($InvokeInstalledVerb) {
    Write-Host "Verified installed shell share-link verbs, Explorer visibility, and shell invocation."
} else {
    Write-Host "Verified installed shell share-link verbs and Explorer visibility."
}
