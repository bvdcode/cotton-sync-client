// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Nodes;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Local;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using System.Text;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsCloudFilesAdapterTests
    {
        private class FakeCloudFilesNativeApi : IWindowsCloudFilesNativeApi
        {
            public List<string>? OperationLog { get; init; }

            public List<string> CallLog { get; } = [];

            public List<WindowsCloudFilesNativeSyncRootRegistration> Registrations { get; } = [];

            public List<WindowsCloudFilesNativePlaceholder> Placeholders { get; } = [];

            public List<WindowsCloudFilesNativePlaceholder> UpdatedPlaceholders { get; } = [];

            public List<ConvertedPlaceholderCall> ConvertedPlaceholders { get; } = [];

            public List<string> InSyncPaths { get; } = [];

            public Dictionary<string, byte[]> PlaceholderIdentities { get; } =
                new(StringComparer.OrdinalIgnoreCase);

            public List<string> IdentityUpdatedPaths { get; } = [];

            public WindowsCloudFilesPlaceholderState InSyncStateAfterSet { get; set; } =
                WindowsCloudFilesPlaceholderState.Placeholder | WindowsCloudFilesPlaceholderState.InSync;

            public List<PinStateCall> PinStates { get; } = [];

            public List<WindowsCloudFilesConnectionRequest> ConnectionRequests { get; } = [];

            public List<string> UnregisteredRoots { get; } = [];

            public List<WindowsCloudFilesConnectionKey> DisconnectedKeys { get; } = [];

            public List<WindowsCloudFilesTransferData> Transfers { get; } = [];

            public List<WindowsCloudFilesAckDehydrateData> Dehydrates { get; } = [];

            public List<string> DehydratedPaths { get; } = [];

            public List<string> HydratedPaths { get; } = [];

            public Action<string>? HydrateAction { get; set; }

            public Exception? RegisterException { get; set; }

            public Exception? UnregisterException { get; set; }

            public Exception? ConvertException { get; set; }

            public Exception? SetInSyncException { get; set; }

            public bool FinalizationSucceeds { get; init; } = true;

            public bool DehydrationContentMatches { get; init; } = true;

            public int UpdateFailuresBeforeSuccess { get; set; }

            public int UpdateCalls { get; private set; }

            public int PinStateFailuresBeforeSuccess { get; set; }

            public int PinStateCalls { get; private set; }

            public void RegisterSyncRoot(WindowsCloudFilesNativeSyncRootRegistration registration)
            {
                OperationLog?.Add("native-register");
                Registrations.Add(registration);
                if (RegisterException is not null)
                {
                    throw RegisterException;
                }
            }

            public void UnregisterSyncRoot(string localRootPath)
            {
                OperationLog?.Add("native-unregister");
                UnregisteredRoots.Add(localRootPath);
                if (UnregisterException is not null)
                {
                    throw UnregisterException;
                }
            }

            public void CreatePlaceholder(WindowsCloudFilesNativePlaceholder placeholder)
            {
                Placeholders.Add(placeholder);
            }

            public List<IReadOnlyList<WindowsCloudFilesNativePlaceholder>> PlaceholderBatches { get; } = [];

            public void CreatePlaceholders(IReadOnlyList<WindowsCloudFilesNativePlaceholder> placeholders)
            {
                PlaceholderBatches.Add(placeholders.ToArray());
                Placeholders.AddRange(placeholders);
            }

            public void UpdatePlaceholder(WindowsCloudFilesNativePlaceholder placeholder)
            {
                CallLog.Add("native-update");
                UpdateCalls++;
                if (UpdateFailuresBeforeSuccess > 0)
                {
                    UpdateFailuresBeforeSuccess--;
                    throw new WindowsCloudFilesNativeException("CreateFile", HResultPathNotFound);
                }

                UpdatedPlaceholders.Add(placeholder);
            }

            public Task<WindowsCloudFilesUploadedFileFinalizationResult> FinalizeUploadedFileAsync(
                WindowsCloudFilesUploadedFileFinalizationRequest request,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ConvertException is not null)
                {
                    throw ConvertException;
                }

                WindowsCloudFilesNativePlaceholder placeholder = request.Placeholder;
                string filePath = Path.GetFullPath(Path.Combine(
                    placeholder.BaseDirectoryPath,
                    placeholder.RelativeFileName));
                FileInfo file = new(filePath);
                if (!FinalizationSucceeds)
                {
                    return Task.FromResult(new WindowsCloudFilesUploadedFileFinalizationResult(
                        IsFinalized: false,
                        file.Length,
                        file.LastWriteTimeUtc));
                }

                InSyncPaths.Add(filePath);
                switch (request.Mode)
                {
                    case WindowsCloudFilesUploadedFileFinalizationMode.ConvertRegularFile:
                        ConvertedPlaceholders.Add(new ConvertedPlaceholderCall(
                            filePath,
                            placeholder.FileIdentity,
                            IsDirectory: false,
                            MarkInSync: true));
                        break;
                    case WindowsCloudFilesUploadedFileFinalizationMode.UpdateExistingPlaceholder:
                        UpdatedPlaceholders.Add(placeholder);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(request));
                }

                return Task.FromResult(new WindowsCloudFilesUploadedFileFinalizationResult(
                    IsFinalized: true,
                    file.Length,
                    file.LastWriteTimeUtc));
            }

            public void ConvertToPlaceholder(string filePath, byte[] fileIdentity, bool isDirectory, bool markInSync)
            {
                CallLog.Add("native-convert");
                if (ConvertException is not null)
                {
                    throw ConvertException;
                }

                ConvertedPlaceholders.Add(new ConvertedPlaceholderCall(filePath, fileIdentity, isDirectory, markInSync));
            }

            public void SetPinState(string filePath, WindowsCloudFilesPinState pinState)
            {
                CallLog.Add("native-set-pin-state");
                PinStateCalls++;
                if (PinStateFailuresBeforeSuccess > 0)
                {
                    PinStateFailuresBeforeSuccess--;
                    throw new WindowsCloudFilesNativeException("CreateFile", HResultPathNotFound);
                }

                PinStates.Add(new PinStateCall(filePath, pinState));
            }

            public void SetInSyncState(string filePath)
            {
                CallLog.Add("native-set-in-sync-state");
                if (SetInSyncException is not null)
                {
                    throw SetInSyncException;
                }

                InSyncPaths.Add(filePath);
            }

            public WindowsCloudFilesPlaceholderState GetPlaceholderState(string filePath)
            {
                return InSyncPaths.Contains(filePath, StringComparer.OrdinalIgnoreCase)
                    ? InSyncStateAfterSet
                    : WindowsCloudFilesPlaceholderState.None;
            }

            public byte[] GetPlaceholderIdentity(string filePath)
            {
                return PlaceholderIdentities[filePath];
            }

            public void UpdatePlaceholderIdentity(string filePath, byte[] placeholderIdentity)
            {
                IdentityUpdatedPaths.Add(filePath);
                PlaceholderIdentities[filePath] = placeholderIdentity;
            }

            public WindowsCloudFilesConnection ConnectSyncRoot(WindowsCloudFilesConnectionRequest request)
            {
                ConnectionRequests.Add(request);
                return new WindowsCloudFilesConnection(
                    request.LocalRootPath,
                    new WindowsCloudFilesConnectionKey(42),
                    DisconnectSyncRoot);
            }

            public void DisconnectSyncRoot(WindowsCloudFilesConnectionKey connectionKey)
            {
                DisconnectedKeys.Add(connectionKey);
            }

            public void TransferData(WindowsCloudFilesTransferData transfer)
            {
                Transfers.Add(transfer);
            }

            public void AcknowledgeDehydrate(WindowsCloudFilesAckDehydrateData dehydrate)
            {
                Dehydrates.Add(dehydrate);
            }

            public void DehydratePlaceholder(string filePath)
            {
                DehydratedPaths.Add(filePath);
            }

            public Task<bool> DehydratePlaceholderIfContentMatchesAsync(
                string filePath,
                string expectedContentHash,
                Action? contentValidated,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!DehydrationContentMatches)
                {
                    return Task.FromResult(false);
                }

                contentValidated?.Invoke();
                DehydratedPaths.Add(filePath);
                return Task.FromResult(true);
            }

            public void HydratePlaceholder(string filePath)
            {
                CallLog.Add("native-hydrate");
                HydratedPaths.Add(filePath);
                HydrateAction?.Invoke(filePath);
            }

            public record PinStateCall(string FilePath, WindowsCloudFilesPinState PinState);

            public record ConvertedPlaceholderCall(
                string FilePath,
                byte[] FileIdentity,
                bool IsDirectory,
                bool MarkInSync);
        }
    }
}
