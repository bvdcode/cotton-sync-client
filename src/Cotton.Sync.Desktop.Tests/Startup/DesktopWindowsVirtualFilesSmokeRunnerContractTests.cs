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
                Assert.That(runner, Does.Contain("Cloud-only placeholder was replaced by a regular local file before sync."));
                Assert.That(runner, Does.Contain("new WindowsVirtualFilesUploadFinalizationPairWork("));
                Assert.That(runner, Does.Contain("new WindowsVirtualFilesDirectoryPlaceholderRepairPairWork("));
                Assert.That(runner, Does.Contain("Cloud-only replacement uploaded and persisted remote identity."));
                Assert.That(runner, Does.Contain("Uploaded replacement file Cloud Files status was finalized."));
                Assert.That(runner, Does.Contain("Uploaded replacement parent directory Cloud Files status was finalized."));
                Assert.That(runner, Does.Contain("Uploaded replacement sync root Cloud Files status was finalized."));
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
