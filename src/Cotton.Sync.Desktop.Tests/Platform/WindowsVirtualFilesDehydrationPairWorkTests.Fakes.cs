// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.Progress;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Local;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsVirtualFilesDehydrationPairWorkTests
    {
        private class RecordingSyncPairWork : ISyncPairWork
        {
            public List<SyncRunRequest> Requests { get; } = [];

            public Task RunOnceAsync(SyncPairSettings syncPair, CancellationToken cancellationToken = default)
            {
                Requests.Add(SyncRunRequest.Full);
                return Task.CompletedTask;
            }

            public Task RunOnceAsync(
                SyncPairSettings syncPair,
                SyncRunRequest request,
                CancellationToken cancellationToken = default)
            {
                Requests.Add(request);
                return Task.CompletedTask;
            }
        }

        private class FakeContentHasher : ILocalFileContentHasher
        {
            private readonly string _hash;

            public FakeContentHasher(string hash)
            {
                _hash = hash;
            }

            public Task<string> ComputeContentHashAsync(
                LocalFileSnapshot localFile,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(_hash);
            }
        }

        private record SuppressedWrite(Guid SyncPairId, string LocalRootPath, string RelativePath);

        private class RecordingLocalChangeSuppression : ILocalChangeSuppression
        {
            public List<SuppressedWrite> SuppressedWrites { get; } = [];

            public List<SuppressedWrite> SuppressedPinnedWrites { get; } = [];

            public int ProviderWriteBurstCount { get; private set; }

            public void SuppressProviderWrite(Guid syncPairId, string localRootPath, string relativePath)
            {
                SuppressedWrites.Add(new SuppressedWrite(syncPairId, localRootPath, relativePath));
            }

            public void SuppressProviderPinnedWrite(Guid syncPairId, string localRootPath, string relativePath)
            {
                SuppressedPinnedWrites.Add(new SuppressedWrite(syncPairId, localRootPath, relativePath));
            }

            public void SuppressProviderDirectoryWrite(Guid syncPairId, string localRootPath, string relativePath)
            {
                SuppressedWrites.Add(new SuppressedWrite(syncPairId, localRootPath, relativePath));
            }

            public void SuppressProviderFileCreation(Guid syncPairId, string localRootPath, string relativePath)
            {
            }

            public IDisposable SuppressProviderWriteBurst(Guid syncPairId, string localRootPath)
            {
                ProviderWriteBurstCount++;
                return NoopDisposable.Instance;
            }

            public bool ShouldSuppress(LocalSyncRootChange change)
            {
                return false;
            }
        }

        private class RecordingRunProgressPublisher : IAppRunProgressPublisher
        {
            public List<AppRunProgress> Progress { get; } = [];

            public IDisposable Subscribe(IObserver<AppRunProgress> observer)
            {
                return NoopDisposable.Instance;
            }

            public void Publish(AppRunProgress progress)
            {
                Progress.Add(progress);
            }
        }

        private class NoopDisposable : IDisposable
        {
            public static NoopDisposable Instance { get; } = new();

            public void Dispose()
            {
            }
        }

        private class FakeCloudFilesAdapter : IWindowsCloudFilesAdapter
        {
            public List<string> DehydratedPaths { get; } = [];

            public List<string> HydratedPaths { get; } = [];

            public List<string> RestoredPaths { get; } = [];

            public List<string> PinnedPaths { get; } = [];

            public List<string> InSyncPaths { get; } = [];

            public bool ContentMatchesForDehydration { get; init; } = true;

            public WindowsCloudFilesPlaceholderState PlaceholderState { get; init; } =
                WindowsCloudFilesPlaceholderState.Placeholder | WindowsCloudFilesPlaceholderState.InSync;

            public RemoteFilePlaceholderResult CreateFilePlaceholder(RemoteFilePlaceholderRequest request)
            {
                throw new NotSupportedException();
            }

            public RemoteFilePlaceholderResult RestoreMissingFilePlaceholder(
                SyncPairSettings syncPair,
                SyncStateEntry fileState)
            {
                RestoredPaths.Add(fileState.RelativePath);
                return new RemoteFilePlaceholderResult(
                    fileState.PlaceholderIdentity,
                    SyncPlaceholderHydrationState.RemoteOnly);
            }

            public void UnregisterSyncRoot(SyncPairSettings syncPair)
            {
                throw new NotSupportedException();
            }

            public void DehydratePlaceholder(SyncPairSettings syncPair, string relativePath)
            {
                DehydratedPaths.Add(relativePath);
            }

            public Task<bool> DehydratePlaceholderIfContentMatchesAsync(
                SyncPairSettings syncPair,
                string relativePath,
                string expectedContentHash,
                Action? contentValidated,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ContentMatchesForDehydration)
                {
                    return Task.FromResult(false);
                }

                contentValidated?.Invoke();
                DehydratedPaths.Add(relativePath);
                return Task.FromResult(true);
            }

            public void HydratePlaceholder(SyncPairSettings syncPair, string relativePath)
            {
                HydratedPaths.Add(relativePath);
            }

            public void PinPlaceholder(SyncPairSettings syncPair, string relativePath)
            {
                PinnedPaths.Add(relativePath);
            }

            public void SetInSyncState(SyncPairSettings syncPair, string relativePath)
            {
                InSyncPaths.Add(relativePath);
            }

            public WindowsCloudFilesPlaceholderState GetPlaceholderState(
                SyncPairSettings syncPair,
                string? relativePath = null)
            {
                return PlaceholderState;
            }

            public WindowsCloudFilesConnection ConnectSyncRoot(
                SyncPairSettings syncPair,
                IWindowsCloudFilesCallbackHandler callbackHandler)
            {
                throw new NotSupportedException();
            }

            public void TransferData(WindowsCloudFilesTransferData transfer)
            {
                throw new NotSupportedException();
            }
        }

        private class FakeSyncStateStore : ISyncStateStore
        {
            private readonly Dictionary<string, SyncStateEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

            public int UpsertManyCallCount { get; private set; }

            public void UpsertEntry(SyncStateEntry entry)
            {
                _entries[CreateKey(entry.SyncPairId, entry.RelativePath)] = entry;
            }

            public SyncStateEntry GetRequired(Guid syncPairId, string relativePath)
            {
                return _entries[CreateKey(syncPairId.ToString("D"), relativePath)];
            }

            public Task InitializeAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<SyncStateEntry>> LoadPairAsync(
                string syncPairId,
                CancellationToken cancellationToken = default)
            {
                IReadOnlyList<SyncStateEntry> entries = _entries.Values
                    .Where(entry => string.Equals(entry.SyncPairId, syncPairId, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                return Task.FromResult(entries);
            }

            public async IAsyncEnumerable<SyncStateEntry> LoadPairEntriesAsync(
                string syncPairId,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                foreach (SyncStateEntry entry in _entries.Values)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.Equals(entry.SyncPairId, syncPairId, StringComparison.OrdinalIgnoreCase))
                    {
                        yield return entry;
                    }
                }

                await Task.CompletedTask.ConfigureAwait(false);
            }

            public Task<DateTime?> GetPairLastSyncedAtUtcAsync(
                string syncPairId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<DateTime?>(null);
            }

            public Task<SyncChangeCursor> GetChangeCursorAsync(
                string syncPairId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new SyncChangeCursor { SyncPairId = syncPairId });
            }

            public Task<SyncStateEntry?> GetAsync(
                string syncPairId,
                string relativePath,
                CancellationToken cancellationToken = default)
            {
                _entries.TryGetValue(CreateKey(syncPairId, relativePath), out SyncStateEntry? entry);
                return Task.FromResult(entry);
            }

            public Task UpsertAsync(SyncStateEntry entry, CancellationToken cancellationToken = default)
            {
                UpsertEntry(entry);
                return Task.CompletedTask;
            }

            public Task UpsertManyAsync(
                IReadOnlyCollection<SyncStateEntry> entries,
                CancellationToken cancellationToken = default)
            {
                UpsertManyCallCount++;
                foreach (SyncStateEntry entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    UpsertEntry(entry);
                }

                return Task.CompletedTask;
            }

            public Task SaveChangeCursorAsync(SyncChangeCursor cursor, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task DeleteAsync(string syncPairId, string relativePath, CancellationToken cancellationToken = default)
            {
                _entries.Remove(CreateKey(syncPairId, relativePath));
                return Task.CompletedTask;
            }

            public Task DeletePairAsync(string syncPairId, CancellationToken cancellationToken = default)
            {
                foreach (string key in _entries
                    .Where(item => item.Value.SyncPairId.Equals(syncPairId, StringComparison.OrdinalIgnoreCase))
                    .Select(static item => item.Key)
                    .ToArray())
                {
                    _entries.Remove(key);
                }

                return Task.CompletedTask;
            }

            public Task ReplacePairAsync(
                string syncPairId,
                IReadOnlyCollection<SyncStateEntry> entries,
                CancellationToken cancellationToken = default)
            {
                foreach (string key in _entries
                    .Where(item => item.Value.SyncPairId.Equals(syncPairId, StringComparison.OrdinalIgnoreCase))
                    .Select(static item => item.Key)
                    .ToArray())
                {
                    _entries.Remove(key);
                }

                foreach (SyncStateEntry entry in entries)
                {
                    UpsertEntry(entry);
                }

                return Task.CompletedTask;
            }

            private static string CreateKey(string syncPairId, string relativePath)
            {
                return syncPairId.ToUpperInvariant() + "|" + SyncPath.ToKey(relativePath);
            }
        }
    }
}
