// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsVirtualFilesUploadFinalizationPairWorkTests
    {
        private partial class RecordingLocalChangeSuppression
        {
            public void SuppressProviderDirectoryWrite(
                Guid syncPairId,
                string localRootPath,
                string relativePath)
            {
                SuppressedWrites.Add(new SuppressedWrite(syncPairId, localRootPath, relativePath));
            }
        }
    }
}
