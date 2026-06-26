// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Runners;
using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Local;
using Cotton.Sync.State;

namespace Cotton.Sync.Desktop.Platform
{
    internal sealed class WindowsVirtualFilesDehydrationPairWork : ISyncPairWork
    {
        private const int FileAttributePinned = 0x00080000;
        private const int FileAttributeUnpinned = 0x00100000;
        private const int FileAttributeRecallOnDataAccess = 0x00400000;

        private readonly ISyncPairWork _inner;
        private readonly ISyncStateStore _stateStore;
        private readonly IWindowsCloudFilesAdapter _cloudFiles;
        private readonly ILocalFileContentHasher _contentHasher;
        private readonly IWindowsCloudFilesDiagnostics _diagnostics;
        private readonly ILocalChangeSuppression? _localChangeSuppression;
        private readonly Func<string, WindowsVirtualFileDiskState?> _readDiskState;

        public WindowsVirtualFilesDehydrationPairWork(
            ISyncPairWork inner,
            ISyncStateStore stateStore,
            IWindowsCloudFilesAdapter cloudFiles,
            ILocalFileContentHasher? contentHasher = null,
            IWindowsCloudFilesDiagnostics? diagnostics = null,
            Func<string, WindowsVirtualFileDiskState?>? readDiskState = null,
            ILocalChangeSuppression? localChangeSuppression = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
            _cloudFiles = cloudFiles ?? throw new ArgumentNullException(nameof(cloudFiles));
            _contentHasher = contentHasher ?? new LocalFileScanner();
            _diagnostics = diagnostics ?? WindowsCloudFilesDiagnostics.Shared;
            _localChangeSuppression = localChangeSuppression;
            _readDiskState = readDiskState ?? ReadDiskState;
        }

        public Task RunOnceAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
        {
            return _inner.RunOnceAsync(syncPair, cancellationToken);
        }

        public async Task RunOnceAsync(
            SyncPairSettings syncPair,
            SyncRunRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(syncPair);
            ArgumentNullException.ThrowIfNull(request);
            if (syncPair.Mode != SyncPairMode.WindowsVirtualFiles || request.IsFull)
            {
                await _inner.RunOnceAsync(syncPair, request, cancellationToken).ConfigureAwait(false);
                return;
            }

            List<string> remainingPaths = [];
            foreach (string relativePath in request.LocalChangedPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await TryHandleManualHydrationAsync(syncPair, relativePath, cancellationToken)
                        .ConfigureAwait(false)
                    && !await TryHandleManualDehydrationAsync(syncPair, relativePath, cancellationToken)
                        .ConfigureAwait(false))
                {
                    remainingPaths.Add(relativePath);
                }
            }

            if (remainingPaths.Count == 0)
            {
                return;
            }

            SyncRunRequest remainingRequest = remainingPaths.Count == request.LocalChangedPaths.Count
                ? request
                : SyncRunRequest.ForLocalChangedPaths(remainingPaths);
            await _inner.RunOnceAsync(syncPair, remainingRequest, cancellationToken).ConfigureAwait(false);
        }

        private async Task<bool> TryHandleManualHydrationAsync(
            SyncPairSettings syncPair,
            string relativePath,
            CancellationToken cancellationToken)
        {
            string normalizedPath;
            try
            {
                normalizedPath = SyncPath.Normalize(relativePath);
            }
            catch (ArgumentException)
            {
                return false;
            }

            SyncStateEntry? state = await _stateStore
                .GetAsync(syncPair.Id.ToString("D"), normalizedPath, cancellationToken)
                .ConfigureAwait(false);
            if (!IsTrackedVirtualFile(state))
            {
                return false;
            }

            string fullPath;
            try
            {
                fullPath = ResolveFullPath(syncPair.LocalRootPath, normalizedPath);
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (NotSupportedException)
            {
                return false;
            }

            WindowsVirtualFileDiskState? diskState;
            try
            {
                diskState = _readDiskState(fullPath);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }

            if (diskState is null || !IsManualAlwaysKeepCandidate(diskState.Attributes, state!.PlaceholderHydrationState))
            {
                return false;
            }

            _localChangeSuppression?.SuppressProviderWrite(syncPair.Id, syncPair.LocalRootPath, normalizedPath);
            try
            {
                _cloudFiles.HydratePlaceholder(syncPair, normalizedPath);
            }
            catch (Exception exception)
            {
                RecordFailed(syncPair, normalizedPath, "Explorer Always keep on this device hydration failed: " + exception.Message);
                throw;
            }

            WindowsVirtualFileDiskState? hydratedState = _readDiskState(fullPath);
            if (hydratedState is null)
            {
                const string details = "Hydrated placeholder is missing after Explorer Always keep on this device.";
                RecordFailed(syncPair, normalizedPath, details);
                throw new InvalidOperationException(details);
            }

            if (!SizeMatchesBaseline(state!, hydratedState.Length))
            {
                const string details = "Hydrated local size differs from the tracked remote file.";
                RecordFailed(syncPair, normalizedPath, details);
                throw new InvalidOperationException(details);
            }

            if (!await ContentMatchesRemoteAsync(state!, normalizedPath, fullPath, hydratedState, cancellationToken)
                    .ConfigureAwait(false))
            {
                const string details = "Hydrated local content differs from the tracked remote file.";
                RecordFailed(syncPair, normalizedPath, details);
                throw new InvalidOperationException(details);
            }

            state!.PlaceholderHydrationState = SyncPlaceholderHydrationState.Hydrated;
            state.LocalContentHash = state.RemoteContentHash;
            state.LocalLastWriteUtc = hydratedState.LastWriteUtc;
            state.LocalSizeBytes = hydratedState.Length;
            state.SyncedAtUtc = DateTime.UtcNow;
            await _stateStore.UpsertAsync(state, cancellationToken).ConfigureAwait(false);
            _diagnostics.Record(
                "manual-always-keep",
                "completed",
                syncPair.Id.ToString("D"),
                syncPair.LocalRootPath,
                normalizedPath,
                "Explorer Always keep on this device hydrated the tracked placeholder.");
            return true;
        }

        private async Task<bool> TryHandleManualDehydrationAsync(
            SyncPairSettings syncPair,
            string relativePath,
            CancellationToken cancellationToken)
        {
            string normalizedPath;
            try
            {
                normalizedPath = SyncPath.Normalize(relativePath);
            }
            catch (ArgumentException)
            {
                return false;
            }

            SyncStateEntry? state = await _stateStore
                .GetAsync(syncPair.Id.ToString("D"), normalizedPath, cancellationToken)
                .ConfigureAwait(false);
            if (!IsTrackedVirtualFile(state))
            {
                return false;
            }

            string fullPath;
            try
            {
                fullPath = ResolveFullPath(syncPair.LocalRootPath, normalizedPath);
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (NotSupportedException)
            {
                return false;
            }

            WindowsVirtualFileDiskState? diskState;
            try
            {
                diskState = _readDiskState(fullPath);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }

            if (diskState is null || !IsManualFreeUpSpaceCandidate(diskState.Attributes))
            {
                return false;
            }

            if (!SizeMatchesBaseline(state!, diskState.Length))
            {
                RecordSkipped(syncPair, normalizedPath, "Local size differs from the tracked remote file.");
                return false;
            }

            if (!await ContentMatchesRemoteAsync(state!, normalizedPath, fullPath, diskState, cancellationToken)
                    .ConfigureAwait(false))
            {
                RecordSkipped(syncPair, normalizedPath, "Local content differs from the tracked remote file.");
                return false;
            }

            _localChangeSuppression?.SuppressProviderWrite(syncPair.Id, syncPair.LocalRootPath, normalizedPath);
            _cloudFiles.DehydratePlaceholder(syncPair, normalizedPath);
            state!.PlaceholderHydrationState = SyncPlaceholderHydrationState.Dehydrated;
            state.LocalContentHash = null;
            state.LocalLastWriteUtc = null;
            state.LocalSizeBytes = null;
            state.SyncedAtUtc = DateTime.UtcNow;
            await _stateStore.UpsertAsync(state, cancellationToken).ConfigureAwait(false);
            _diagnostics.Record(
                "manual-free-up-space",
                "completed",
                syncPair.Id.ToString("D"),
                syncPair.LocalRootPath,
                normalizedPath,
                "Explorer Free up space dehydrated the tracked placeholder.");
            return true;
        }

        private async Task<bool> ContentMatchesRemoteAsync(
            SyncStateEntry state,
            string normalizedPath,
            string fullPath,
            WindowsVirtualFileDiskState diskState,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(state.RemoteContentHash))
            {
                return false;
            }

            var local = new LocalFileSnapshot
            {
                RelativePath = normalizedPath,
                FullPath = fullPath,
                SizeBytes = diskState.Length,
                LastWriteUtc = diskState.LastWriteUtc,
            };
            string hash = await _contentHasher.ComputeContentHashAsync(local, cancellationToken).ConfigureAwait(false);
            return string.Equals(hash, state.RemoteContentHash, StringComparison.OrdinalIgnoreCase);
        }

        private void RecordSkipped(SyncPairSettings syncPair, string normalizedPath, string details)
        {
            _diagnostics.Record(
                "manual-free-up-space",
                "skipped",
                syncPair.Id.ToString("D"),
                syncPair.LocalRootPath,
                normalizedPath,
                details);
        }

        private void RecordFailed(SyncPairSettings syncPair, string normalizedPath, string details)
        {
            _diagnostics.Record(
                "manual-always-keep",
                "failed",
                syncPair.Id.ToString("D"),
                syncPair.LocalRootPath,
                normalizedPath,
                details);
        }

        private static bool IsTrackedVirtualFile(SyncStateEntry? state)
        {
            return state is
            {
                Kind: SyncEntryKind.File,
                PlaceholderIdentity.Length: > 0,
            };
        }

        private static bool SizeMatchesBaseline(SyncStateEntry state, long localLength)
        {
            long? expectedLength = state.RemoteSizeBytes ?? state.LocalSizeBytes;
            return !expectedLength.HasValue || expectedLength.Value == localLength;
        }

        private static bool IsManualFreeUpSpaceCandidate(FileAttributes attributes)
        {
            return (attributes & FileAttributes.ReparsePoint) != 0
                && HasRawAttribute(attributes, FileAttributeUnpinned)
                && !HasRawAttribute(attributes, FileAttributeRecallOnDataAccess)
                && (attributes & FileAttributes.Offline) == 0;
        }

        private static bool IsManualAlwaysKeepCandidate(
            FileAttributes attributes,
            SyncPlaceholderHydrationState hydrationState)
        {
            return (attributes & FileAttributes.ReparsePoint) != 0
                && HasRawAttribute(attributes, FileAttributePinned)
                && (hydrationState != SyncPlaceholderHydrationState.Hydrated
                    || HasRawAttribute(attributes, FileAttributeRecallOnDataAccess)
                    || (attributes & FileAttributes.Offline) != 0);
        }

        private static bool HasRawAttribute(FileAttributes attributes, int attribute)
        {
            return (((int)attributes) & attribute) == attribute;
        }

        private static string ResolveFullPath(string localRootPath, string normalizedRelativePath)
        {
            string root = Path.GetFullPath(localRootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullPath = Path.GetFullPath(Path.Combine(
                root,
                normalizedRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            string rootWithSeparator = root + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Virtual file path escaped the sync root.", nameof(normalizedRelativePath));
            }

            return fullPath;
        }

        private static WindowsVirtualFileDiskState? ReadDiskState(string fullPath)
        {
            if (!File.Exists(fullPath))
            {
                return null;
            }

            var info = new FileInfo(fullPath);
            info.Refresh();
            return new WindowsVirtualFileDiskState(info.Attributes, info.Length, info.LastWriteTimeUtc);
        }
    }

    internal sealed record WindowsVirtualFileDiskState(
        FileAttributes Attributes,
        long Length,
        DateTime LastWriteUtc);
}
