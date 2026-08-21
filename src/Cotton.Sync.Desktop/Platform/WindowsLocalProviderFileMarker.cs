// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.Local;
using Cotton.Sync.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace Cotton.Sync.Desktop.Platform
{
    internal class WindowsLocalProviderFileMarker : ILocalProviderFileMarker
    {
        private const int MarkerVersion = 1;
        private static readonly char[] DirectorySeparators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

        private readonly string _markerRootPath;
        private readonly ILogger<WindowsLocalProviderFileMarker> _logger;

        public WindowsLocalProviderFileMarker(
            string markerRootPath,
            ILogger<WindowsLocalProviderFileMarker>? logger = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(markerRootPath);
            _markerRootPath = Path.GetFullPath(markerRootPath);
            _logger = logger ?? NullLogger<WindowsLocalProviderFileMarker>.Instance;
        }

        public async Task MarkAsync(
            Guid syncPairId,
            string localRootPath,
            string relativePath,
            string contentHash,
            long sizeBytes,
            CancellationToken cancellationToken = default)
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            ValidateArguments(syncPairId, localRootPath, relativePath, contentHash, sizeBytes);
            string normalizedPath = SyncPath.Normalize(relativePath);
            string fullPath = ResolveInsideRoot(localRootPath, normalizedPath);
            string fileIdentity = ReadFileIdentity(fullPath);
            MarkerPayload payload = new()
            {
                Version = MarkerVersion,
                SyncPairId = syncPairId,
                LocalRootPath = NormalizePath(localRootPath),
                RelativePath = normalizedPath,
                ContentHash = contentHash,
                SizeBytes = sizeBytes,
                FileIdentity = fileIdentity,
            };

            string markerPath = GetMarkerPath(syncPairId, normalizedPath);
            string markerDirectory = Path.GetDirectoryName(markerPath)!;
            Directory.CreateDirectory(markerDirectory);
            string temporaryPath = markerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload);
                await File.WriteAllBytesAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
                File.Move(temporaryPath, markerPath, overwrite: true);
            }
            finally
            {
                File.Delete(temporaryPath);
            }
        }

        public async Task<bool> IsUnchangedAsync(
            Guid syncPairId,
            string localRootPath,
            LocalFileSnapshot localFile,
            CancellationToken cancellationToken = default)
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            ArgumentNullException.ThrowIfNull(localFile);
            string normalizedPath = SyncPath.Normalize(localFile.RelativePath);
            string markerPath = GetMarkerPath(syncPairId, normalizedPath);
            if (!File.Exists(markerPath))
            {
                return false;
            }

            try
            {
                MarkerPayload? marker = await ReadMarkerAsync(markerPath, cancellationToken).ConfigureAwait(false);
                if (!MarkerMatchesFile(marker, syncPairId, localRootPath, normalizedPath, localFile))
                {
                    DeleteMarker(markerPath);
                    return false;
                }

                string contentHash = await ComputeContentHashAsync(localFile.FullPath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(marker!.ContentHash, contentHash, StringComparison.OrdinalIgnoreCase))
                {
                    DeleteMarker(markerPath);
                    return false;
                }

                return true;
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or Win32Exception)
            {
                _logger.LogWarning(
                    exception,
                    "Failed to inspect provider-created local file marker for {RelativePath}; treating the path as a user change.",
                    normalizedPath);
                return false;
            }
        }

        private static async Task<MarkerPayload?> ReadMarkerAsync(
            string markerPath,
            CancellationToken cancellationToken)
        {
            byte[] json = await File.ReadAllBytesAsync(markerPath, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<MarkerPayload>(json);
        }

        private static bool MarkerMatchesFile(
            MarkerPayload? marker,
            Guid syncPairId,
            string localRootPath,
            string normalizedPath,
            LocalFileSnapshot localFile)
        {
            return marker is not null
                && marker.Version == MarkerVersion
                && marker.SyncPairId == syncPairId
                && string.Equals(marker.LocalRootPath, NormalizePath(localRootPath), StringComparison.OrdinalIgnoreCase)
                && string.Equals(marker.RelativePath, normalizedPath, StringComparison.OrdinalIgnoreCase)
                && marker.SizeBytes == localFile.SizeBytes
                && string.Equals(marker.FileIdentity, ReadFileIdentity(localFile.FullPath), StringComparison.Ordinal);
        }

        private static void ValidateArguments(
            Guid syncPairId,
            string localRootPath,
            string relativePath,
            string contentHash,
            long sizeBytes)
        {
            if (syncPairId == Guid.Empty)
            {
                throw new ArgumentException("Sync pair id cannot be empty.", nameof(syncPairId));
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(localRootPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
            ArgumentOutOfRangeException.ThrowIfNegative(sizeBytes);
        }

        private string GetMarkerPath(Guid syncPairId, string relativePath)
        {
            string pathKey = SyncPath.ToKey(relativePath);
            string markerName = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(pathKey))) + ".json";
            return Path.Combine(_markerRootPath, syncPairId.ToString("D"), markerName);
        }

        private void DeleteMarker(string markerPath)
        {
            try
            {
                File.Delete(markerPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "Failed to remove stale provider-created local file marker {MarkerPath}.", markerPath);
            }
        }

        private static async Task<string> ComputeContentHashAsync(
            string fullPath,
            CancellationToken cancellationToken)
        {
            await using FileStream stream = new(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexStringLower(hash);
        }

        private static string ReadFileIdentity(string fullPath)
        {
            using SafeFileHandle handle = File.OpenHandle(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information))
            {
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, "GetFileInformationByHandle failed for " + fullPath + ".");
            }

            return information.VolumeSerialNumber.ToString("x8", System.Globalization.CultureInfo.InvariantCulture)
                + ":"
                + information.FileIndexHigh.ToString("x8", System.Globalization.CultureInfo.InvariantCulture)
                + information.FileIndexLow.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string ResolveInsideRoot(string localRootPath, string relativePath)
        {
            string rootPath = NormalizePath(localRootPath);
            string fullPath = NormalizePath(Path.Combine(
                rootPath,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            string rootPrefix = rootPath.TrimEnd(DirectorySeparators) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Provider-created file marker path must stay inside the local sync root.", nameof(relativePath));
            }

            return fullPath;
        }

        private static string NormalizePath(string path)
        {
            string normalized = Path.GetFullPath(path);
            string? root = Path.GetPathRoot(normalized);
            if (!string.IsNullOrWhiteSpace(root)
                && string.Equals(
                    normalized.TrimEnd(DirectorySeparators),
                    root.TrimEnd(DirectorySeparators),
                    StringComparison.OrdinalIgnoreCase))
            {
                return root;
            }

            return normalized.TrimEnd(DirectorySeparators);
        }

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle hFile,
            out ByHandleFileInformation lpFileInformation);

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        private class MarkerPayload
        {
            public int Version { get; set; }

            public Guid SyncPairId { get; set; }

            public string LocalRootPath { get; set; } = string.Empty;

            public string RelativePath { get; set; } = string.Empty;

            public string ContentHash { get; set; } = string.Empty;

            public long SizeBytes { get; set; }

            public string FileIdentity { get; set; } = string.Empty;
        }
    }
}
