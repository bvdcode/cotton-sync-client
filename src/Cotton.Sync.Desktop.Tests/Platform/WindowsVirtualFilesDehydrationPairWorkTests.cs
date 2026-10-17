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
        private static WindowsVirtualFilesDehydrationPairWork CreateWork(
            ISyncPairWork inner,
            ISyncStateStore stateStore,
            FakeCloudFilesAdapter cloudFiles,
            ILocalFileContentHasher? contentHasher = null,
            IWindowsCloudFilesDiagnostics? diagnostics = null,
            Func<string, WindowsVirtualFileDiskState?>? readDiskState = null,
            ILocalChangeSuppression? localChangeSuppression = null,
            IAppRunProgressPublisher? runProgressPublisher = null)
        {
            return new(inner, stateStore, cloudFiles, cloudFiles, contentHasher, diagnostics,
                readDiskState, localChangeSuppression, runProgressPublisher);
        }

        private const int FileAttributePinned = 0x00080000;
        private const int FileAttributeUnpinned = 0x00100000;
        private const int FileAttributeRecallOnDataAccess = 0x00400000;





    }
}
