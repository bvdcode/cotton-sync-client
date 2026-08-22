// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Cotton.Files;
using Cotton.Nodes;
using Cotton.Sdk;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using Microsoft.Extensions.Logging;

namespace Cotton.Sync.Tests
{
    public partial class SyncEngineTests
    {

        private class RecordingLogger<T> : ILogger<T>
        {
            public List<(LogLevel Level, string Message)> Entries { get; } = [];

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                return null;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return true;
            }

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                Entries.Add((logLevel, formatter(state, exception)));
            }
        }


        private class RecordingProgress<T> : IProgress<T>
        {
            private readonly Action<T>? _onReport;

            public RecordingProgress(Action<T>? onReport = null)
            {
                _onReport = onReport;
            }

            public List<T> Values { get; } = [];

            public void Report(T value)
            {
                Values.Add(value);
                _onReport?.Invoke(value);
            }
        }


        private class FakeRemoteFilePlaceholderWriter :
            IRemoteFilePlaceholderWriter,
            IRemoteFilePlaceholderPopulationObserver,
            IRemoteFileMaterializationObserver,
            IRemoteDirectoryMaterializationObserver,
            IRemoteDirectoryTreePopulationObserver
        {
            private readonly object _requestsLock = new();

            public byte[] PlaceholderIdentity { get; } = [0x43, 0x4F, 0x54, 0x54, 0x4F, 0x4E];

            public List<RemoteFilePlaceholderRequest> Requests { get; } = [];

            public List<RemoteFileMaterializationRequest> FileMaterializationRequests { get; } = [];

            public List<bool> FileExistsWhenMaterializationRequested { get; } = [];

            public List<RemoteFileMaterializationRequest> CompletedFileMaterializationRequests { get; } = [];

            public List<bool> FileExistsWhenMaterializationCompleted { get; } = [];

            public List<RemoteDirectoryMaterializationRequest> DirectoryRequests { get; } = [];

            public List<RemoteDirectoryMaterializationRequest> CompletedDirectoryRequests { get; } = [];

            public List<IReadOnlyList<RemoteDirectoryMaterializationRequest>> CompletedDirectoryTreeRequests { get; } = [];

            public List<bool> DirectoryExistsWhenCompleted { get; } = [];

            public List<int> PlaceholderCountWhenDirectoryTreeCompleted { get; } = [];

            public string? UnavailableReason { get; set; }

            public SyncPlaceholderHydrationState HydrationState { get; set; } = SyncPlaceholderHydrationState.RemoteOnly;

            public long? LocalSizeBytes { get; set; }

            public DateTime? LocalLastWriteUtc { get; set; }

            public int BeginPopulationCalls { get; private set; }

            public int EndPopulationCalls { get; private set; }

            public IDisposable BeginPopulation(string syncPairId, string localRootPath)
            {
                BeginPopulationCalls++;
                return new PopulationLease(this);
            }

            public Task BeforeWriteFileAsync(
                RemoteFileMaterializationRequest request,
                CancellationToken cancellationToken = default)
            {
                FileMaterializationRequests.Add(request);
                FileExistsWhenMaterializationRequested.Add(File.Exists(Path.Combine(
                    request.LocalRootPath,
                    request.RelativePath.Replace('/', Path.DirectorySeparatorChar))));
                return Task.CompletedTask;
            }

            public Task AfterWriteFileAsync(
                RemoteFileMaterializationRequest request,
                CancellationToken cancellationToken = default)
            {
                CompletedFileMaterializationRequests.Add(request);
                FileExistsWhenMaterializationCompleted.Add(File.Exists(Path.Combine(
                    request.LocalRootPath,
                    request.RelativePath.Replace('/', Path.DirectorySeparatorChar))));
                return Task.CompletedTask;
            }

            public Task BeforeCreateDirectoryAsync(
                RemoteDirectoryMaterializationRequest request,
                CancellationToken cancellationToken = default)
            {
                DirectoryRequests.Add(request);
                return Task.CompletedTask;
            }

            public Task AfterCreateDirectoryAsync(
                RemoteDirectoryMaterializationRequest request,
                CancellationToken cancellationToken = default)
            {
                CompletedDirectoryRequests.Add(request);
                DirectoryExistsWhenCompleted.Add(Directory.Exists(Path.Combine(
                    request.LocalRootPath,
                    request.RelativePath.Replace('/', Path.DirectorySeparatorChar))));
                return Task.CompletedTask;
            }

            public Task AfterDirectoryTreePopulationAsync(
                IReadOnlyList<RemoteDirectoryMaterializationRequest> directories,
                CancellationToken cancellationToken = default)
            {
                CompletedDirectoryTreeRequests.Add(directories.ToArray());
                lock (_requestsLock)
                {
                    PlaceholderCountWhenDirectoryTreeCompleted.Add(Requests.Count);
                }

                return Task.CompletedTask;
            }

            public Task<RemoteFilePlaceholderResult> CreatePlaceholderAsync(
                RemoteFilePlaceholderRequest request,
                CancellationToken cancellationToken = default)
            {
                lock (_requestsLock)
                {
                    Requests.Add(request);
                }

                if (!string.IsNullOrWhiteSpace(UnavailableReason))
                {
                    throw new RemoteFilePlaceholderUnavailableException(request.RelativePath, UnavailableReason);
                }

                return Task.FromResult(new RemoteFilePlaceholderResult(
                    PlaceholderIdentity,
                    HydrationState,
                    LocalSizeBytes,
                    LocalLastWriteUtc));
            }

            private class PopulationLease : IDisposable
            {
                private FakeRemoteFilePlaceholderWriter? _owner;

                public PopulationLease(FakeRemoteFilePlaceholderWriter owner)
                {
                    _owner = owner;
                }

                public void Dispose()
                {
                    FakeRemoteFilePlaceholderWriter? owner = Interlocked.Exchange(ref _owner, null);
                    if (owner is not null)
                    {
                        owner.EndPopulationCalls++;
                    }
                }
            }
        }


        private class SignalingRemoteFilePlaceholderWriter : IRemoteFilePlaceholderWriter
        {
            private static readonly byte[] PlaceholderIdentity = [0x43, 0x4F, 0x54, 0x54, 0x4F, 0x4E];
            private readonly object _requestsLock = new();
            private readonly TaskCompletionSource _firstPlaceholderStarted;

            private readonly SyncPlaceholderHydrationState _hydrationState;

            public SignalingRemoteFilePlaceholderWriter(
                TaskCompletionSource firstPlaceholderStarted,
                SyncPlaceholderHydrationState hydrationState = SyncPlaceholderHydrationState.RemoteOnly)
            {
                _firstPlaceholderStarted = firstPlaceholderStarted;
                _hydrationState = hydrationState;
            }

            public List<RemoteFilePlaceholderRequest> Requests { get; } = [];

            public Task<RemoteFilePlaceholderResult> CreatePlaceholderAsync(
                RemoteFilePlaceholderRequest request,
                CancellationToken cancellationToken = default)
            {
                lock (_requestsLock)
                {
                    Requests.Add(request);
                }

                _firstPlaceholderStarted.TrySetResult();
                return Task.FromResult(new RemoteFilePlaceholderResult(PlaceholderIdentity, _hydrationState));
            }
        }


        private class BatchRemoteFilePlaceholderWriter : IRemoteFilePlaceholderBatchWriter
        {
            private static readonly byte[] PlaceholderIdentity = [0x43, 0x4F, 0x54, 0x54, 0x4F, 0x4E];

            public List<string> SingleRequests { get; } = [];

            public List<IReadOnlyList<string>> Batches { get; } = [];

            public Task<RemoteFilePlaceholderResult> CreatePlaceholderAsync(
                RemoteFilePlaceholderRequest request,
                CancellationToken cancellationToken = default)
            {
                SingleRequests.Add(request.RelativePath);
                return Task.FromResult(new RemoteFilePlaceholderResult(PlaceholderIdentity));
            }

            public Task<IReadOnlyList<RemoteFilePlaceholderBatchResult>> CreatePlaceholdersAsync(
                IReadOnlyList<RemoteFilePlaceholderRequest> requests,
                CancellationToken cancellationToken = default)
            {
                Batches.Add(requests.Select(static request => request.RelativePath).ToArray());
                return Task.FromResult<IReadOnlyList<RemoteFilePlaceholderBatchResult>>(
                    requests
                        .Select(static request => RemoteFilePlaceholderBatchResult.Success(
                            request,
                            new RemoteFilePlaceholderResult(PlaceholderIdentity)))
                        .ToArray());
            }
        }
    }
}
