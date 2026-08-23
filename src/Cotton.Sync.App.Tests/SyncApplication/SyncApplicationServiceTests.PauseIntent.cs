// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncApplication;
using Cotton.Sync.App.SyncPairs;

namespace Cotton.Sync.App.Tests.SyncApplication
{
    public partial class SyncApplicationServiceTests
    {
        [Test]
        public async Task PauseAllAsync_PersistsIntentBeforeWaitingForActiveSyncCancellation()
        {
            TaskCompletionSource<bool> pauseCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            FakeAppPreferencesStore preferences = new();
            FakeSyncSupervisor supervisor = new()
            {
                PauseAllCompletion = pauseCompletion,
            };
            SyncApplicationService service = CreateService(
                new InMemorySyncPairSettingsStore(),
                preferences: preferences,
                supervisor: supervisor);

            Task pauseTask = service.PauseAllAsync();

            Assert.Multiple(() =>
            {
                Assert.That(preferences.Preferences.IsSyncPaused, Is.True);
                Assert.That(preferences.SaveCallCount, Is.EqualTo(1));
                Assert.That(pauseTask.IsCompleted, Is.False);
            });

            pauseCompletion.SetResult(true);
            await pauseTask;
        }

        [Test]
        public async Task ResumeAllAsync_PersistsIntentBeforeWaitingForResumedSync()
        {
            TaskCompletionSource<bool> resumeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            FakeAppPreferencesStore preferences = new();
            preferences.Preferences.IsSyncPaused = true;
            FakeSyncSupervisor supervisor = new()
            {
                ResumeAllCompletion = resumeCompletion,
            };
            SyncApplicationService service = CreateService(
                new InMemorySyncPairSettingsStore(),
                preferences: preferences,
                supervisor: supervisor);

            Task resumeTask = service.ResumeAllAsync();

            Assert.Multiple(() =>
            {
                Assert.That(preferences.Preferences.IsSyncPaused, Is.False);
                Assert.That(preferences.SaveCallCount, Is.EqualTo(1));
                Assert.That(resumeTask.IsCompleted, Is.False);
            });

            resumeCompletion.SetResult(true);
            await resumeTask;
        }
    }
}
