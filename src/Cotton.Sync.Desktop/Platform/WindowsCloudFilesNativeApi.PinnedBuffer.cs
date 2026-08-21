// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Runtime.InteropServices;

namespace Cotton.Sync.Desktop.Platform
{
    internal partial class WindowsCloudFilesNativeApi
    {
        private readonly struct PinnedBuffer : IDisposable
        {
            private readonly GCHandle _handle;

            private PinnedBuffer(byte[]? buffer)
            {
                if (buffer is not { Length: > 0 })
                {
                    Pointer = IntPtr.Zero;
                    Length = 0;
                    _handle = default;
                    return;
                }

                _handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                Pointer = _handle.AddrOfPinnedObject();
                Length = (uint)buffer.Length;
            }

            public IntPtr Pointer { get; }

            public uint Length { get; }

            public static PinnedBuffer Pin(byte[]? buffer)
            {
                return new PinnedBuffer(buffer);
            }

            public void Dispose()
            {
                if (_handle.IsAllocated)
                {
                    _handle.Free();
                }
            }
        }
    }
}
