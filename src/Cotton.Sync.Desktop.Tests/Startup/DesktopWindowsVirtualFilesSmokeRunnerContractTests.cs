// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Tests.Startup
{
    public class DesktopWindowsVirtualFilesSmokeRunnerContractTests
    {
        [Test]
        public void RunAsync_PreparesIsolatedQaDriveBeforeCloudFilesSmoke()
        {
            string runner = File.ReadAllText(GetDesktopFilePath("Startup/DesktopWindowsVirtualFilesSmokeRunner.cs"));
            int preparationIndex = runner.IndexOf(
                "PrepareSmokeRootEnvironmentAsync(rootPath",
                StringComparison.Ordinal);
            int diagnosticsIndex = runner.IndexOf(
                "new WindowsCloudFilesDiagnostics()",
                StringComparison.Ordinal);

            Assert.Multiple(() =>
            {
                Assert.That(preparationIndex, Is.GreaterThanOrEqualTo(0));
                Assert.That(diagnosticsIndex, Is.GreaterThan(preparationIndex));
                Assert.That(runner, Does.Contain("subst.exe"));
                Assert.That(runner, Does.Contain("CottonSyncSmokeDrive"));
                Assert.That(runner, Does.Contain("Isolated QA drive prepared."));
                Assert.That(runner, Does.Contain("Windows virtual-files smoke could not create the isolated QA drive"));
                Assert.That(runner, Does.Contain("Result: failed"));
            });
        }

        [Test]
        public void Program_PreparesVfsSmokeEnvironmentBeforePathAndTraceBootstrap()
        {
            string program = File.ReadAllText(GetDesktopFilePath("Program.cs"));
            int startupSetupIndex = program.IndexOf(
                "PrepareStartupEnvironmentAsync(startupOptions",
                StringComparison.Ordinal);
            int pathResolverIndex = program.IndexOf(
                "DesktopStartupPathResolver.Resolve(startupOptions)",
                StringComparison.Ordinal);
            int traceLoggingIndex = program.IndexOf(
                "DesktopTraceLogging.Install(paths)",
                StringComparison.Ordinal);

            Assert.Multiple(() =>
            {
                Assert.That(startupSetupIndex, Is.GreaterThanOrEqualTo(0));
                Assert.That(pathResolverIndex, Is.GreaterThan(startupSetupIndex));
                Assert.That(traceLoggingIndex, Is.GreaterThan(startupSetupIndex));
            });
        }

        [Test]
        public void RunAsync_ExposesLargeRemovePairCleanupPhaseThroughProductDeletionPath()
        {
            string runner = File.ReadAllText(GetDesktopFilePath("Startup/DesktopWindowsVirtualFilesSmokeRunner.cs"));

            Assert.Multiple(() =>
            {
                Assert.That(runner, Does.Contain("\"large-remove-pair-cleanup\""));
                Assert.That(runner, Does.Contain("RunLargeRemovePairCleanupAsync("));
                Assert.That(runner, Does.Contain("new SqliteSyncPairSettingsStore(paths.AppDatabasePath)"));
                Assert.That(runner, Does.Contain("new SqliteSyncStateStore(paths.SyncStateDatabasePath)"));
                Assert.That(runner, Does.Contain("CreateDeletionSmokeApplication(syncPairs, stateStore, cloudFiles)"));
                Assert.That(runner, Does.Contain("DeleteSyncPairAsync(syncPair.Id"));
                Assert.That(runner, Does.Contain("StopSyncAsync(cancellationToken)"));
                Assert.That(runner, Does.Contain("Deleting the large virtual-files pair compacted the sync-state database."));
                Assert.That(runner, Does.Contain("Deleting the large virtual-files pair removed the local placeholder root."));
                Assert.That(runner, Does.Contain("Large virtual-files pair cleanup runtime health captured."));
                Assert.That(runner, Does.Contain("ForceFullCollection()"));
                Assert.That(runner, Does.Contain("workingSetBytes="));
                Assert.That(runner, Does.Contain("privateMemoryBytes="));
                Assert.That(runner, Does.Contain("threadCount="));
                Assert.That(runner, Does.Contain("handleCount="));
            });
        }

        [Test]
        public void RunAsync_ExposesSteadyStateRepeatPhaseWithLocalScanGuard()
        {
            string runner = File.ReadAllText(GetDesktopFilePath("Startup/DesktopWindowsVirtualFilesSmokeRunner.cs"));

            Assert.Multiple(() =>
            {
                Assert.That(runner, Does.Contain("\"steady-state-repeat\""));
                Assert.That(runner, Does.Contain("RunSteadyStateRepeatAsync("));
                Assert.That(runner, Does.Contain("new SqliteSyncStateStore(paths.SyncStateDatabasePath)"));
                Assert.That(runner, Does.Contain("new GuardLocalScanner()"));
                Assert.That(runner, Does.Contain("new LargeStateFirstRemoteCrawler("));
                Assert.That(runner, Does.Contain("new NoTransferRemoteFileSynchronizer()"));
                Assert.That(runner, Does.Contain("new GuardRemoteFilePlaceholderWriter()"));
                Assert.That(runner, Does.Contain("remoteFilePlaceholderWriter: placeholderWriter"));
                Assert.That(runner, Does.Contain("Steady-state repeat pass avoided local placeholder-tree scanning."));
                Assert.That(runner, Does.Contain("Steady-state repeat smoke must not scan the local placeholder tree."));
                Assert.That(runner, Does.Contain("Steady-state repeat smoke must not create or refresh placeholders."));
                Assert.That(runner, Does.Contain("fullLocalScans="));
                Assert.That(runner, Does.Contain("metadataTreeScans="));
                Assert.That(runner, Does.Contain("pathLookups="));
                Assert.That(runner, Does.Contain("streamingCrawls="));
                Assert.That(runner, Does.Contain("transfers="));
                Assert.That(runner, Does.Contain("placeholderWrites="));
            });
        }

        [Test]
        public void RunAsync_ExposesInitialStreamingLoggingPhaseWithTraceMetrics()
        {
            string runner = File.ReadAllText(GetDesktopFilePath("Startup/DesktopWindowsVirtualFilesSmokeRunner.cs"));

            Assert.Multiple(() =>
            {
                Assert.That(runner, Does.Contain("\"initial-streaming-logging\""));
                Assert.That(runner, Does.Contain("RunInitialStreamingLoggingAsync("));
                Assert.That(runner, Does.Contain("new InitialStreamingLoggingRemoteCrawler("));
                Assert.That(runner, Does.Contain("DesktopCloudFilesPlaceholderWriter placeholderWriter = new("));
                Assert.That(runner, Does.Contain("Cloud Files sync root connected for initial VFS logging smoke."));
                Assert.That(runner, Does.Contain("loggerFactory.CreateLogger<SyncEngine>()"));
                Assert.That(runner, Does.Contain("new SyncRunOptions { RunProgress = runProgress }"));
                Assert.That(runner, Does.Contain("Initial VFS streaming progress stayed on placeholder creation and completed cleanly."));
                Assert.That(runner, Does.Contain("localScanSamples="));
                Assert.That(runner, Does.Contain("remoteScanSamples="));
                Assert.That(runner, Does.Contain("Completed initial streaming Windows virtual-files population"));
                Assert.That(runner, Does.Contain("remote page latency total="));
                Assert.That(runner, Does.Contain("expectedFileCount + \" placeholders created or refreshed\""));
                Assert.That(runner, Does.Contain("Initial VFS runtime health captured."));
                Assert.That(runner, Does.Contain("\"before=\""));
                Assert.That(runner, Does.Contain("\", after=\""));
                Assert.That(runner, Does.Contain("FormatRuntimeHealth(beforeStreamingHealth)"));
                Assert.That(runner, Does.Contain("FormatRuntimeHealth(afterStreamingHealth)"));
                Assert.That(runner, Does.Contain("Initial VFS trace log contains large-run metrics."));
                Assert.That(runner, Does.Contain("Metric excerpt: "));
            });
        }

        [Test]
        public void RunAsync_BoundsExternalPlaceholderHydrationReads()
        {
            string runner = File.ReadAllText(GetDesktopFilePath("Startup/DesktopWindowsVirtualFilesSmokeRunner.cs"));

            Assert.Multiple(() =>
            {
                Assert.That(runner, Does.Contain("ExternalFileReadTimeout = TimeSpan.FromSeconds(30)"));
                Assert.That(runner, Does.Contain("CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token)"));
                Assert.That(runner, Does.Contain("process.WaitForExitAsync(linkedCancellation.Token)"));
                Assert.That(runner, Does.Contain("External file-read helper timed out after "));
                Assert.That(runner, Does.Contain("process.Kill(entireProcessTree: true)"));
            });
        }

        [Test]
        public void RunAsync_LargeTreePhaseVerifiesCloudFilesDirectoryStatusFinalization()
        {
            string runner = File.ReadAllText(GetDesktopFilePath("Startup/DesktopWindowsVirtualFilesSmokeRunner.cs"));

            Assert.Multiple(() =>
            {
                Assert.That(runner, Does.Contain("CreateDirectoryPlaceholder(CreateDirectoryRequest(syncPair, LargeTreeDirectoryName))"));
                Assert.That(runner, Does.Contain("SetSyncRootInSyncState(syncPair)"));
                Assert.That(runner, Does.Contain("VerifyCloudFilesInSyncStateAsync("));
                Assert.That(runner, Does.Contain("Large-tree Cloud Files sync root status was marked in sync."));
                Assert.That(runner, Does.Contain("Large-tree Cloud Files directory status was finalized."));
                Assert.That(runner, Does.Contain("VerifyExplorerShellSettledStatusAsync("));
                Assert.That(runner, Does.Contain("Explorer shell status settled for "));
                Assert.That(runner, Does.Contain("IsActiveExplorerShellStatus("));
                Assert.That(runner, Does.Contain("WindowsCloudFilesPlaceholderState.InSync"));
                Assert.That(runner, Does.Contain("WindowsCloudFilesPlaceholderState.Partial"));
            });
        }

        [Test]
        public void RunAsync_ReplaceCloudOnlyUploadPhaseVerifiesNativeCloudFilesStatus()
        {
            string runner = File.ReadAllText(GetDesktopFilePath("Startup/DesktopWindowsVirtualFilesSmokeRunner.cs"));

            Assert.Multiple(() =>
            {
                Assert.That(runner, Does.Contain("\"replace-cloud-only-upload\""));
                Assert.That(runner, Does.Contain("RunReplaceCloudOnlyUploadAsync("));
                Assert.That(runner, Does.Contain("Cloud Files sync root connected for cloud-only replacement upload smoke."));
                Assert.That(runner, Does.Contain("Cloud-only placeholder was replaced by a regular local file before sync."));
                Assert.That(runner, Does.Contain("new WindowsVirtualFilesUploadFinalizationPairWork("));
                Assert.That(runner, Does.Contain("new WindowsVirtualFilesDirectoryPlaceholderRepairPairWork("));
                Assert.That(runner, Does.Contain("Cloud-only replacement uploaded and persisted remote identity."));
                Assert.That(runner, Does.Contain("Uploaded replacement file Cloud Files status was finalized."));
                Assert.That(runner, Does.Contain("Uploaded replacement parent directory Cloud Files status was finalized."));
                Assert.That(runner, Does.Contain("Uploaded replacement sync root Cloud Files status was finalized."));
                Assert.That(runner, Does.Contain("VerifyExplorerShellSettledStatusAsync("));
                Assert.That(runner, Does.Contain("\"uploaded replacement file\""));
                Assert.That(runner, Does.Contain("\"uploaded replacement parent directory\""));
            });
        }

        [Test]
        public void RunAsync_ShellShareLinkTargetsPhaseVerifiesRealVfsTargets()
        {
            string runner = File.ReadAllText(GetDesktopFilePath("Startup/DesktopWindowsVirtualFilesSmokeRunner.cs"));

            Assert.Multiple(() =>
            {
                Assert.That(runner, Does.Contain("\"shell-share-link-targets\""));
                Assert.That(runner, Does.Contain("RunShellShareLinkTargetsAsync("));
                Assert.That(runner, Does.Contain("Shell share-link VFS target smoke requires the native Windows Cloud Files API."));
                Assert.That(runner, Does.Contain("Cloud Files sync root connected for VFS shell share-link target smoke."));
                Assert.That(runner, Does.Contain("VFS shell share-link smoke seeded synced, placeholder, folder, and local-only targets."));
                Assert.That(runner, Does.Contain("VFS synced file share link copied"));
                Assert.That(runner, Does.Contain("VFS remote-only placeholder share link copied"));
                Assert.That(runner, Does.Contain("VFS hydrated placeholder share link copied"));
                Assert.That(runner, Does.Contain("VFS folder share link copied"));
                Assert.That(runner, Does.Contain("VFS local-only item is rejected without clipboard write"));
                Assert.That(runner, Does.Contain("RunShellShareLinkCopyAsync("));
                Assert.That(runner, Does.Contain("VfsShellShareLinkSmokeClient"));
                Assert.That(runner, Does.Contain("VfsShellShareLinkSmokeClipboardService"));
                Assert.That(runner, Does.Contain("VFS shell share-link remote-only placeholder Cloud Files status was finalized."));
                Assert.That(runner, Does.Contain("VFS shell share-link hydrated placeholder Cloud Files status was finalized."));
                Assert.That(runner, Does.Contain("VFS shell share-link folder Cloud Files status was finalized."));
            });
        }

        [Test]
        public void RunAsync_DesktopRootLifecyclePhaseUsesAppServiceAndNativeVfsCleanup()
        {
            string runner = File.ReadAllText(GetDesktopFilePath("Startup/DesktopWindowsVirtualFilesSmokeRunner.cs"));

            Assert.Multiple(() =>
            {
                Assert.That(runner, Does.Contain("\"desktop-root-lifecycle\""));
                Assert.That(runner, Does.Contain("RunDesktopRootLifecycleAsync("));
                Assert.That(runner, Does.Contain("Desktop root lifecycle smoke requires the native Windows Cloud Files API."));
                Assert.That(runner, Does.Contain("CreateDesktopRootSyncPair("));
                Assert.That(runner, Does.Contain("DisplayName = \"Desktop\""));
                Assert.That(runner, Does.Contain("RemoteDisplayPath = \"/Desktop\""));
                Assert.That(runner, Does.Contain("SaveSyncPairAsync(syncPair"));
                Assert.That(runner, Does.Contain("StartSyncAsync(cancellationToken)"));
                Assert.That(runner, Does.Contain("SyncNowAsync(syncPair.Id"));
                Assert.That(runner, Does.Contain("DeleteSyncPairAsync(syncPair.Id"));
                Assert.That(runner, Does.Contain("WindowsCloudFilesSyncRootConnectionCoordinator"));
                Assert.That(runner, Does.Contain("CreateDesktopRootLifecycleApplication("));
                Assert.That(runner, Does.Contain("Desktop root sync pair was saved through the app service."));
                Assert.That(runner, Does.Contain("Desktop root remote file became an online-only placeholder."));
                Assert.That(runner, Does.Contain("Desktop root Cloud Files sync root status was finalized."));
                Assert.That(runner, Does.Contain("Desktop root sync root reconnected from persisted settings after app restart."));
                Assert.That(runner, Does.Contain("Restarted Desktop root callbacks hydrated the persisted placeholder."));
                Assert.That(runner, Does.Contain("Restarted Desktop root placeholder was dehydrated before pair deletion."));
                Assert.That(runner, Does.Contain("Deleting the Desktop root sync pair removed the local placeholder root."));
            });
        }

        [Test]
        public void RunAsync_DesktopSessionRestorePhaseVerifiesSavedSessionAndVfsReconnect()
        {
            string runner = File.ReadAllText(GetDesktopFilePath("Startup/DesktopWindowsVirtualFilesSmokeRunner.cs"));

            Assert.Multiple(() =>
            {
                Assert.That(runner, Does.Contain("\"desktop-session-restore\""));
                Assert.That(runner, Does.Contain("RunDesktopSessionRestoreAsync("));
                Assert.That(runner, Does.Contain("Desktop session restore smoke requires the native Windows Cloud Files API."));
                Assert.That(runner, Does.Contain("Persisted startup state prepared for Desktop session restore smoke."));
                Assert.That(runner, Does.Contain("Desktop startup restored the saved signed-in session."));
                Assert.That(runner, Does.Contain("Desktop startup used the remembered server for session restore."));
                Assert.That(runner, Does.Contain("Desktop startup loaded the persisted virtual-files sync pair."));
                Assert.That(runner, Does.Contain("Desktop startup reconnected the persisted Cloud Files sync root."));
                Assert.That(runner, Does.Contain("Deleting the restored Desktop session pair removed the local placeholder root."));
                Assert.That(runner, Does.Contain("SessionRestoreApplicationFactory"));
                Assert.That(runner, Does.Contain("SessionRestoreMemoryTokenStore"));
                Assert.That(runner, Does.Contain("SmokeAutostartService"));
            });
        }

        [Test]
        public void RunAsync_NonEmptyPreservationPhaseVerifiesPreExistingLocalFiles()
        {
            string runner = File.ReadAllText(GetDesktopFilePath("Startup/DesktopWindowsVirtualFilesSmokeRunner.cs"));

            Assert.Multiple(() =>
            {
                Assert.That(runner, Does.Contain("\"non-empty-preservation\""));
                Assert.That(runner, Does.Contain("RunNonEmptyPreservationAsync("));
                Assert.That(runner, Does.Contain("Isolated non-empty QA root prepared."));
                Assert.That(runner, Does.Contain("Pre-existing root file survived with identical content."));
                Assert.That(runner, Does.Contain("Pre-existing nested file survived with identical content."));
                Assert.That(runner, Does.Contain("Pre-existing local files uploaded and received sync baselines."));
                Assert.That(runner, Does.Contain("Pre-existing local directory tree received remote directory baselines."));
                Assert.That(runner, Does.Contain("Cloud Files finalization progress completed before smoke success for "));
                Assert.That(runner, Does.Contain("\"non-empty preservation app sync path\""));
                Assert.That(runner, Does.Contain("Pre-existing top-level directory Cloud Files status was finalized."));
                Assert.That(runner, Does.Contain("Remote-only directory Cloud Files status was finalized."));
                Assert.That(runner, Does.Contain("\"non-empty preservation uploaded top-level directory\""));
                Assert.That(runner, Does.Contain("\"non-empty preservation remote-only directory\""));
                Assert.That(runner, Does.Contain("Remote-only file became an online-only placeholder."));
                Assert.That(runner, Does.Contain("Remote-only placeholder Cloud Files status was finalized."));
                Assert.That(runner, Does.Contain("RecordingRunProgressObserver"));
                Assert.That(runner, Does.Contain("RecordingRemoteDirectorySynchronizer"));
            });
        }

        private static string GetDesktopFilePath(string relativePath)
        {
            string directory = TestContext.CurrentContext.TestDirectory;
            while (!string.IsNullOrWhiteSpace(directory))
            {
                string candidate = Path.Combine(directory, "src", "Cotton.Sync.Desktop", relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                string? parent = Directory.GetParent(directory)?.FullName;
                if (parent == directory)
                {
                    break;
                }

                directory = parent ?? string.Empty;
            }

            throw new FileNotFoundException(relativePath + " was not found from the test directory.");
        }
    }
}
