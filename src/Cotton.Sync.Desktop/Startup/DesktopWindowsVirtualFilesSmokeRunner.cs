// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.VirtualFiles;
using System.Security.Cryptography;
using System.Text;

namespace Cotton.Sync.Desktop.Startup
{
    internal static class DesktopWindowsVirtualFilesSmokeRunner
    {
        private const string DefaultSmokeRoot = @"S:\CottonSyncVfsQa\root";
        private const string AllowedSmokeRoot = @"S:\CottonSyncVfsQa";
        private const string RelativePlaceholderPath = "remote-only-smoke.txt";
        private const string SmokeContentText = "Cotton Sync Windows virtual files smoke content\n";

        public static async Task<int> RunAsync(
            DesktopAppPaths paths,
            DesktopStartupOptions startupOptions,
            TextWriter output,
            IWindowsCloudFilesAdapter? cloudFilesAdapter = null,
            Func<string, CancellationToken, Task<string>>? readAllTextAsync = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(paths);
            ArgumentNullException.ThrowIfNull(startupOptions);
            ArgumentNullException.ThrowIfNull(output);

            await output.WriteLineAsync("Cotton Sync Desktop Windows virtual files smoke").ConfigureAwait(false);
            await output.WriteLineAsync("Allowed destructive root: " + AllowedSmokeRoot + @"\...").ConfigureAwait(false);

            if (!OperatingSystem.IsWindows())
            {
                await output.WriteLineAsync(FormatCheck(false, "Windows Cloud Files API is only available on Windows."))
                    .ConfigureAwait(false);
                await output.WriteLineAsync("Result: failed").ConfigureAwait(false);
                return 2;
            }

            string rootPath = ResolveSmokeRoot(startupOptions.LocalRoot);
            string? rootError = ValidateSmokeRoot(rootPath);
            if (rootError is not null)
            {
                await output.WriteLineAsync(FormatCheck(false, rootError)).ConfigureAwait(false);
                await output.WriteLineAsync("Result: failed").ConfigureAwait(false);
                return 2;
            }

            var diagnostics = new WindowsCloudFilesDiagnostics();
            string phase = (startupOptions.WindowsVirtualFilesSmokePhase ?? string.Empty).Trim().ToLowerInvariant();
            bool leaveRegistered = string.Equals(phase, "leave-registered", StringComparison.Ordinal);
            bool reconnectExisting = string.Equals(phase, "reconnect-existing", StringComparison.Ordinal);
            if (!string.IsNullOrEmpty(phase) && !leaveRegistered && !reconnectExisting)
            {
                await output.WriteLineAsync(FormatCheck(false, "Unsupported Windows virtual-files smoke phase: " + phase))
                    .ConfigureAwait(false);
                await output.WriteLineAsync("Result: failed").ConfigureAwait(false);
                return 2;
            }

            IWindowsCloudFilesNativeApi? nativeApi = cloudFilesAdapter is null
                ? new WindowsCloudFilesNativeApi()
                : null;
            IWindowsCloudFilesAdapter cloudFiles = cloudFilesAdapter
                ?? new WindowsCloudFilesAdapter(nativeApi: nativeApi, diagnostics: diagnostics);
            byte[] expectedContent = Encoding.UTF8.GetBytes(SmokeContentText);
            string expectedText = Encoding.UTF8.GetString(expectedContent);
            string expectedHash = Convert.ToHexStringLower(SHA256.HashData(expectedContent));
            SyncPairSettings syncPair = CreateSyncPair(rootPath);
            RemoteFilePlaceholderRequest placeholderRequest = CreatePlaceholderRequest(
                syncPair,
                RelativePlaceholderPath,
                expectedContent.LongLength,
                expectedHash);
            var contentProvider = new StaticSmokeContentProvider(expectedContent);
            IWindowsCloudFilesCallbackHandler callbackHandler = nativeApi is null
                ? new NoopCloudFilesCallbackHandler()
                : new WindowsCloudFilesHydrationCoordinator(
                    contentProvider,
                    nativeApi,
                    Path.Combine(paths.DataDirectory, "vfs-smoke-temp"),
                    diagnostics);
            Func<string, CancellationToken, Task<string>> reader = readAllTextAsync ?? File.ReadAllTextAsync;
            WindowsCloudFilesConnection? connection = null;
            int failures = 0;

            try
            {
                string placeholderPath = Path.Combine(rootPath, RelativePlaceholderPath);
                if (!reconnectExisting)
                {
                    TryUnregisterExistingRoot(cloudFiles, syncPair, output);
                    PrepareRoot(rootPath);
                    await output.WriteLineAsync(FormatCheck(true, "Isolated QA root prepared.") + " root=" + rootPath)
                        .ConfigureAwait(false);

                    RemoteFilePlaceholderResult placeholder = cloudFiles.CreateFilePlaceholder(placeholderRequest);
                    if (contentProvider.DownloadCount == 0)
                    {
                        await output.WriteLineAsync(
                            FormatCheck(true, "Placeholder creation did not download remote content.")
                            + " identityBytes=" + (placeholder.PlaceholderIdentity?.Length ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture))
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        failures++;
                        await output.WriteLineAsync(FormatCheck(false, "Placeholder creation unexpectedly downloaded remote content."))
                            .ConfigureAwait(false);
                    }
                }

                if (File.Exists(placeholderPath))
                {
                    await output.WriteLineAsync(
                        FormatCheck(true, reconnectExisting
                            ? "Existing remote-only placeholder is available before reconnect hydration."
                            : "Remote-only placeholder exists before hydration.")
                        + " path=" + placeholderPath
                        + ", attributes=" + FormatAttributes(File.GetAttributes(placeholderPath))
                        + ", downloads=" + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        .ConfigureAwait(false);
                }
                else
                {
                    failures++;
                    await output.WriteLineAsync(FormatCheck(false, "Remote-only placeholder file was not created."))
                        .ConfigureAwait(false);
                }

                connection = cloudFiles.ConnectSyncRoot(syncPair, callbackHandler);
                await output.WriteLineAsync(
                    FormatCheck(true, "Cloud Files sync root connected only under the isolated QA root.")
                    + " root=" + connection.LocalRootPath)
                    .ConfigureAwait(false);

                if (startupOptions.WindowsVirtualFilesSmokeHoldAfterPlaceholder > TimeSpan.Zero)
                {
                    await output.WriteLineAsync(
                        "Holding after remote-only placeholder creation for "
                        + startupOptions.WindowsVirtualFilesSmokeHoldAfterPlaceholder.TotalSeconds.ToString(
                            "0.###",
                            System.Globalization.CultureInfo.InvariantCulture)
                        + " seconds; inspect "
                        + placeholderPath
                        + " before hydration starts.")
                        .ConfigureAwait(false);
                    await Task
                        .Delay(startupOptions.WindowsVirtualFilesSmokeHoldAfterPlaceholder, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (leaveRegistered)
                {
                    await output.WriteLineAsync(
                        FormatCheck(true, "Cloud Files sync root left registered for process restart smoke.")
                        + " root=" + rootPath)
                        .ConfigureAwait(false);
                }
                else
                {
                    string hydratedText = await reader(placeholderPath, cancellationToken).ConfigureAwait(false);
                    byte[] hydratedBytes = Encoding.UTF8.GetBytes(hydratedText);
                    string hydratedHash = Convert.ToHexStringLower(SHA256.HashData(hydratedBytes));
                    if (string.Equals(hydratedText, expectedText, StringComparison.Ordinal)
                        && string.Equals(hydratedHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        await output.WriteLineAsync(
                            FormatCheck(true, "Opening the placeholder hydrated exact remote content.")
                            + " sha256=" + hydratedHash
                            + ", downloads=" + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        failures++;
                        await output.WriteLineAsync(
                            FormatCheck(false, "Hydrated content did not match expected remote content.")
                            + " expectedSha256=" + expectedHash
                            + ", actualSha256=" + hydratedHash)
                            .ConfigureAwait(false);
                    }

                    if (nativeApi is not null && contentProvider.DownloadCount == 0)
                    {
                        failures++;
                        await output.WriteLineAsync(FormatCheck(false, "Opening the placeholder did not trigger a Cloud Files fetch callback."))
                            .ConfigureAwait(false);
                    }

                    if (nativeApi is not null)
                    {
                        int downloadsBeforeDehydrate = contentProvider.DownloadCount;
                        nativeApi.DehydratePlaceholder(placeholderPath);
                        FileAttributes dehydratedAttributes = File.GetAttributes(placeholderPath);
                        if (HasRecallOnDataAccess(dehydratedAttributes)
                            && contentProvider.DownloadCount == downloadsBeforeDehydrate)
                        {
                            await output.WriteLineAsync(
                                FormatCheck(true, "Dehydrating the hydrated placeholder freed local content without remote transfer.")
                                + " attributes=" + FormatAttributes(dehydratedAttributes)
                                + ", downloads=" + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            failures++;
                            await output.WriteLineAsync(
                                FormatCheck(false, "Dehydrating the hydrated placeholder did not return it to online-only state.")
                                + " attributes=" + FormatAttributes(dehydratedAttributes)
                                + ", downloadsBefore="
                                + downloadsBeforeDehydrate.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                + ", downloadsAfter="
                                + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                                .ConfigureAwait(false);
                        }

                        connection.Dispose();
                        connection = null;
                        int downloadsBeforeReconnect = contentProvider.DownloadCount;
                        await output.WriteLineAsync("Disconnected Cloud Files sync root before reconnect smoke.").ConfigureAwait(false);

                        connection = cloudFiles.ConnectSyncRoot(syncPair, callbackHandler);
                        await output.WriteLineAsync(
                            FormatCheck(true, "Cloud Files sync root reconnected after provider restart simulation.")
                            + " root=" + connection.LocalRootPath)
                            .ConfigureAwait(false);

                        string rehydratedText = await reader(placeholderPath, cancellationToken).ConfigureAwait(false);
                        string rehydratedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rehydratedText)));
                        if (string.Equals(rehydratedText, expectedText, StringComparison.Ordinal)
                            && string.Equals(rehydratedHash, expectedHash, StringComparison.OrdinalIgnoreCase)
                            && contentProvider.DownloadCount == downloadsBeforeReconnect + 1)
                        {
                            await output.WriteLineAsync(
                                FormatCheck(true, "Reconnected Cloud Files callbacks hydrated the placeholder without duplicate registration.")
                                + " sha256=" + rehydratedHash
                                + ", downloads=" + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            failures++;
                            await output.WriteLineAsync(
                                FormatCheck(false, "Reconnected Cloud Files callbacks did not hydrate the placeholder correctly.")
                                + " expectedSha256=" + expectedHash
                                + ", actualSha256=" + rehydratedHash
                                + ", downloadsBeforeReconnect="
                                + downloadsBeforeReconnect.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                + ", downloadsAfterReconnect="
                                + contentProvider.DownloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                                .ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures++;
                await output.WriteLineAsync(
                    FormatCheck(false, exception.GetType().Name + ": " + CleanSingleLine(exception.Message)))
                    .ConfigureAwait(false);
            }
            finally
            {
                connection?.Dispose();
                if (!leaveRegistered)
                {
                    failures += TryUnregisterSmokeRoot(cloudFiles, syncPair, output);
                }
            }

            foreach (WindowsCloudFilesDiagnosticEvent item in diagnostics.Snapshot())
            {
                await output.WriteLineAsync(
                    "Diagnostic: "
                    + item.Operation
                    + " "
                    + item.Status
                    + " "
                    + CleanSingleLine(item.Details))
                    .ConfigureAwait(false);
            }

            await output.WriteLineAsync(failures == 0 ? "Result: passed" : "Result: failed").ConfigureAwait(false);
            return failures == 0 ? 0 : 1;
        }

        private static string ResolveSmokeRoot(string? configuredRoot)
        {
            return string.IsNullOrWhiteSpace(configuredRoot)
                ? Path.GetFullPath(DefaultSmokeRoot)
                : Path.GetFullPath(configuredRoot.Trim());
        }

        private static string? ValidateSmokeRoot(string rootPath)
        {
            if (!Path.IsPathFullyQualified(rootPath))
            {
                return "Windows virtual-files smoke root must be an absolute path under " + AllowedSmokeRoot + @"\...";
            }

            string allowedRoot = Path.GetFullPath(AllowedSmokeRoot);
            StringComparison comparison = StringComparison.OrdinalIgnoreCase;
            string normalizedRoot = NormalizeFullPath(rootPath);
            string normalizedAllowed = NormalizeFullPath(allowedRoot);
            if (string.Equals(normalizedRoot, normalizedAllowed, comparison)
                || !normalizedRoot.StartsWith(EnsureTrailingSeparator(normalizedAllowed), comparison))
            {
                return "Windows virtual-files smoke refuses to touch paths outside " + AllowedSmokeRoot + @"\...";
            }

            return null;
        }

        private static void PrepareRoot(string rootPath)
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }

            Directory.CreateDirectory(rootPath);
        }

        private static void TryUnregisterExistingRoot(
            IWindowsCloudFilesAdapter cloudFiles,
            SyncPairSettings syncPair,
            TextWriter output)
        {
            try
            {
                cloudFiles.UnregisterSyncRoot(syncPair);
                output.WriteLine("Info: previous Cloud Files registration was unregistered before smoke.");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                output.WriteLine("Info: no previous Cloud Files registration cleanup was confirmed: " + CleanSingleLine(exception.Message));
            }
        }

        private static int TryUnregisterSmokeRoot(
            IWindowsCloudFilesAdapter cloudFiles,
            SyncPairSettings syncPair,
            TextWriter output)
        {
            try
            {
                cloudFiles.UnregisterSyncRoot(syncPair);
                output.WriteLine(FormatCheck(true, "Cloud Files sync root unregistered after smoke.") + " root=" + syncPair.LocalRootPath);
                return 0;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                output.WriteLine(
                    FormatCheck(false, "Cloud Files sync root cleanup failed.")
                    + " "
                    + CleanSingleLine(exception.Message));
                return 1;
            }
        }

        private static SyncPairSettings CreateSyncPair(string rootPath)
        {
            return new SyncPairSettings
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                DisplayName = "Cotton Sync VFS smoke",
                LocalRootPath = rootPath,
                RemoteDisplayPath = "/CodexSyncQa/WindowsVirtualFilesSmoke",
                RemoteRootNodeId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Mode = SyncPairMode.WindowsVirtualFiles,
                IsEnabled = true,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            };
        }

        private static RemoteFilePlaceholderRequest CreatePlaceholderRequest(
            SyncPairSettings syncPair,
            string relativePath,
            long sizeBytes,
            string contentHash)
        {
            return new RemoteFilePlaceholderRequest(
                syncPair.Id.ToString("D"),
                syncPair.LocalRootPath,
                syncPair.RemoteRootNodeId,
                relativePath,
                new NodeFileManifestDto
                {
                    Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                    NodeId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                    FileManifestId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
                    OriginalNodeFileId = Guid.Parse("66666666-6666-6666-6666-666666666666"),
                    OwnerId = Guid.Parse("77777777-7777-7777-7777-777777777777"),
                    Name = Path.GetFileName(relativePath),
                    ContentType = "text/plain",
                    SizeBytes = sizeBytes,
                    ContentHash = contentHash,
                    ETag = "vfs-smoke-etag",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    Metadata = new Dictionary<string, string> { ["relativePath"] = relativePath },
                });
        }

        private static string FormatCheck(bool passed, string label)
        {
            return (passed ? "PASS: " : "FAIL: ") + label;
        }

        private static string NormalizeFullPath(string path)
        {
            string fullPath = Path.GetFullPath(path);
            string? root = Path.GetPathRoot(fullPath);
            if (!string.IsNullOrEmpty(root) && string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
            {
                return root;
            }

            return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string EnsureTrailingSeparator(string path)
        {
            return path.EndsWith(Path.DirectorySeparatorChar)
                ? path
                : path + Path.DirectorySeparatorChar;
        }

        private static string CleanSingleLine(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return "Operation could not be completed.";
            }

            return message
                .Replace(Environment.NewLine, " ", StringComparison.Ordinal)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
        }

        private static string FormatAttributes(FileAttributes attributes)
        {
            const int RecallOnOpen = 0x00040000;
            const int Pinned = 0x00080000;
            const int Unpinned = 0x00100000;
            const int RecallOnDataAccess = 0x00400000;

            var names = new List<string>();
            int raw = (int)attributes;
            foreach (FileAttributes known in Enum.GetValues<FileAttributes>())
            {
                if ((int)known == 0 || known == FileAttributes.Normal)
                {
                    continue;
                }

                if ((attributes & known) == known)
                {
                    names.Add(known.ToString());
                    raw &= ~(int)known;
                }
            }

            AddKnownCloudFilesAttribute(raw, RecallOnOpen, "RecallOnOpen", names, out raw);
            AddKnownCloudFilesAttribute(raw, Pinned, "Pinned", names, out raw);
            AddKnownCloudFilesAttribute(raw, Unpinned, "Unpinned", names, out raw);
            AddKnownCloudFilesAttribute(raw, RecallOnDataAccess, "RecallOnDataAccess", names, out raw);
            if (names.Count == 0)
            {
                names.Add(FileAttributes.Normal.ToString());
            }

            if (raw != 0)
            {
                names.Add("0x" + raw.ToString("X", System.Globalization.CultureInfo.InvariantCulture));
            }

            return string.Join("|", names)
                + " (raw=0x"
                + ((int)attributes).ToString("X", System.Globalization.CultureInfo.InvariantCulture)
                + ")";
        }

        private static bool HasRecallOnDataAccess(FileAttributes attributes)
        {
            const int RecallOnDataAccess = 0x00400000;
            return (((int)attributes) & RecallOnDataAccess) == RecallOnDataAccess;
        }

        private static void AddKnownCloudFilesAttribute(
            int raw,
            int flag,
            string name,
            List<string> names,
            out int remaining)
        {
            remaining = raw;
            if ((raw & flag) == flag)
            {
                names.Add(name);
                remaining &= ~flag;
            }
        }

        private sealed class StaticSmokeContentProvider : IWindowsCloudFilesRemoteContentProvider
        {
            private readonly byte[] _content;

            public StaticSmokeContentProvider(byte[] content)
            {
                _content = content;
            }

            public int DownloadCount { get; private set; }

            public async Task DownloadAsync(
                WindowsCloudFilesPlaceholderIdentity identity,
                Stream destination,
                IProgress<SyncTransferProgress>? transferProgress = null,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(identity);
                ArgumentNullException.ThrowIfNull(destination);
                DownloadCount++;
                transferProgress?.Report(new SyncTransferProgress(
                    SyncTransferDirection.Download,
                    identity.RelativePath,
                    0,
                    _content.LongLength,
                    isCompleted: false));
                await destination.WriteAsync(_content, cancellationToken).ConfigureAwait(false);
                transferProgress?.Report(new SyncTransferProgress(
                    SyncTransferDirection.Download,
                    identity.RelativePath,
                    _content.LongLength,
                    _content.LongLength,
                    isCompleted: true));
                destination.Position = 0;
            }
        }

        private sealed class NoopCloudFilesCallbackHandler : IWindowsCloudFilesCallbackHandler
        {
            public Task HandleFetchDataAsync(
                WindowsCloudFilesFetchDataRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public void CancelFetchData(WindowsCloudFilesCancelFetchDataRequest request)
            {
            }

            public Task HandleDehydrateAsync(
                WindowsCloudFilesDehydrateRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public void NotifyDehydrateCompleted(WindowsCloudFilesDehydrateCompletionNotification notification)
            {
            }
        }
    }
}
