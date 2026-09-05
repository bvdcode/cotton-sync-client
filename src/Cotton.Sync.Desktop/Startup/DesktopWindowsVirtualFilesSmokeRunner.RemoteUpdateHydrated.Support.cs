// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using System.Security.Cryptography;

namespace Cotton.Sync.Desktop.Startup
{
    internal static partial class DesktopWindowsVirtualFilesSmokeRunner
    {
        private static RemoteFilePlaceholderRequest CreateHydratedUpdateRequest(
            WindowsVirtualFilesSmokeContext context,
            byte[] content)
        {
            string hash = Convert.ToHexStringLower(SHA256.HashData(content));
            RemoteFilePlaceholderRequest request = CreatePlaceholderRequest(context.SyncPair, HydratedUpdateRelativePath, content.LongLength, hash);
            request.RemoteFile.FileManifestId = Guid.CreateVersion7();
            request.RemoteFile.ETag = "hydrated-update-" + hash;
            return request;
        }

        private static async Task<int> VerifyHydratedUpdateContentAsync(
            WindowsVirtualFilesSmokeContext context,
            byte[] expectedContent,
            string label)
        {
            byte[] actual = await File.ReadAllBytesAsync(
                Path.Combine(context.SyncPair.LocalRootPath, HydratedUpdateRelativePath), context.CancellationToken).ConfigureAwait(false);
            return await WritePassFailAsync(context.Output, actual.SequenceEqual(expectedContent), label,
                " sha256=" + Convert.ToHexStringLower(SHA256.HashData(actual))).ConfigureAwait(false);
        }

        private static async Task<int> VerifyHydratedUpdateStateAsync(
            WindowsVirtualFilesSmokeContext context,
            ISyncStateStore stateStore,
            NodeFileManifestDto expectedRemote,
            string stage)
        {
            string label = "Hydrated remote-update provider state and identity are current " + stage + ".";
            try
            {
                WindowsCloudFilesPlaceholderState nativeState = context.CloudFiles.GetPlaceholderState(context.SyncPair, HydratedUpdateRelativePath);
                WindowsCloudFilesPlaceholderIdentity identity = WindowsCloudFilesPlaceholderIdentity.Parse(
                    context.CloudFiles.GetPlaceholderIdentity(context.SyncPair, HydratedUpdateRelativePath));
                FileAttributes attributes = File.GetAttributes(Path.Combine(context.SyncPair.LocalRootPath, HydratedUpdateRelativePath));
                SyncStateEntry? baseline = await stateStore.GetAsync(context.SyncPair.Id.ToString("D"), HydratedUpdateRelativePath,
                    context.CancellationToken).ConfigureAwait(false);
                bool passed = nativeState != WindowsCloudFilesPlaceholderState.Invalid
                    && nativeState.HasFlag(WindowsCloudFilesPlaceholderState.Placeholder)
                    && nativeState.HasFlag(WindowsCloudFilesPlaceholderState.InSync)
                    && !nativeState.HasFlag(WindowsCloudFilesPlaceholderState.Partial)
                    && !nativeState.HasFlag(WindowsCloudFilesPlaceholderState.PartiallyOnDisk)
                    && !HasRecallOnDataAccess(attributes)
                    && identity.SyncPairId == context.SyncPair.Id
                    && identity.RelativePath == HydratedUpdateRelativePath
                    && identity.NodeFileId == expectedRemote.Id
                    && identity.FileManifestId == expectedRemote.FileManifestId
                    && identity.ContentHash == expectedRemote.ContentHash
                    && baseline is not null
                    && baseline.RemoteFileManifestId == expectedRemote.FileManifestId
                    && baseline.RemoteContentHash == expectedRemote.ContentHash
                    && baseline.LocalContentHash == expectedRemote.ContentHash;
                return await WritePassFailAsync(context.Output, passed, label,
                    " state=" + nativeState + ", attributes=" + FormatAttributes(attributes)
                    + ", identityHash=" + identity.ContentHash + ", baseline=" + FormatStateSummary(baseline)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return await WritePassFailAsync(context.Output, false, label, " " + CleanSingleLine(exception.Message)).ConfigureAwait(false);
            }
        }

        private class HydratedUpdateRemoteFileSynchronizer : RecordingUploadRemoteFileSynchronizer
        {
            public byte[] Content { get; set; } = [];

            public Func<CancellationToken, Task>? BeforeDownloadAsync { get; set; }

            public int DownloadCount { get; private set; }

            public override async Task DownloadFileAsync(Guid nodeFileId, Stream destination, CancellationToken cancellationToken = default)
            {
                DownloadCount++;
                await destination.WriteAsync(Content, cancellationToken).ConfigureAwait(false);
                if (BeforeDownloadAsync is not null)
                {
                    await BeforeDownloadAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }
}
