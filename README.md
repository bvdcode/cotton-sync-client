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

The first public Windows release is a normal full-mirror sync client. Windows virtual files/placeholders are tracked separately and must not be treated as release-ready until the virtual-files release gate is fully closed. See `docs/windows-client-release-checklist.md`, `docs/windows-virtual-files-release-checklist.md`, and `docs/windows-virtual-files-qa-report.md`.
