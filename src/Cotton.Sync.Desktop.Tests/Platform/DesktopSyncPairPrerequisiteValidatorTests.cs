// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    [Platform(Include = "Win")]
    public class DesktopSyncPairPrerequisiteValidatorTests
    {
        [Test]
        public async Task ValidateAsync_RejectsDriveRootForWindowsVirtualFilesBeforeInnerValidation()
        {
            RecordingPrerequisiteValidator inner = new();
            DesktopSyncPairPrerequisiteValidator validator = new(inner, CreateSafetyPolicy());
            SyncPairSettings pair = CreatePair(@"C:\", SyncPairMode.WindowsVirtualFiles);

            IReadOnlyList<SyncPairValidationError> errors = await validator.ValidateAsync(pair);

            Assert.Multiple(() =>
            {
                Assert.That(inner.CallCount, Is.Zero);
                Assert.That(errors, Has.Count.EqualTo(1));
                Assert.That(errors[0].Issue, Is.EqualTo(SyncPairValidationIssue.LocalRootUnavailable));
                Assert.That(errors[0].SyncPairId, Is.EqualTo(pair.Id));
                Assert.That(errors[0].Message, Is.EqualTo("Virtual-files sync root cannot be a drive or share root."));
            });
        }

        [Test]
        public async Task ValidateAsync_AllowsDriveRootForFullMirror()
        {
            RecordingPrerequisiteValidator inner = new();
            DesktopSyncPairPrerequisiteValidator validator = new(inner, CreateSafetyPolicy());
            SyncPairSettings pair = CreatePair(@"C:\", SyncPairMode.FullMirror);

            IReadOnlyList<SyncPairValidationError> errors = await validator.ValidateAsync(pair);

            Assert.Multiple(() =>
            {
                Assert.That(inner.CallCount, Is.EqualTo(1));
                Assert.That(errors, Is.Empty);
            });
        }

        [Test]
        public async Task ValidateAsync_DelegatesSafeWindowsVirtualFilesRoot()
        {
            RecordingPrerequisiteValidator inner = new();
            DesktopSyncPairPrerequisiteValidator validator = new(inner, CreateSafetyPolicy());
            SyncPairSettings pair = CreatePair(@"S:\CottonSyncQa\pair", SyncPairMode.WindowsVirtualFiles);

            IReadOnlyList<SyncPairValidationError> errors = await validator.ValidateAsync(pair);

            Assert.Multiple(() =>
            {
                Assert.That(inner.CallCount, Is.EqualTo(1));
                Assert.That(errors, Is.Empty);
            });
        }

        private static WindowsVirtualFilesRootSafetyPolicy CreateSafetyPolicy()
        {
            return new WindowsVirtualFilesRootSafetyPolicy(
                _ => string.Empty,
                () => @"C:\Temp\CottonSyncQa");
        }

        private static SyncPairSettings CreatePair(string localRootPath, SyncPairMode mode)
        {
            return new SyncPairSettings
            {
                Id = Guid.NewGuid(),
                DisplayName = "Pair",
                LocalRootPath = localRootPath,
                RemoteRootNodeId = Guid.NewGuid(),
                RemoteDisplayPath = "/Pair",
                IsEnabled = true,
                Mode = mode,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            };
        }

        private class RecordingPrerequisiteValidator : ISyncPairPrerequisiteValidator
        {
            public int CallCount { get; private set; }

            public Task<IReadOnlyList<SyncPairValidationError>> ValidateAsync(
                SyncPairSettings syncPair,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CallCount++;
                IReadOnlyList<SyncPairValidationError> errors = [];
                return Task.FromResult(errors);
            }
        }
    }
}
