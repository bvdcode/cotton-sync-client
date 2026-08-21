// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Runtime.InteropServices;

namespace Cotton.Sync.Desktop.Platform
{
    internal partial class WindowsCloudFilesNativeApi
    {
        public void RegisterSyncRoot(WindowsCloudFilesNativeSyncRootRegistration registration)
        {
            ArgumentNullException.ThrowIfNull(registration);
            Directory.CreateDirectory(registration.LocalRootPath);

            PinnedBuffer syncRootIdentity = PinnedBuffer.Pin(registration.SyncRootIdentity);
            try
            {
                var nativeRegistration = new CfSyncRegistration
                {
                    StructSize = (uint)Marshal.SizeOf<CfSyncRegistration>(),
                    ProviderName = registration.ProviderName,
                    ProviderVersion = registration.ProviderVersion,
                    SyncRootIdentity = syncRootIdentity.Pointer,
                    SyncRootIdentityLength = syncRootIdentity.Length,
                    FileIdentity = IntPtr.Zero,
                    FileIdentityLength = 0,
                    ProviderId = registration.ProviderId,
                };
                CfSyncPolicies policies = CfSyncPolicies.CreateDefault();
                int result = CfRegisterSyncRoot(
                    WindowsNativePath.ToWin32FilePath(registration.LocalRootPath),
                    ref nativeRegistration,
                    ref policies,
                    CfRegisterFlags.Update | CfRegisterFlags.MarkInSyncOnRoot);
                ThrowIfFailed(result, nameof(CfRegisterSyncRoot));
            }
            finally
            {
                syncRootIdentity.Dispose();
            }
        }

        public void UnregisterSyncRoot(string localRootPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(localRootPath);
            int result = CfUnregisterSyncRoot(WindowsNativePath.ToWin32FilePath(localRootPath));
            ThrowIfFailed(result, nameof(CfUnregisterSyncRoot));
        }

        public void CreatePlaceholder(WindowsCloudFilesNativePlaceholder placeholder)
        {
            ArgumentNullException.ThrowIfNull(placeholder);
            CreatePlaceholders([placeholder]);
        }

        public void CreatePlaceholders(IReadOnlyList<WindowsCloudFilesNativePlaceholder> placeholders)
        {
            ArgumentNullException.ThrowIfNull(placeholders);
            foreach (IGrouping<string, WindowsCloudFilesNativePlaceholder> group in placeholders
                .GroupBy(static placeholder => placeholder.BaseDirectoryPath, StringComparer.OrdinalIgnoreCase))
            {
                CreatePlaceholdersInDirectory(group.Key, [.. group]);
            }
        }

        private static void CreatePlaceholdersInDirectory(
            string baseDirectoryPath,
            IReadOnlyList<WindowsCloudFilesNativePlaceholder> placeholders)
        {
            Directory.CreateDirectory(baseDirectoryPath);
            var pinnedIdentities = new PinnedBuffer[placeholders.Count];
            try
            {
                var nativePlaceholders = new CfPlaceholderCreateInfo[placeholders.Count];
                for (int index = 0; index < placeholders.Count; index++)
                {
                    WindowsCloudFilesNativePlaceholder placeholder = placeholders[index];
                    pinnedIdentities[index] = PinnedBuffer.Pin(placeholder.FileIdentity);
                    nativePlaceholders[index] = new CfPlaceholderCreateInfo
                    {
                        RelativeFileName = placeholder.RelativeFileName,
                        FsMetadata = placeholder.IsDirectory
                            ? CfFsMetadata.CreateDirectory(placeholder.CreatedAtUtc, placeholder.UpdatedAtUtc)
                            : CfFsMetadata.CreateFile(
                                placeholder.FileSizeBytes,
                                placeholder.CreatedAtUtc,
                                placeholder.UpdatedAtUtc),
                        FileIdentity = pinnedIdentities[index].Pointer,
                        FileIdentityLength = pinnedIdentities[index].Length,
                        Flags = CreatePlaceholderCreateFlags(placeholder.IsDirectory),
                        Result = Succeeded,
                        CreateUsn = 0,
                    };
                }

                int result = CfCreatePlaceholders(
                    WindowsNativePath.ToWin32FilePath(baseDirectoryPath),
                    nativePlaceholders,
                    (uint)nativePlaceholders.Length,
                    CfCreateFlags.StopOnError,
                    out uint entriesProcessed);
                ThrowIfFailed(result, nameof(CfCreatePlaceholders));

                uint processed = Math.Min(entriesProcessed, (uint)nativePlaceholders.Length);
                for (int index = 0; index < processed; index++)
                {
                    ThrowIfFailed(nativePlaceholders[index].Result, nameof(CfCreatePlaceholders));
                }

                if (entriesProcessed != nativePlaceholders.Length)
                {
                    throw new WindowsCloudFilesNativeException(nameof(CfCreatePlaceholders), unchecked((int)0x80004005));
                }
            }
            finally
            {
                foreach (PinnedBuffer pinnedIdentity in pinnedIdentities)
                {
                    pinnedIdentity.Dispose();
                }
            }
        }

        public void UpdatePlaceholder(WindowsCloudFilesNativePlaceholder placeholder)
        {
            ArgumentNullException.ThrowIfNull(placeholder);
            string filePath = Path.Combine(placeholder.BaseDirectoryPath, placeholder.RelativeFileName);
            int openResult = CfOpenFileWithOplock(
                WindowsNativePath.ToWin32FilePath(filePath),
                CfOpenFileFlags.Exclusive | CfOpenFileFlags.WriteAccess,
                out IntPtr protectedHandle);
            ThrowIfFailed(openResult, nameof(CfOpenFileWithOplock));
            try
            {
                PinnedBuffer fileIdentity = PinnedBuffer.Pin(placeholder.FileIdentity);
                try
                {
                    CfFsMetadata metadata = placeholder.IsDirectory
                        ? CfFsMetadata.CreateDirectory(placeholder.CreatedAtUtc, placeholder.UpdatedAtUtc)
                        : CfFsMetadata.CreateFile(
                            placeholder.FileSizeBytes,
                            placeholder.CreatedAtUtc,
                            placeholder.UpdatedAtUtc);
                    int result = CfUpdatePlaceholder(
                        protectedHandle,
                        ref metadata,
                        fileIdentity.Pointer,
                        fileIdentity.Length,
                        IntPtr.Zero,
                        0,
                        CreateUpdateFlags(placeholder.IsDirectory),
                        IntPtr.Zero,
                        IntPtr.Zero);
                    ThrowIfFailed(result, nameof(CfUpdatePlaceholder));
                }
                finally
                {
                    fileIdentity.Dispose();
                }
            }
            finally
            {
                CfCloseHandle(protectedHandle);
            }
        }
    }
}
