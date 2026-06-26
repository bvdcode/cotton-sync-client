; SPDX-License-Identifier: MIT
; Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

#ifndef SourceDir
#define SourceDir "..\..\bin\Release\net10.0\publish\win-x64"
#endif

#ifndef OutputDir
#define OutputDir "."
#endif

#ifndef AppVersion
#define AppVersion "0.0.1-dev"
#endif

#ifndef OutputBaseFilename
#define OutputBaseFilename "cotton-sync-desktop-win-x64-setup"
#endif

#ifndef IconFile
#define IconFile "..\..\Assets\app.ico"
#endif

#ifndef AppMutexName
#define AppMutexName "CottonSyncDesktop_B671C18E_1E77_437C_AB9B_5C5C9D877E18"
#endif

#ifndef AppUserModelId
#define AppUserModelId "Cotton.Sync.Desktop"
#endif

[Setup]
AppId={{B671C18E-1E77-437C-AB9B-5C5C9D877E18}
AppName=Cotton Sync
AppVersion={#AppVersion}
AppVerName=Cotton Sync
AppPublisher=Belov
AppPublisherURL=https://cottoncloud.dev
AppSupportURL=https://cottoncloud.dev
DefaultDirName={localappdata}\Programs\Cotton Sync
DefaultGroupName=Cotton Sync
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}
SetupIconFile={#IconFile}
UninstallDisplayName=Cotton Sync
UninstallDisplayIcon={app}\Cotton.Sync.Desktop.exe
CloseApplications=force
RestartApplications=no
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Cotton Sync"; Filename: "{app}\Cotton.Sync.Desktop.exe"; IconFilename: "{app}\Cotton.Sync.Desktop.exe"; AppUserModelID: "{#AppUserModelId}"
Name: "{group}\Uninstall Cotton Sync"; Filename: "{uninstallexe}"
Name: "{userdesktop}\Cotton Sync"; Filename: "{app}\Cotton.Sync.Desktop.exe"; IconFilename: "{app}\Cotton.Sync.Desktop.exe"; AppUserModelID: "{#AppUserModelId}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Cotton Sync"; ValueData: "{code:GetAutostartLaunchCommand}"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\*\shell\CottonSyncCopyShareLink"; ValueType: string; ValueName: ""; ValueData: "Copy Cotton Cloud share link"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\*\shell\CottonSyncCopyShareLink"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\Cotton.Sync.Desktop.exe"
Root: HKCU; Subkey: "Software\Classes\*\shell\CottonSyncCopyShareLink\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Cotton.Sync.Desktop.exe"" --copy-shell-share-link ""%1"""
Root: HKCU; Subkey: "Software\Classes\Directory\shell\CottonSyncCopyShareLink"; ValueType: string; ValueName: ""; ValueData: "Copy Cotton Cloud share link"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\CottonSyncCopyShareLink"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\Cotton.Sync.Desktop.exe"
Root: HKCU; Subkey: "Software\Classes\Directory\shell\CottonSyncCopyShareLink\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Cotton.Sync.Desktop.exe"" --copy-shell-share-link ""%1"""

[Run]
Filename: "{app}\Cotton.Sync.Desktop.exe"; Description: "Launch Cotton Sync"; Flags: nowait postinstall; Check: ShouldOfferLaunchAfterInstall
Filename: "{app}\Cotton.Sync.Desktop.exe"; Parameters: "{code:GetHiddenUpdateLaunchParameters}"; Flags: nowait; Check: ShouldLaunchHiddenAfterUpdate

[Code]
function PowerShellSingleQuotedLiteral(Value: String): String;
begin
  StringChangeEx(Value, '''', '''''', True);
  Result := '''' + Value + '''';
end;

function CommandLineQuoted(Value: String): String;
begin
  Result := '"' + Value + '"';
end;

procedure StopInstalledAppForSilentUninstall();
var
  ResultCode: Integer;
  PowerShellPath: String;
  AppExecutablePath: String;
  Command: String;
  Attempt: Integer;
begin
  if not UninstallSilent then
  begin
    exit;
  end;

  AppExecutablePath := ExpandConstant('{app}\Cotton.Sync.Desktop.exe');
  PowerShellPath := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  Command := '$target = ' + PowerShellSingleQuotedLiteral(AppExecutablePath) + '; ' +
    'Get-CimInstance Win32_Process | ' +
    'Where-Object { $_.Name -eq ''Cotton.Sync.Desktop.exe'' -and $_.ExecutablePath -eq $target } | ' +
    'ForEach-Object { Stop-Process -Id $_.ProcessId -Force; Wait-Process -Id $_.ProcessId -Timeout 5 -ErrorAction SilentlyContinue }';

  if Exec(PowerShellPath, '-NoProfile -ExecutionPolicy Bypass -Command ' + AddQuotes(Command), '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Log(Format('Silent uninstall pre-close command exited with code %d.', [ResultCode]));
  end
  else
  begin
    Log('Silent uninstall pre-close command could not be started.');
  end;

  for Attempt := 1 to 20 do
  begin
    if not CheckForMutexes('{#AppMutexName}') then
    begin
      Log(Format('Silent uninstall app mutex released after %d wait attempt(s).', [Attempt]));
      exit;
    end;

    Sleep(250);
  end;

  Log('Silent uninstall app mutex was still present after waiting for process shutdown.');
end;

procedure RunCloudFilesCleanupForUninstall();
var
  ResultCode: Integer;
  PowerShellPath: String;
  AppExecutablePath: String;
  Command: String;
begin
  AppExecutablePath := ExpandConstant('{app}\Cotton.Sync.Desktop.exe');
  PowerShellPath := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  Command := '$target = ' + PowerShellSingleQuotedLiteral(AppExecutablePath) + '; ' +
    'if (-not (Test-Path -LiteralPath $target)) { Write-Output ''Cloud Files cleanup skipped: executable missing.''; exit 0 }; ' +
    '$process = Start-Process -FilePath $target -ArgumentList @(''--cleanup-cloud-files'') -WindowStyle Hidden -PassThru; ' +
    '$deadline = (Get-Date).AddSeconds(60); ' +
    'while ((-not $process.HasExited) -and ((Get-Date) -lt $deadline)) { Start-Sleep -Milliseconds 250; $process.Refresh() }; ' +
    'if (-not $process.HasExited) { ' +
    'Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue; ' +
    'Write-Output ''Cloud Files cleanup timed out.''; exit 124 }; ' +
    '$process.Refresh(); exit $process.ExitCode';

  if Exec(PowerShellPath, '-NoProfile -ExecutionPolicy Bypass -Command ' + AddQuotes(Command), '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Log(Format('Cloud Files cleanup command exited with code %d.', [ResultCode]));
    if ResultCode <> 0 then
    begin
      Log('Cloud Files cleanup did not complete successfully; uninstall will continue so the app is not left installed.');
    end;
  end
  else
  begin
    Log('Cloud Files cleanup command could not be started; uninstall will continue.');
  end;
end;

function InitializeUninstall(): Boolean;
begin
  StopInstalledAppForSilentUninstall();
  Result := True;
end;

function ShouldOfferLaunchAfterInstall(): Boolean;
begin
  Result := (not WizardSilent) and (ExpandConstant('{param:LaunchAfterUpdate|0}') <> '1');
end;

function ShouldLaunchHiddenAfterUpdate(): Boolean;
begin
  Result := ExpandConstant('{param:LaunchAfterUpdate|0}') = '1';
end;

function GetHiddenUpdateLaunchParameters(Value: String): String;
var
  DataDirectory: String;
begin
  Result := '--start-minimized';
  DataDirectory := ExpandConstant('{param:LaunchAfterUpdateDataDir|}');
  if DataDirectory <> '' then
  begin
    Result := Result + ' --data-dir ' + CommandLineQuoted(DataDirectory);
  end;
end;

function GetAutostartLaunchCommand(Value: String): String;
var
  DataDirectory: String;
begin
  Result := CommandLineQuoted(ExpandConstant('{app}\Cotton.Sync.Desktop.exe')) + ' --start-minimized';
  DataDirectory := ExpandConstant('{param:AutostartDataDir|}');
  if DataDirectory <> '' then
  begin
    Result := Result + ' --data-dir ' + CommandLineQuoted(DataDirectory);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    RunCloudFilesCleanupForUninstall();
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Cotton Sync');
  end;
end;
