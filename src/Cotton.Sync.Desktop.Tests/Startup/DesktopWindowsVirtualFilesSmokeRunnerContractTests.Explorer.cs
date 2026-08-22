// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Startup;

namespace Cotton.Sync.Desktop.Tests.Startup
{
    public partial class DesktopWindowsVirtualFilesSmokeRunnerContractTests
    {
        [Test]
        public void RunAsync_ExplorerAlwaysKeepPhaseVerifiesPinnedHydrationPath()
        {
            string runner = ReadSmokeRunnerSources();

            Assert.Multiple(() =>
            {
                Assert.That(runner, Does.Contain("\"explorer-always-keep\""));
                Assert.That(runner, Does.Contain("RunExplorerAlwaysKeepAsync("));
                Assert.That(runner, Does.Contain("Cloud Files pinned state was applied for Always keep processing."));
                Assert.That(runner, Does.Contain("Production app Always keep handler processed the Cloud Files pin-state change."));
                Assert.That(runner, Does.Contain("Explorer Always keep hydrated the placeholder and kept it pinned."));
                Assert.That(runner, Does.Contain("Always-keep hydration updated sync-state as hydrated."));
                Assert.That(runner, Does.Contain("Repeating Explorer Always keep on this device was idempotent."));
                Assert.That(runner, Does.Contain("Always-keep placeholder Cloud Files status was finalized."));
                Assert.That(runner, Does.Contain("VerifyExplorerShellSettledStatusAsync("));
            });
        }

        [Test]
        public void RunAsync_ExplorerAlwaysKeepMissingPlaceholderPhaseVerifiesNativeRecovery()
        {
            string runner = ReadSmokeRunnerSources();

            Assert.Multiple(() =>
            {
                Assert.That(runner, Does.Contain("\"explorer-always-keep-missing-placeholder\""));
                Assert.That(
                    runner,
                    Does.Contain(
                        "context.Phase == WindowsVirtualFilesSmokePhase.ExplorerAlwaysKeepMissingPlaceholder"));
                Assert.That(runner, Does.Contain("Tracked placeholder was removed before Explorer Always keep recovery."));
                Assert.That(runner, Does.Contain("Always keep restored the missing tracked placeholder before hydration."));
                Assert.That(runner, Does.Contain("\"manual-always-keep-placeholder-repair\""));
            });
        }

        [Test]
        public void RunAsync_ExplorerAlwaysKeepDuringPopulationUsesRealWatcherAndQueuedRunner()
        {
            string runner = ReadSmokeRunnerSources();

            Assert.Multiple(() =>
            {
                Assert.That(runner, Does.Contain("\"explorer-always-keep-during-population\""));
                Assert.That(runner, Does.Contain("RunExplorerAlwaysKeepDuringPopulationAsync("));
                Assert.That(runner, Does.Contain("new FileSystemLocalSyncRootWatcher("));
                Assert.That(runner, Does.Contain("new LocalChangeSuppression()"));
                Assert.That(runner, Does.Contain("new SyncPairRunner("));
                Assert.That(runner, Does.Contain("InvokeExplorerAlwaysKeepAsync("));
                Assert.That(runner, Does.Contain("Explorer Always keep watcher event queued while initial population was active."));
                Assert.That(runner, Does.Contain("Late-created descendants inherited Always keep before initial population completed."));
                Assert.That(runner, Does.Contain("All early and late files became pinned and hydrated."));
                Assert.That(runner, Does.Contain("Second Explorer Always keep invocation removed pin without deleting hydrated content."));
                Assert.That(runner, Does.Contain("Third Explorer Always keep invocation restored pin without redownloading."));
                Assert.That(runner, Does.Contain("Holding pinned population root for "));
            });
        }

        [Test]
        public void RunAsync_ExplorerAvailabilityPhasesRequirePackagedShellRegistration()
        {
            string runner = ReadSmokeRunnerSources();

            Assert.Multiple(() =>
            {
                Assert.That(runner, Does.Contain("ExplorerAvailabilityPhases"));
                Assert.That(runner, Does.Contain("WindowsVirtualFilesSmokePhase.ExplorerFreeUpSpace"));
                Assert.That(runner, Does.Contain("WindowsVirtualFilesSmokePhase.ExplorerAlwaysKeep"));
                Assert.That(runner, Does.Contain("WindowsVirtualFilesSmokePhase.ExplorerAlwaysKeepMissingPlaceholder"));
                Assert.That(runner, Does.Contain("WindowsVirtualFilesSmokePhase.ExplorerAlwaysKeepDuringPopulation"));
                Assert.That(runner, Does.Contain("RequiresExplorerAvailabilityVerbs(phase)"));
                Assert.That(runner, Does.Contain("WindowsStorageProviderSyncRootRegistrar.TryCreateDefault()"));
                Assert.That(
                    runner,
                    Does.Contain(
                        "Explorer availability smoke requires the packaged Windows shell helper beside the desktop app."));
                Assert.That(runner, Does.Contain("storageProviderRegistrar: storageProviderRegistrar"));
            });
        }

        [Test]
        public void RunAsync_ExplorerFreeUpSpacePhaseSupportsInteractiveFolderSubtree()
        {
            string runner = ReadSmokeRunnerSources();
            int interactiveSetup = runner.IndexOf("if (interactiveFolderSmoke)", StringComparison.Ordinal);
            int connect = runner.IndexOf("connection = cloudFiles.ConnectSyncRoot(syncPair, callbackHandler)", interactiveSetup, StringComparison.Ordinal);
            int directoryPlaceholder = runner.IndexOf("cloudFiles.CreateDirectoryPlaceholder", connect, StringComparison.Ordinal);
            int filePlaceholder = runner.IndexOf("RemoteFilePlaceholderRequest placeholderRequest", directoryPlaceholder, StringComparison.Ordinal);

            Assert.Multiple(() =>
            {
                Assert.That(runner, Does.Contain("bool interactiveFolderSmoke = startupOptions.WindowsVirtualFilesSmokeHoldAfterPlaceholder > TimeSpan.Zero"));
                Assert.That(runner, Does.Contain("Hydrated folder subtree is ready for modern Explorer Free up space."));
                Assert.That(runner, Does.Contain("invoke modern Explorer Free up space on "));
                Assert.That(runner, Does.Contain("Modern Explorer folder Free up space changed the subtree pin state."));
                Assert.That(runner, Does.Contain("? [relativeFolderPath, relativePlaceholderPath]"));
                Assert.That(runner, Does.Contain("HasUnpinned(folderAttributesAfterVerb) || HasUnpinned(fileAttributesAfterVerb)"));
                Assert.That(connect, Is.GreaterThan(interactiveSetup));
                Assert.That(directoryPlaceholder, Is.GreaterThan(connect));
                Assert.That(filePlaceholder, Is.GreaterThan(directoryPlaceholder));
            });
        }

        [Test]
        public void RunAsync_ShellShareLinkTargetsPhaseVerifiesRealVfsTargets()
        {
            string runner = ReadSmokeRunnerSources();

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

    }
}
