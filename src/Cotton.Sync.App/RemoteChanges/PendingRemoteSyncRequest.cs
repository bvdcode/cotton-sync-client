// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

namespace Cotton.Sync.App.RemoteChanges
{
    internal class PendingRemoteSyncRequest
    {
        public PendingRemoteSyncRequest(
            CancellationTokenSource cancellation,
            string methodName,
            DateTimeOffset createdAt)
        {
            Cancellation = cancellation;
            MethodName = methodName;
            CreatedAt = createdAt;
        }

        public CancellationTokenSource Cancellation { get; }

        public DateTimeOffset CreatedAt { get; }

        public string MethodName { get; private set; }

        public int ChangeVersion { get; private set; }

        public Task? Runner { get; set; }

        public void RecordChange(string methodName)
        {
            MethodName = methodName;
            ChangeVersion++;
        }
    }
}
