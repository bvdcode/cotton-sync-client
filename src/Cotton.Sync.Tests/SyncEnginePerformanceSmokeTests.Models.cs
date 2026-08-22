// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Cotton.Files;
using Cotton.Nodes;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;

namespace Cotton.Sync.Tests
{
    public partial class SyncEnginePerformanceSmokeTests
    {
        private record UploadCall(
            Guid RootNodeId,
            string RelativePath,
            LocalFileSnapshot LocalFile,
            NodeFileManifestDto? ExistingRemoteFile,
            NodeFileManifestDto ReturnedFile);

        private record MemorySample(long ManagedHeapBytes, long WorkingSetBytes);

        private record VirtualPlaceholderPopulationSmokeResult(
            TimeSpan Elapsed,
            long ManagedHeapDeltaBytes,
            int PlaceholderCount,
            string FirstPlaceholderPath,
            string LastPlaceholderPath,
            int RunProgressCount,
            int CooperativeYieldCount,
            int RetainedActivityCount,
            bool IsActivityListTruncated);

        private record VirtualPlaceholderRepeatPassSmokeResult(
            TimeSpan Elapsed,
            int LocalFullScanCalls,
            int PlaceholderWrites,
            int StateEntriesLoaded,
            int StreamingCrawlCalls,
            int RetainedActivityCount,
            int TotalActivityCount,
            int RunProgressCount);

        private class RecordingProgress<T> : IProgress<T>
        {
            public List<T> Values { get; } = [];

            public void Report(T value)
            {
                Values.Add(value);
            }
        }
    }
}
