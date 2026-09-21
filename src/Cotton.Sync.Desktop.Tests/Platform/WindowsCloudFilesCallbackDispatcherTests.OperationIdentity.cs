// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Platform;
using System.Collections.Concurrent;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsCloudFilesCallbackDispatcherTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task QueueFetchData_SameRequestKeyInDifferentContexts_AcceptsBoth(bool differentConnection)
        {
            BlockingCallbackHandler handler = new();
            ConcurrentQueue<WindowsCloudFilesTransferData> failures = new();
            using WindowsCloudFilesCallbackDispatcher dispatcher = new(handler, failures.Enqueue);
            WindowsCloudFilesFetchDataRequest first = CreateRequest(0);
            WindowsCloudFilesFetchDataRequest second = differentConnection
                ? first with { ConnectionKey = new(2) }
                : first with { TransferKey = new(200) };

            Assert.That(dispatcher.QueueFetchData(first), Is.True);
            await handler.WaitForStartedCountAsync(1);
            Assert.That(dispatcher.QueueFetchData(second), Is.True);
            await handler.WaitForStartedCountAsync(2);

            dispatcher.CancelFetchData(new(first.ConnectionKey, first.TransferKey, first.RequestKey, 0, 1024));
            await WaitUntilAsync(() => dispatcher.PendingFetchCount == 1);
            Assert.Multiple(() =>
            {
                Assert.That(handler.CanceledRequestKeys, Has.Count.EqualTo(1));
                Assert.That(failures, Is.Empty);
            });
            dispatcher.CancelFetchData(new(second.ConnectionKey, second.TransferKey, second.RequestKey, 0, 1024));
            await WaitUntilAsync(() => dispatcher.PendingFetchCount == 0);
            Assert.That(handler.CanceledRequestKeys, Has.Count.EqualTo(2));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task QueueDehydrate_SameRequestKeyInDifferentContexts_AcceptsBoth(bool differentConnection)
        {
            BlockingCallbackHandler handler = new();
            ConcurrentQueue<WindowsCloudFilesAckDehydrateData> failures = new();
            using WindowsCloudFilesCallbackDispatcher dispatcher = new(handler, _ => { }, failures.Enqueue);
            WindowsCloudFilesDehydrateRequest first = CreateDehydrateRequest(0);
            WindowsCloudFilesDehydrateRequest second = differentConnection
                ? first with { ConnectionKey = new(2) }
                : first with { TransferKey = new(200) };

            Assert.That(dispatcher.QueueDehydrate(first), Is.True);
            await handler.WaitForDehydrateStartedCountAsync(1);
            Assert.That(dispatcher.QueueDehydrate(second), Is.True);
            await handler.WaitForDehydrateStartedCountAsync(2);

            Assert.Multiple(() =>
            {
                Assert.That(dispatcher.PendingDehydrateCount, Is.EqualTo(2));
                Assert.That(failures, Is.Empty);
            });
        }

        [Test]
        public async Task CancelFetchData_UnknownFileWithSameRequestKey_DoesNotCancelPendingFile()
        {
            BlockingCallbackHandler handler = new();
            using WindowsCloudFilesCallbackDispatcher dispatcher = new(handler, _ => { });
            WindowsCloudFilesFetchDataRequest first = CreateRequest(0);
            Assert.That(dispatcher.QueueFetchData(first), Is.True);
            await handler.WaitForStartedCountAsync(1);

            dispatcher.CancelFetchData(new(first.ConnectionKey, new(200), first.RequestKey, 0, 1024));

            Assert.Multiple(() =>
            {
                Assert.That(dispatcher.PendingFetchCount, Is.EqualTo(1));
                Assert.That(handler.CanceledRequestKeys, Is.Empty);
            });
            dispatcher.CancelFetchData(new(first.ConnectionKey, first.TransferKey, first.RequestKey, 0, 1024));
            await WaitUntilAsync(() => dispatcher.PendingFetchCount == 0);
        }
    }
}
