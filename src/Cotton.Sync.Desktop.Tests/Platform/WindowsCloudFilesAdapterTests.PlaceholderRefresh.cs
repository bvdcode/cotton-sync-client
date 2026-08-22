// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Nodes;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Local;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using System.Text;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsCloudFilesAdapterTests
    {
        [Test]
        public void CreateFilePlaceholder_AllowsCloudFilesDirectoryPlaceholderAncestors()
        {
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            string root = Path.Combine(_tempDirectory, "root");
            string parent = Path.GetFullPath(Path.Combine(root, "Projects"));
            Directory.CreateDirectory(parent);
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(
                CreatePolicy(),
                nativeApi,
                isReparsePoint: path => string.Equals(Path.GetFullPath(path), parent, StringComparison.OrdinalIgnoreCase),
                isCloudFilesReparsePoint: path => string.Equals(Path.GetFullPath(path), parent, StringComparison.OrdinalIgnoreCase));

            adapter.CreateFilePlaceholder(CreateRequest(root, "Projects/remote-only.txt"));

            Assert.That(nativeApi.Placeholders.Select(static item => item.RelativeFileName), Is.EqualTo(new[] { "remote-only.txt" }));
        }

        [Test]
        public void CreateFilePlaceholder_RetriesTransientPinStatePathOpenFailure()
        {
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi
            {
                PinStateFailuresBeforeSuccess = 2,
            };
            WindowsCloudFilesDiagnostics diagnostics = new WindowsCloudFilesDiagnostics();
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(
                CreatePolicy(),
                nativeApi,
                diagnostics: diagnostics,
                transientRetryDelay: _ => { });
            string root = Path.Combine(_tempDirectory, "root");

            adapter.CreateFilePlaceholder(CreateRequest(root, "Projects/remote-only.txt"));

            IReadOnlyList<WindowsCloudFilesDiagnosticEvent> retryEvents = diagnostics.Snapshot();
            Assert.Multiple(() =>
            {
                Assert.That(nativeApi.PinStateCalls, Is.EqualTo(3));
                Assert.That(nativeApi.PinStates, Has.Count.EqualTo(1));
                Assert.That(retryEvents.Select(static item => item.Status), Is.EqualTo(new[] { "retrying", "retrying" }));
                Assert.That(retryEvents.Select(static item => item.Operation), Is.All.EqualTo("set-pin-state"));
                Assert.That(retryEvents.Select(static item => item.HResult), Is.All.EqualTo(HResultPathNotFound));
            });
        }

        [Test]
        public void CreateFilePlaceholder_UpdatesExistingCloudFilesPlaceholder()
        {
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi();
            string root = Path.Combine(_tempDirectory, "root");
            string target = Path.GetFullPath(Path.Combine(root, "Projects", "remote-only.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, string.Empty);
            RemoteFilePlaceholderRequest request = CreateRequest(root, "Projects/remote-only.txt");
            TrackExistingFilePlaceholder(nativeApi, target, request);
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(
                CreatePolicy(),
                nativeApi,
                isReparsePoint: path => string.Equals(Path.GetFullPath(path), target, StringComparison.OrdinalIgnoreCase),
                isCloudFilesReparsePoint: path => string.Equals(
                    Path.GetFullPath(path),
                    target,
                    StringComparison.OrdinalIgnoreCase));

            RemoteFilePlaceholderResult result = adapter.CreateFilePlaceholder(request);

            Assert.Multiple(() =>
            {
                Assert.That(nativeApi.Registrations, Has.Count.EqualTo(1));
                Assert.That(nativeApi.Placeholders, Is.Empty);
                Assert.That(nativeApi.UpdatedPlaceholders, Has.Count.EqualTo(1));
                Assert.That(nativeApi.PinStates, Is.Empty);
                Assert.That(nativeApi.UpdatedPlaceholders[0].BaseDirectoryPath, Is.EqualTo(Path.Combine(Path.GetFullPath(root), "Projects")));
                Assert.That(nativeApi.UpdatedPlaceholders[0].RelativeFileName, Is.EqualTo("remote-only.txt"));
                Assert.That(nativeApi.UpdatedPlaceholders[0].FileIdentity, Is.EqualTo(result.PlaceholderIdentity));
            });
        }

        [Test]
        public void CreateFilePlaceholder_RejectsForeignCloudFilesPlaceholderIdentity()
        {
            FakeCloudFilesNativeApi nativeApi = new();
            string root = Path.Combine(_tempDirectory, "root");
            string target = Path.GetFullPath(Path.Combine(root, "Projects", "remote-only.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, string.Empty);
            RemoteFilePlaceholderRequest request = CreateRequest(root, "Projects/remote-only.txt");
            RemoteFilePlaceholderRequest foreignRequest = CreateRequest(
                root,
                "Projects/remote-only.txt",
                "99999999-9999-9999-9999-999999999999");
            TrackExistingFilePlaceholder(nativeApi, target, foreignRequest);
            WindowsCloudFilesAdapter adapter = new(
                CreatePolicy(),
                nativeApi,
                isReparsePoint: _ => true,
                isCloudFilesReparsePoint: _ => true);

            RemoteFilePlaceholderUnavailableException? exception =
                Assert.Throws<RemoteFilePlaceholderUnavailableException>(
                    () => adapter.CreateFilePlaceholder(request));

            Assert.Multiple(() =>
            {
                Assert.That(exception?.Reason, Does.Contain("foreign or stale identity"));
                Assert.That(nativeApi.UpdatedPlaceholders, Is.Empty);
                Assert.That(nativeApi.Placeholders, Is.Empty);
            });
        }

        [Test]
        public void CreateFilePlaceholder_RejectsNonCloudFilesReparsePoint()
        {
            FakeCloudFilesNativeApi nativeApi = new();
            string root = Path.Combine(_tempDirectory, "root");
            string target = Path.GetFullPath(Path.Combine(root, "Projects", "remote-only.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, string.Empty);
            WindowsCloudFilesAdapter adapter = new(
                CreatePolicy(),
                nativeApi,
                isReparsePoint: path => string.Equals(
                    Path.GetFullPath(path),
                    target,
                    StringComparison.OrdinalIgnoreCase),
                isCloudFilesReparsePoint: _ => false);

            RemoteFilePlaceholderUnavailableException? exception =
                Assert.Throws<RemoteFilePlaceholderUnavailableException>(
                    () => adapter.CreateFilePlaceholder(
                        CreateRequest(root, "Projects/remote-only.txt")));

            Assert.Multiple(() =>
            {
                Assert.That(exception?.Reason, Does.Contain("non-Cloud Files reparse point"));
                Assert.That(nativeApi.UpdatedPlaceholders, Is.Empty);
                Assert.That(nativeApi.Placeholders, Is.Empty);
            });
        }

        [Test]
        public void CreateFilePlaceholder_RefreshesPinnedExistingPlaceholderAndPreservesHydration()
        {
            FakeCloudFilesNativeApi nativeApi = new();
            string root = Path.Combine(_tempDirectory, "root");
            string target = Path.GetFullPath(Path.Combine(root, "Projects", "available-offline.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "old remote content");
            RemoteFilePlaceholderRequest request = CreateRequest(root, "Projects/available-offline.txt");
            TrackExistingFilePlaceholder(nativeApi, target, request);
            WindowsCloudFilesAdapter adapter = new(
                CreatePolicy(),
                nativeApi,
                isReparsePoint: path => string.Equals(
                    Path.GetFullPath(path),
                    target,
                    StringComparison.OrdinalIgnoreCase),
                isCloudFilesReparsePoint: path => string.Equals(
                    Path.GetFullPath(path),
                    target,
                    StringComparison.OrdinalIgnoreCase),
                readFileAttributes: _ => FileAttributes.Archive
                    | FileAttributes.ReparsePoint
                    | (FileAttributes)0x00080000);

            RemoteFilePlaceholderResult result = adapter.CreateFilePlaceholder(request);

            Assert.Multiple(() =>
            {
                Assert.That(result.HydrationState, Is.EqualTo(SyncPlaceholderHydrationState.Hydrated));
                Assert.That(nativeApi.UpdatedPlaceholders, Has.Count.EqualTo(1));
                Assert.That(nativeApi.HydratedPaths, Is.EqualTo(new[] { target }));
                Assert.That(
                    nativeApi.PinStates,
                    Is.EqualTo(new[]
                    {
                        new FakeCloudFilesNativeApi.PinStateCall(target, WindowsCloudFilesPinState.Unpinned),
                        new FakeCloudFilesNativeApi.PinStateCall(target, WindowsCloudFilesPinState.Pinned),
                    }));
                Assert.That(nativeApi.InSyncPaths, Is.EqualTo(new[] { target }));
                Assert.That(
                    nativeApi.CallLog,
                    Is.EqualTo(new[]
                    {
                        "native-set-pin-state",
                        "native-update",
                        "native-hydrate",
                        "native-set-pin-state",
                        "native-set-in-sync-state",
                    }));
            });
        }

        [Test]
        public void CreateFilePlaceholder_RestoresPinnedStateWhenRefreshHydrationIsDeferred()
        {
            const int cloudFileUnsuccessful = unchecked((int)0x80070185);
            string root = Path.Combine(_tempDirectory, "root");
            string target = Path.GetFullPath(Path.Combine(root, "Projects", "available-offline.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "old remote content");
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi
            {
                HydrateAction = _ => throw new WindowsCloudFilesNativeException(
                    "CfHydratePlaceholder",
                    cloudFileUnsuccessful),
            };
            RemoteFilePlaceholderRequest request = CreateRequest(root, "Projects/available-offline.txt");
            TrackExistingFilePlaceholder(nativeApi, target, request);
            WindowsCloudFilesDiagnostics diagnostics = new WindowsCloudFilesDiagnostics();
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(
                CreatePolicy(),
                nativeApi,
                diagnostics: diagnostics,
                isReparsePoint: path => string.Equals(
                    Path.GetFullPath(path),
                    target,
                    StringComparison.OrdinalIgnoreCase),
                isCloudFilesReparsePoint: path => string.Equals(
                    Path.GetFullPath(path),
                    target,
                    StringComparison.OrdinalIgnoreCase),
                readFileAttributes: _ => FileAttributes.Archive
                    | FileAttributes.ReparsePoint
                    | (FileAttributes)0x00080000);

            RemoteFilePlaceholderResult result = adapter.CreateFilePlaceholder(request);

            Assert.Multiple(() =>
            {
                Assert.That(result.HydrationState, Is.EqualTo(SyncPlaceholderHydrationState.RemoteOnly));
                Assert.That(nativeApi.UpdatedPlaceholders, Has.Count.EqualTo(1));
                Assert.That(
                    nativeApi.PinStates,
                    Is.EqualTo(new[]
                    {
                        new FakeCloudFilesNativeApi.PinStateCall(target, WindowsCloudFilesPinState.Unpinned),
                        new FakeCloudFilesNativeApi.PinStateCall(target, WindowsCloudFilesPinState.Pinned),
                    }));
                Assert.That(nativeApi.InSyncPaths, Is.Empty);
                Assert.That(
                    nativeApi.CallLog,
                    Is.EqualTo(new[]
                    {
                        "native-set-pin-state",
                        "native-update",
                        "native-hydrate",
                        "native-set-pin-state",
                    }));
                Assert.That(
                    diagnostics.Snapshot(),
                    Has.One.Matches<WindowsCloudFilesDiagnosticEvent>(
                        item => item.Operation == "hydrate-placeholder"
                            && item.Status == "deferred"
                            && item.HResult == cloudFileUnsuccessful));
            });
        }

        [Test]
        public void CreateFilePlaceholder_RefreshesPreviouslyHydratedPlaceholderWithoutPinAttributes()
        {
            FakeCloudFilesNativeApi nativeApi = new();
            string root = Path.Combine(_tempDirectory, "root");
            string target = Path.GetFullPath(Path.Combine(root, "Projects", "available-offline.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "old remote content");
            DateTime hydratedLastWriteUtc = new(2026, 07, 17, 13, 12, 34, DateTimeKind.Utc);
            nativeApi.HydrateAction = path => File.SetLastWriteTimeUtc(path, hydratedLastWriteUtc);
            RemoteFilePlaceholderRequest request = CreateRequest(root, "Projects/available-offline.txt") with
            {
                ExistingHydrationState = SyncPlaceholderHydrationState.Hydrated,
            };
            TrackExistingFilePlaceholder(nativeApi, target, request);
            WindowsCloudFilesAdapter adapter = new(
                CreatePolicy(),
                nativeApi,
                isReparsePoint: path => string.Equals(
                    Path.GetFullPath(path),
                    target,
                    StringComparison.OrdinalIgnoreCase),
                isCloudFilesReparsePoint: path => string.Equals(
                    Path.GetFullPath(path),
                    target,
                    StringComparison.OrdinalIgnoreCase),
                readFileAttributes: _ => FileAttributes.Archive | FileAttributes.ReparsePoint);

            RemoteFilePlaceholderResult result = adapter.CreateFilePlaceholder(request);

            Assert.Multiple(() =>
            {
                Assert.That(result.HydrationState, Is.EqualTo(SyncPlaceholderHydrationState.Hydrated));
                Assert.That(nativeApi.UpdatedPlaceholders, Has.Count.EqualTo(1));
                Assert.That(nativeApi.HydratedPaths, Is.EqualTo(new[] { target }));
                Assert.That(nativeApi.PinStates, Is.Empty);
                Assert.That(nativeApi.InSyncPaths, Is.EqualTo(new[] { target }));
                Assert.That(result.LocalSizeBytes, Is.EqualTo(new FileInfo(target).Length));
                Assert.That(result.LocalLastWriteUtc, Is.EqualTo(hydratedLastWriteUtc));
                Assert.That(
                    nativeApi.CallLog,
                    Is.EqualTo(new[]
                    {
                        "native-update",
                        "native-hydrate",
                        "native-set-in-sync-state",
                    }));
            });
        }

        [Test]
        public void CreateFilePlaceholder_WhenPinnedRefreshFailsRestoresPinnedState()
        {
            FakeCloudFilesNativeApi nativeApi = new()
            {
                UpdateFailuresBeforeSuccess = 10,
            };
            string root = Path.Combine(_tempDirectory, "root");
            string target = Path.GetFullPath(Path.Combine(root, "Projects", "available-offline.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "old remote content");
            RemoteFilePlaceholderRequest request = CreateRequest(root, "Projects/available-offline.txt");
            TrackExistingFilePlaceholder(nativeApi, target, request);
            WindowsCloudFilesAdapter adapter = new(
                CreatePolicy(),
                nativeApi,
                isReparsePoint: path => string.Equals(
                    Path.GetFullPath(path),
                    target,
                    StringComparison.OrdinalIgnoreCase),
                isCloudFilesReparsePoint: path => string.Equals(
                    Path.GetFullPath(path),
                    target,
                    StringComparison.OrdinalIgnoreCase),
                readFileAttributes: _ => FileAttributes.Archive
                    | FileAttributes.ReparsePoint
                    | (FileAttributes)0x00080000,
                transientRetryDelay: _ => { });

            Assert.Throws<WindowsCloudFilesNativeException>(() =>
                adapter.CreateFilePlaceholder(request));

            Assert.Multiple(() =>
            {
                Assert.That(nativeApi.UpdateCalls, Is.EqualTo(4));
                Assert.That(
                    nativeApi.PinStates,
                    Is.EqualTo(new[]
                    {
                        new FakeCloudFilesNativeApi.PinStateCall(target, WindowsCloudFilesPinState.Unpinned),
                        new FakeCloudFilesNativeApi.PinStateCall(target, WindowsCloudFilesPinState.Pinned),
                    }));
                Assert.That(nativeApi.HydratedPaths, Is.Empty);
                Assert.That(nativeApi.InSyncPaths, Is.Empty);
            });
        }

        [Test]
        public void CreateFilePlaceholder_RetriesTransientUpdatePathOpenFailure()
        {
            FakeCloudFilesNativeApi nativeApi = new FakeCloudFilesNativeApi
            {
                UpdateFailuresBeforeSuccess = 1,
            };
            WindowsCloudFilesDiagnostics diagnostics = new WindowsCloudFilesDiagnostics();
            string root = Path.Combine(_tempDirectory, "root");
            string target = Path.GetFullPath(Path.Combine(root, "Projects", "remote-only.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, string.Empty);
            RemoteFilePlaceholderRequest request = CreateRequest(root, "Projects/remote-only.txt");
            TrackExistingFilePlaceholder(nativeApi, target, request);
            WindowsCloudFilesAdapter adapter = new WindowsCloudFilesAdapter(
                CreatePolicy(),
                nativeApi,
                diagnostics: diagnostics,
                isReparsePoint: path => string.Equals(Path.GetFullPath(path), target, StringComparison.OrdinalIgnoreCase),
                isCloudFilesReparsePoint: path => string.Equals(
                    Path.GetFullPath(path),
                    target,
                    StringComparison.OrdinalIgnoreCase),
                transientRetryDelay: _ => { });

            adapter.CreateFilePlaceholder(request);

            WindowsCloudFilesDiagnosticEvent retryEvent = diagnostics.Snapshot().Single();
            Assert.Multiple(() =>
            {
                Assert.That(nativeApi.UpdateCalls, Is.EqualTo(2));
                Assert.That(nativeApi.UpdatedPlaceholders, Has.Count.EqualTo(1));
                Assert.That(retryEvent.Operation, Is.EqualTo("update-placeholder"));
                Assert.That(retryEvent.Status, Is.EqualTo("retrying"));
                Assert.That(retryEvent.HResult, Is.EqualTo(HResultPathNotFound));
            });
        }
    }
}
