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

        private record CreateDirectoryCall(Guid ParentNodeId, string Name, NodeDto ReturnedNode);


        private record UploadCall(
            Guid RootNodeId,
            string RelativePath,
            LocalFileSnapshot LocalFile,
            NodeFileManifestDto? ExistingRemoteFile,
            NodeFileManifestDto ReturnedFile);


        private record MoveCall(
            Guid RootNodeId,
            string RelativePath,
            NodeFileManifestDto ExistingRemoteFile,
            NodeFileManifestDto ReturnedFile);


        private abstract class DelegatingStateStore : ISyncStateStore
        {
            private readonly ISyncStateStore _inner;

            protected DelegatingStateStore(ISyncStateStore inner)
            {
                _inner = inner;
            }

            public virtual Task InitializeAsync(CancellationToken cancellationToken = default)
            {
                return _inner.InitializeAsync(cancellationToken);
            }

            public virtual Task<IReadOnlyList<SyncStateEntry>> LoadPairAsync(string syncPairId, CancellationToken cancellationToken = default)
            {
                return _inner.LoadPairAsync(syncPairId, cancellationToken);
            }

            public virtual IAsyncEnumerable<SyncStateEntry> LoadPairEntriesAsync(
                string syncPairId,
                CancellationToken cancellationToken = default)
            {
                return _inner.LoadPairEntriesAsync(syncPairId, cancellationToken);
            }

            public virtual IAsyncEnumerable<SyncStateEntry> LoadEntriesByPathKeysAsync(
                string syncPairId,
                IEnumerable<string> relativePathKeys,
                CancellationToken cancellationToken = default)
            {
                return _inner.LoadEntriesByPathKeysAsync(syncPairId, relativePathKeys, cancellationToken);
            }

            public virtual Task<DateTime?> GetPairLastSyncedAtUtcAsync(string syncPairId, CancellationToken cancellationToken = default)
            {
                return _inner.GetPairLastSyncedAtUtcAsync(syncPairId, cancellationToken);
            }

            public virtual Task<SyncChangeCursor> GetChangeCursorAsync(string syncPairId, CancellationToken cancellationToken = default)
            {
                return _inner.GetChangeCursorAsync(syncPairId, cancellationToken);
            }

            public virtual Task<SyncStateEntry?> GetAsync(string syncPairId, string relativePath, CancellationToken cancellationToken = default)
            {
                return _inner.GetAsync(syncPairId, relativePath, cancellationToken);
            }

            public virtual Task UpsertAsync(SyncStateEntry entry, CancellationToken cancellationToken = default)
            {
                return _inner.UpsertAsync(entry, cancellationToken);
            }

            public virtual Task SaveChangeCursorAsync(SyncChangeCursor cursor, CancellationToken cancellationToken = default)
            {
                return _inner.SaveChangeCursorAsync(cursor, cancellationToken);
            }

            public virtual Task DeleteAsync(string syncPairId, string relativePath, CancellationToken cancellationToken = default)
            {
                return _inner.DeleteAsync(syncPairId, relativePath, cancellationToken);
            }

            public virtual Task DeletePairAsync(string syncPairId, CancellationToken cancellationToken = default)
            {
                return _inner.DeletePairAsync(syncPairId, cancellationToken);
            }

            public virtual Task ReplacePairAsync(string syncPairId, IReadOnlyCollection<SyncStateEntry> entries, CancellationToken cancellationToken = default)
            {
                return _inner.ReplacePairAsync(syncPairId, entries, cancellationToken);
            }
        }


        private class FailingUpsertStateStore : DelegatingStateStore
        {
            public FailingUpsertStateStore(ISyncStateStore inner)
                : base(inner)
            {
            }

            public override Task UpsertAsync(SyncStateEntry entry, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("State write failed.");
            }
        }


        private class FailingDeleteStateStore : DelegatingStateStore
        {
            public FailingDeleteStateStore(ISyncStateStore inner)
                : base(inner)
            {
            }

            public override Task DeleteAsync(string syncPairId, string relativePath, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("State delete failed.");
            }
        }


        private class StreamingOnlyStateStore : DelegatingStateStore
        {
            public StreamingOnlyStateStore(ISyncStateStore inner)
                : base(inner)
            {
            }

            public int LoadPairEntriesCallCount { get; private set; }

            public int LoadEntriesByPathKeysCallCount { get; private set; }

            public int GetAsyncCallCount { get; private set; }

            public override Task<IReadOnlyList<SyncStateEntry>> LoadPairAsync(
                string syncPairId,
                CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("SyncEngine should use streamed state loading.");
            }

            public override IAsyncEnumerable<SyncStateEntry> LoadPairEntriesAsync(
                string syncPairId,
                CancellationToken cancellationToken = default)
            {
                LoadPairEntriesCallCount++;
                return base.LoadPairEntriesAsync(syncPairId, cancellationToken);
            }

            public override IAsyncEnumerable<SyncStateEntry> LoadEntriesByPathKeysAsync(
                string syncPairId,
                IEnumerable<string> relativePathKeys,
                CancellationToken cancellationToken = default)
            {
                LoadEntriesByPathKeysCallCount++;
                return base.LoadEntriesByPathKeysAsync(syncPairId, relativePathKeys, cancellationToken);
            }

            public override Task<SyncStateEntry?> GetAsync(
                string syncPairId,
                string relativePath,
                CancellationToken cancellationToken = default)
            {
                GetAsyncCallCount++;
                return base.GetAsync(syncPairId, relativePath, cancellationToken);
            }
        }
    }
}
