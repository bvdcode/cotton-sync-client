param(
    [Parameter(Mandatory = $true)]
    [string]$AppExecutable,

    [string]$DataRoot = "",

    [string]$ReportPath = "",

    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = "Stop"

if ($TimeoutSeconds -le 0) {
    throw "TimeoutSeconds must be greater than zero."
}

$resolvedExecutable = (Resolve-Path -LiteralPath $AppExecutable).Path
$root = if ([string]::IsNullOrWhiteSpace($DataRoot)) {
    Join-Path ([System.IO.Path]::GetTempPath()) ("cotton-sync-update-visual-states-" + [Guid]::NewGuid().ToString("N"))
} else {
    $DataRoot
}

New-Item -ItemType Directory -Path $root -Force | Out-Null

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$visualStateReportLines = New-Object System.Collections.Generic.List[string]

function Get-WindowAutomationRoot {
    param(
        [System.Diagnostics.Process]$Process,
        [int]$TimeoutSeconds
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 250
        $Process.Refresh()
        if ($Process.HasExited) {
            throw "Desktop app exited before showing the update visual state window."
        }

        if ($Process.MainWindowHandle -ne [IntPtr]::Zero) {
            return [System.Windows.Automation.AutomationElement]::FromHandle($Process.MainWindowHandle)
        }
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Desktop app did not expose a main window handle within $TimeoutSeconds second(s)."
}

function Get-AutomationDescendants {
    param([System.Windows.Automation.AutomationElement]$Root)

    return @($Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition))
}

function Get-AutomationNames {
    param([System.Windows.Automation.AutomationElement[]]$Elements)

    return @($Elements | ForEach-Object { $_.Current.Name } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

function Assert-NamePresent {
    param(
        [string[]]$Names,
        [string]$ExpectedName,
        [string]$Scenario
    )

    if (-not ($Names -contains $ExpectedName)) {
        $Names | Sort-Object | ForEach-Object { Write-Host "Observed UI text: $_" }
        throw "Update visual state '$Scenario' did not show expected text '$ExpectedName'."
    }
}

function Assert-NameMatches {
    param(
        [string[]]$Names,
        [string]$Pattern,
        [string]$Scenario
    )

    if (-not ($Names | Where-Object { $_ -match $Pattern } | Select-Object -First 1)) {
        $Names | Sort-Object | ForEach-Object { Write-Host "Observed UI text: $_" }
        throw "Update visual state '$Scenario' did not show text matching '$Pattern'."
    }
}

function Assert-NameMissing {
    param(
        [string[]]$Names,
        [string]$UnexpectedName,
        [string]$Scenario
    )

    if ($Names -contains $UnexpectedName) {
        throw "Update visual state '$Scenario' still exposed stale action '$UnexpectedName'."
    }
}

function Assert-ProgressBarPresent {
    param(
        [System.Windows.Automation.AutomationElement[]]$Elements,
        [string]$Scenario,
        [string]$ExpectedName = ""
    )

    $progressBars = @($Elements | Where-Object {
        $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::ProgressBar
    })
    if ($progressBars.Count -eq 0) {
        throw "Update visual state '$Scenario' did not expose a progress bar."
    }

    if (-not [string]::IsNullOrWhiteSpace($ExpectedName)) {
        $matchingProgressBar = $progressBars | Where-Object {
            [string]::Equals($_.Current.Name, $ExpectedName, [System.StringComparison]::Ordinal)
        } | Select-Object -First 1
        if ($null -eq $matchingProgressBar) {
            $progressBars | ForEach-Object { Write-Host "Observed progress bar name: $($_.Current.Name)" }
            throw "Update visual state '$Scenario' did not expose expected progress bar '$ExpectedName'."
        }
    }
}

function Assert-VisualStateSnapshot {
    param(
        [System.Windows.Automation.AutomationElement]$Window,
        [string]$Scenario,
        [string]$ExpectedStatus,
        [string]$ExpectedDetailsPattern,
        [string[]]$ExpectedNames,
        [bool]$RequireSettingsActions,
        [string[]]$UnexpectedNames,
        [string]$ExpectedProgressBarName = ""
    )

    $elements = Get-AutomationDescendants -Root $Window
    $names = Get-AutomationNames -Elements $elements

    if ($RequireSettingsActions) {
        Assert-NamePresent -Names $names -ExpectedName "Settings" -Scenario $Scenario
        Assert-NamePresent -Names $names -ExpectedName "Updates" -Scenario $Scenario
    }
    Assert-NamePresent -Names $names -ExpectedName $ExpectedStatus -Scenario $Scenario
    foreach ($expectedName in $ExpectedNames) {
        Assert-NamePresent -Names $names -ExpectedName $expectedName -Scenario $Scenario
    }
    Assert-NameMatches -Names $names -Pattern $ExpectedDetailsPattern -Scenario $Scenario
    Assert-ProgressBarPresent -Elements $elements -Scenario $Scenario -ExpectedName $ExpectedProgressBarName
    foreach ($unexpectedName in $UnexpectedNames) {
        Assert-NameMissing -Names $names -UnexpectedName $unexpectedName -Scenario $Scenario
    }
}

function Test-VisualState {
    param(
        [string]$Scenario,
        [string]$ExpectedStatus,
        [string]$ExpectedDetailsPattern,
        [string[]]$ExpectedNames = @(),
        [bool]$RequireSettingsActions = $true,
        [string[]]$UnexpectedNames = @("Download", "Update"),
        [string]$ExpectedProgressBarName = "",
        [int]$StableObservationSeconds = 0
    )

    $dataDirectory = Join-Path $root $Scenario
    New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
    $process = Start-Process `
        -FilePath $resolvedExecutable `
        -ArgumentList @("--data-dir", $dataDirectory, "--visual-smoke", $Scenario) `
        -PassThru

    try {
        $window = Get-WindowAutomationRoot -Process $process -TimeoutSeconds $TimeoutSeconds
        Start-Sleep -Milliseconds 1000
        $sampleCount = 0
        $maxSnapshotMs = 0
        $maxSampleGapMs = 0
        $previousSampleElapsedMs = $null
        $scenarioStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        $deadline = [DateTime]::UtcNow.AddSeconds($StableObservationSeconds)
        while ($true) {
            $snapshotStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
            Assert-VisualStateSnapshot `
                -Window $window `
                -Scenario $Scenario `
                -ExpectedStatus $ExpectedStatus `
                -ExpectedDetailsPattern $ExpectedDetailsPattern `
                -ExpectedNames $ExpectedNames `
                -RequireSettingsActions $RequireSettingsActions `
                -UnexpectedNames $UnexpectedNames `
                -ExpectedProgressBarName $ExpectedProgressBarName
            $snapshotStopwatch.Stop()
            $maxSnapshotMs = [Math]::Max($maxSnapshotMs, [int]$snapshotStopwatch.ElapsedMilliseconds)
            $currentSampleElapsedMs = [int]$scenarioStopwatch.ElapsedMilliseconds
            if ($null -ne $previousSampleElapsedMs) {
                $maxSampleGapMs = [Math]::Max($maxSampleGapMs, $currentSampleElapsedMs - $previousSampleElapsedMs)
            }

            $previousSampleElapsedMs = $currentSampleElapsedMs
            $sampleCount++
            if ($StableObservationSeconds -le 0 -or [DateTime]::UtcNow -ge $deadline) {
                break
            }

            Start-Sleep -Milliseconds 500
        }

        Write-Host "Observed visual state '$Scenario' sample(s): $sampleCount"
        $visualStateReportLines.Add(
            "Scenario: $Scenario;Status=$ExpectedStatus;StableObservationSeconds=$StableObservationSeconds;Samples=$sampleCount;MaxSnapshotMs=$maxSnapshotMs;MaxSampleGapMs=$maxSampleGapMs")
    } finally {
        if (-not $process.HasExited) {
            $process.CloseMainWindow() | Out-Null
            if (-not $process.WaitForExit(3000)) {
                $process.Kill()
                $process.WaitForExit()
            }
        }
    }
}

Test-VisualState `
    -Scenario "update-download-progress" `
    -ExpectedStatus "Downloading update" `
    -ExpectedDetailsPattern "^Downloading .+ / .+ \(25%\)\.$" `
    -StableObservationSeconds 5

Test-VisualState `
    -Scenario "update-install-progress" `
    -ExpectedStatus "Installing update" `
    -ExpectedDetailsPattern "^Starting the update installer\.$" `
    -StableObservationSeconds 5

Test-VisualState `
    -Scenario "virtual-files-seeding" `
    -ExpectedStatus "Syncing" `
    -ExpectedDetailsPattern "^Making cloud files available .+ 118054 cloud items ready .+ scanning cloud .+ saving state$" `
    -ExpectedNames @("Preparing cloud files") `
    -RequireSettingsActions $false `
    -UnexpectedNames @("Download", "Update", "Processing queued changes", "Preparing cloud files 118054 of 500000", "118054 of 500000") `
    -ExpectedProgressBarName "Open-ended cloud file progress" `
    -StableObservationSeconds 30

if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
    $reportDirectory = Split-Path -Parent $ReportPath
    if (-not [string]::IsNullOrWhiteSpace($reportDirectory)) {
        New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
    }

    $report = New-Object System.Collections.Generic.List[string]
    $report.Add("Result: passed")
    foreach ($line in $visualStateReportLines) {
        $report.Add($line)
    }

    $report | Set-Content -LiteralPath $ReportPath -Encoding utf8
}

Write-Host "Verified installed update and VFS visual states."
