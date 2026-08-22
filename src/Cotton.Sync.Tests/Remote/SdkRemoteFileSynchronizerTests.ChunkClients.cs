// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Cotton.Auth;
using Cotton.Files;
using Cotton.Nodes;
using Cotton.Settings;
using Cotton.Sdk;
using Cotton.Sdk.Auth;
using Cotton.Sdk.Chunks;
using Cotton.Sdk.Files;
using Cotton.Sdk.Nodes;
using Cotton.Sdk.Notifications;
using Cotton.Sdk.Realtime;
using Cotton.Sdk.Settings;
using Cotton.Sdk.Sync;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;

namespace Cotton.Sync.Tests.Remote
{
    public partial class SdkRemoteFileSynchronizerTests
    {
        private class RecordingProgress<T> : IProgress<T>
        {
            public List<T> Values { get; } = [];

            public void Report(T value)
            {
                Values.Add(value);
            }
        }

        private class SignalingProgress<T> : IProgress<T>
        {
            private readonly Func<T, bool> _matches;
            private readonly TaskCompletionSource<T> _match = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public SignalingProgress(Func<T, bool> matches)
            {
                _matches = matches;
            }

            public void Report(T value)
            {
                if (_matches(value))
                {
                    _match.TrySetResult(value);
                }
            }

            public async Task<T> WaitForMatchAsync()
            {
                return await _match.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }

        private class FakeCottonCloudClient : ICottonCloudClient
        {
            public FakeCottonCloudClient(int chunkSizeBytes)
            {
                SettingsClient = new FakeSettingsClient(chunkSizeBytes);
            }

            public ICottonAuthClient Auth => throw new NotSupportedException();

            public FakeSettingsClient SettingsClient { get; }

            public FakeChunkClient ChunksClient { get; } = new();

            public FakeFileClient FilesClient { get; } = new();

            public FakeNodeClient NodesClient { get; } = new();

            public ICottonSettingsClient Settings => SettingsClient;

            public ICottonChunkClient Chunks => ChunksClient;

            public ICottonFileClient Files => FilesClient;

            public ICottonNodeClient Nodes => NodesClient;

            public ICottonNotificationClient Notifications => throw new NotSupportedException();

            public ICottonSyncClient Sync => throw new NotSupportedException();

            public ICottonRealtimeClient Realtime => throw new NotSupportedException();

            public ValueTask DisposeAsync()
            {
                return ValueTask.CompletedTask;
            }
        }

        private class FakeSettingsClient : ICottonSettingsClient
        {
            private readonly int _chunkSizeBytes;

            public FakeSettingsClient(int chunkSizeBytes)
            {
                _chunkSizeBytes = chunkSizeBytes;
            }

            public int Calls { get; private set; }

            public Task<ClientSettingsDto> GetAsync(CancellationToken cancellationToken = default)
            {
                Calls++;
                return Task.FromResult(new ClientSettingsDto
                {
                    MaxChunkSizeBytes = _chunkSizeBytes,
                    SupportedHashAlgorithm = "SHA-256",
                });
            }
        }

        private class FakeChunkClient : ICottonChunkClient
        {
            private readonly object _gate = new();
            private readonly Dictionary<string, TaskCompletionSource> _blockedUploads = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, TaskCompletionSource> _blockedUploadAttempts = new(StringComparer.OrdinalIgnoreCase);
            private int _activeOperations;

            public HashSet<string> ExistingHashes { get; } = new(StringComparer.OrdinalIgnoreCase);

            public List<string> ExistsChecks { get; } = [];

            public List<(string Hash, byte[] Bytes)> UploadedChunks { get; } = [];

            public TimeSpan OperationDelay { get; set; }

            public int MaxConcurrentOperations { get; private set; }

            public void BlockUpload(string hash)
            {
                lock (_gate)
                {
                    _blockedUploads[hash] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _blockedUploadAttempts[hash] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }

            public async Task WaitForUploadAttemptAsync(string hash)
            {
                TaskCompletionSource? attempt;
                lock (_gate)
                {
                    _blockedUploadAttempts.TryGetValue(hash, out attempt);
                }

                if (attempt is null)
                {
                    throw new InvalidOperationException("No blocked upload was registered for " + hash + ".");
                }

                await attempt.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }

            public void ReleaseUpload(string hash)
            {
                TaskCompletionSource? block;
                lock (_gate)
                {
                    if (!_blockedUploads.TryGetValue(hash, out block))
                    {
                        return;
                    }

                    _blockedUploads.Remove(hash);
                }

                block.SetResult();
            }

            public async Task<bool> ExistsAsync(string hash, CancellationToken cancellationToken = default)
            {
                await TrackOperationAsync(cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    ExistsChecks.Add(hash);
                    return ExistingHashes.Contains(hash);
                }
            }

            public async Task UploadRawAsync(
                string hash,
                Stream content,
                string contentType = "application/octet-stream",
                CancellationToken cancellationToken = default)
            {
                BeginOperation();
                try
                {
                    await WaitForUploadReleaseAsync(hash, cancellationToken).ConfigureAwait(false);
                    await DelayOperationAsync(cancellationToken).ConfigureAwait(false);
                    await using MemoryStream copy = new MemoryStream();
                    await content.CopyToAsync(copy, cancellationToken);
                    lock (_gate)
                    {
                        UploadedChunks.Add((hash, copy.ToArray()));
                        ExistingHashes.Add(hash);
                    }
                }
                finally
                {
                    EndOperation();
                }
            }

            private async Task TrackOperationAsync(CancellationToken cancellationToken)
            {
                BeginOperation();
                try
                {
                    await DelayOperationAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    EndOperation();
                }
            }

            private async Task WaitForUploadReleaseAsync(string hash, CancellationToken cancellationToken)
            {
                TaskCompletionSource? block;
                TaskCompletionSource? attempt;
                lock (_gate)
                {
                    _blockedUploads.TryGetValue(hash, out block);
                    _blockedUploadAttempts.TryGetValue(hash, out attempt);
                }

                if (block is not null)
                {
                    attempt?.TrySetResult();
                    await block.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            private void BeginOperation()
            {
                int activeOperations = Interlocked.Increment(ref _activeOperations);
                lock (_gate)
                {
                    MaxConcurrentOperations = Math.Max(MaxConcurrentOperations, activeOperations);
                }
            }

            private async Task DelayOperationAsync(CancellationToken cancellationToken)
            {
                if (OperationDelay > TimeSpan.Zero)
                {
                    await Task.Delay(OperationDelay, cancellationToken).ConfigureAwait(false);
                }
            }

            private void EndOperation()
            {
                Interlocked.Decrement(ref _activeOperations);
            }
        }

    }
}
