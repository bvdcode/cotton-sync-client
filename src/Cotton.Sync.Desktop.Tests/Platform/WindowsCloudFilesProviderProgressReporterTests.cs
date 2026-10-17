// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Platform;
using NativeApi = Cotton.Sync.Desktop.Tests.Platform.WindowsCloudFilesHydrationCoordinatorTests.FakeCloudFilesNativeApi;
using AppProgress = Cotton.Sync.Desktop.Tests.Platform.WindowsCloudFilesHydrationCoordinatorTests.RecordingProgress<Cotton.Sync.SyncTransferProgress>;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public class WindowsCloudFilesProviderProgressReporterTests
    {
        private static WindowsCloudFilesFetchDataRequest CreateRequest()
        {
            return new(new(1), new(2), new(3), [], 100, 0, 100, 0, 0, null, 0);
        }

        [TestCase(0, false)]
        [TestCase(40, false)]
        [TestCase(100, true)]
        public async Task ProviderProgressReporter_SilentPreparationKeepsRequestAliveWithoutInventingBytes(long bytes, bool completed)
        {
            NativeApi nativeApi = new();
            AppProgress appProgress = new();
            HydrationProgressTimeProvider clock = new();
            WindowsCloudFilesProviderProgressReporter reporter = new(
                nativeApi, CreateRequest(), appProgress, CancellationToken.None, clock);
            TaskCompletionSource finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task preparation = reporter.RunAsync(token => finish.Task.WaitAsync(token));
            try
            {
                if (bytes > 0)
                {
                    reporter.Report(new(SyncTransferDirection.Download, "file.bin", bytes, 100, isCompleted: completed));
                }

                for (int tick = 0; tick < 70; tick++)
                {
                    TaskCompletionSource reported = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    nativeApi.OnKeepAlive = _ => reported.TrySetResult();
                    clock.Advance(TimeSpan.FromSeconds(1));
                    await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }

                Assert.Multiple(() =>
                {
                    Assert.That(preparation.IsCompleted, Is.False);
                    Assert.That(nativeApi.ProviderProgress.Last().CompletedBytes, Is.EqualTo(bytes));
                    Assert.That(nativeApi.KeepAliveTransfers.Count, Is.EqualTo(70));
                    Assert.That(nativeApi.KeepAliveTransfers.Select(static transfer => transfer.Length), Is.All.Zero);
                    Assert.That(nativeApi.KeepAliveTransfers.Select(static transfer => transfer.Offset), Is.All.Zero);
                    Assert.That(nativeApi.KeepAliveTransfers.Select(static transfer => transfer.ConnectionKey), Is.All.EqualTo(new WindowsCloudFilesConnectionKey(1)));
                    Assert.That(nativeApi.KeepAliveTransfers.Select(static transfer => transfer.TransferKey), Is.All.EqualTo(new WindowsCloudFilesTransferKey(2)));
                    Assert.That(nativeApi.KeepAliveTransfers.Select(static transfer => transfer.RequestKey), Is.All.EqualTo(new WindowsCloudFilesRequestKey(3)));
                    Assert.That(nativeApi.Transfers, Is.Empty);
                    Assert.That(appProgress.Values.Count, Is.EqualTo(bytes > 0 ? 1 : 0));
                });
            }
            finally
            {
                finish.TrySetResult();
                await preparation.WaitAsync(TimeSpan.FromSeconds(5));
            }

            int finalCount = nativeApi.KeepAliveTransfers.Count;
            clock.Advance(TimeSpan.FromMinutes(2));
            Assert.Multiple(() =>
            {
                Assert.That(nativeApi.KeepAliveTransfers.Count, Is.EqualTo(finalCount));
                Assert.That(clock.Timer!.IsDisposed, Is.True);
            });
        }

        [Test]
        public async Task ProviderProgressReporter_CancellationStopsPreparationAndTimer()
        {
            NativeApi nativeApi = new();
            HydrationProgressTimeProvider clock = new();
            using CancellationTokenSource cancellation = new();
            WindowsCloudFilesProviderProgressReporter reporter = new(
                nativeApi, CreateRequest(), null, cancellation.Token, clock);
            Task preparation = reporter.RunAsync(token => Task.Delay(Timeout.InfiniteTimeSpan, token));

            await cancellation.CancelAsync();

            Assert.ThrowsAsync<TaskCanceledException>(async () => await preparation.WaitAsync(TimeSpan.FromSeconds(5)));
            clock.Advance(TimeSpan.FromMinutes(2));
            Assert.Multiple(() =>
            {
                Assert.That(nativeApi.KeepAliveTransfers, Is.Empty);
                Assert.That(clock.Timer!.IsDisposed, Is.True);
            });
        }

        [Test]
        public void ProviderProgressReporter_NativeHeartbeatRejectedDuringCancellationRemainsCancellation()
        {
            NativeApi nativeApi = new();
            HydrationProgressTimeProvider clock = new();
            using CancellationTokenSource cancellation = new();
            WindowsCloudFilesProviderProgressReporter reporter = new(
                nativeApi, CreateRequest(), null, cancellation.Token, clock);
            Task preparation = reporter.RunAsync(token => Task.Delay(Timeout.InfiniteTimeSpan, token));
            nativeApi.OnKeepAlive = _ =>
            {
                cancellation.Cancel();
                throw new IOException("Native request is no longer active.");
            };

            clock.Advance(TimeSpan.FromSeconds(1));

            Assert.CatchAsync<OperationCanceledException>(async () => await preparation.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.That(clock.Timer!.IsDisposed, Is.True);
        }

        [Test]
        public void ProviderProgressReporter_PreparationFailureStopsTimerAndPreservesError()
        {
            NativeApi nativeApi = new();
            HydrationProgressTimeProvider clock = new();
            WindowsCloudFilesProviderProgressReporter reporter = new(
                nativeApi, CreateRequest(), null, CancellationToken.None, clock);
            IOException expected = new("Content preparation failed.");

            IOException? actual = Assert.ThrowsAsync<IOException>(
                () => reporter.RunAsync(_ => Task.FromException(expected)));

            clock.Advance(TimeSpan.FromMinutes(2));
            Assert.Multiple(() =>
            {
                Assert.That(actual, Is.SameAs(expected));
                Assert.That(nativeApi.KeepAliveTransfers, Is.Empty);
                Assert.That(clock.Timer!.IsDisposed, Is.True);
            });
        }

        [Test]
        public void ProviderProgressReporter_NativeHeartbeatFailureCancelsPreparationAndPreservesError()
        {
            NativeApi nativeApi = new();
            HydrationProgressTimeProvider clock = new();
            WindowsCloudFilesProviderProgressReporter reporter = new(
                nativeApi, CreateRequest(), null, CancellationToken.None, clock);
            bool preparationStopped = false;
            Task preparation = reporter.RunAsync(async token =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                finally
                {
                    preparationStopped = true;
                }
            });
            IOException expected = new("Native request renewal failed.");
            nativeApi.OnKeepAlive = _ => throw expected;

            clock.Advance(TimeSpan.FromSeconds(1));

            IOException? actual = Assert.ThrowsAsync<IOException>(async () => await preparation.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Multiple(() =>
            {
                Assert.That(actual, Is.SameAs(expected));
                Assert.That(preparationStopped, Is.True);
                Assert.That(clock.Timer!.IsDisposed, Is.True);
            });
        }
    }
}
