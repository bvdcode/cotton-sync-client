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
