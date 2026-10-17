// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.Desktop.Platform
{
    internal class WindowsCloudFilesPreparedContent(FileStream stream, Action<FileStream> release) : IDisposable
    {
        private FileStream? _stream = stream;

        public void Dispose()
        {
            FileStream? current = Interlocked.Exchange(ref _stream, null);
            if (current is not null)
            {
                release(current);
            }
        }
    }
}
