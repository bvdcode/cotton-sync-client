# Cotton Sync Client

Cross-platform Cotton Sync client suite.

This repository contains the sync engine, application services, Windows/Linux desktop client, Windows CLI, tests, and release packaging workflow. It consumes the public `Cotton` and `Cotton.Sdk` NuGet packages instead of depending on the Cotton server monorepo.

## Build

```powershell
dotnet restore src/Cotton.sln
dotnet build src/Cotton.sln --configuration Release --no-restore
dotnet test src/Cotton.sln --configuration Release --no-build
```

## Publish Locally

```powershell
dotnet publish src/Cotton.Sync.Desktop/Cotton.Sync.Desktop.csproj /p:PublishProfile=win-x64
dotnet publish src/Cotton.Sync.Cli/Cotton.Sync.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishReadyToRun=false
```

## Windows Virtual Files

The first public Windows release is a normal full-mirror sync client. Windows virtual files/placeholders are tracked separately in the QA report and must not be treated as release-ready without the relevant installed-client evidence. See `docs/qa.md`.

## Diagnostic Logs

The desktop client writes `cotton-sync.log` and a separate `cloud-files.log` in its data directory (`%APPDATA%\Cotton\Sync` on Windows). Cloud Files events are written as they occur, including the operation, status, file path, Windows error code, and details. Exporting diagnostics is not required to save these events.

Each journal rotates at 5 MiB and retains three previous files (`.1` through `.3`). Diagnostic archives include both journals and their retained files. Older events are removed as the logs rotate.
