// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Preferences;
using Cotton.Sync.Desktop.Platform;

namespace Cotton.Sync.Desktop.Shell
{
    internal class VisualSmokeSnapshotFactory(DesktopVisualSmokeScenario scenario)
    {
        public DesktopShellSnapshot CreateShellSnapshot(DateTime syncedAt)
        {
            bool isSignedIn = scenario is not DesktopVisualSmokeScenario.Connecting
                and not DesktopVisualSmokeScenario.SignInError;
            IReadOnlyList<DesktopSyncPairSnapshot> pairs = CreatePairs(syncedAt);
            string root = Path.Combine(Path.GetTempPath(), "cotton-sync-visual-smoke");
            return new DesktopShellSnapshot(
                new Uri("https://app.cottoncloud.dev/"),
                isSignedIn ? "qa@cottoncloud.dev" : "Signed out",
                "qa@cottoncloud.dev",
                true,
                true,
                AppThemeMode.Dark,
                new DesktopDataPathSnapshot(
                    root,
                    Path.Combine(root, "sync-app.db"),
                    Path.Combine(root, "sync-state.db"),
                    Path.Combine(root, "tokens.json")),
                DesktopPlatformCapabilities.CreateSnapshot(),
                isSignedIn,
                pairs);
        }

        public DesktopRemoteFolderListSnapshot ListRemoteFolders(string remotePath)
        {
            string normalizedRemotePath = NormalizeRemotePath(remotePath);
            if (scenario == DesktopVisualSmokeScenario.AddFolderManyRemoteFolders)
            {
                if (normalizedRemotePath != "/")
                {
                    return new DesktopRemoteFolderListSnapshot(normalizedRemotePath, []);
                }

                IReadOnlyList<DesktopRemoteFolderSnapshot> manyFolders = Enumerable.Range(1, 250)
                    .Select(index => new DesktopRemoteFolderSnapshot(
                        Guid.CreateVersion7(),
                        "Project archive " + index.ToString("000", System.Globalization.CultureInfo.InvariantCulture),
                        "/Project archive " + index.ToString("000", System.Globalization.CultureInfo.InvariantCulture)))
                    .ToList();
                return new DesktopRemoteFolderListSnapshot("/", manyFolders);
            }

            if (normalizedRemotePath != "/")
            {
                return new DesktopRemoteFolderListSnapshot(normalizedRemotePath, []);
            }

            IReadOnlyList<DesktopRemoteFolderSnapshot> folders =
            [
                new DesktopRemoteFolderSnapshot(Guid.Parse("10a52979-ae72-42e6-8f05-c70b0a73cd20"), "Documents", "/Documents"),
                new DesktopRemoteFolderSnapshot(Guid.Parse("74b4732d-8d0b-4e39-b41b-99eb070c212f"), "Photos", "/Photos"),
                new DesktopRemoteFolderSnapshot(Guid.Parse("386f35fc-f1b7-492c-8fe0-c814144d1646"), "Projects", "/Projects"),
            ];
            return new DesktopRemoteFolderListSnapshot("/", folders);
        }

        public static DesktopSelfTestSnapshot CreateSelfTestSnapshot()
        {
            IReadOnlyList<DesktopSelfTestItemSnapshot> items =
            [
                new DesktopSelfTestItemSnapshot("Preferences database", true, "Writable"),
                new DesktopSelfTestItemSnapshot("Token storage", true, "Release-secure storage available"),
                new DesktopSelfTestItemSnapshot("Server identity", true, "Cotton Cloud"),
            ];
            return new DesktopSelfTestSnapshot(items);
        }

        private static string NormalizeRemotePath(string remotePath)
        {
            string normalized = string.IsNullOrWhiteSpace(remotePath)
                ? "/"
                : remotePath.Trim().Replace('\\', '/');
            if (!normalized.StartsWith('/'))
            {
                normalized = "/" + normalized;
            }

            return normalized.Length > 1 ? normalized.TrimEnd('/') : "/";
        }

        private static string CreateLocalPath(params string[] segments)
        {
            string root = Path.Combine(Path.GetTempPath(), "cotton-sync-visual-smoke");
            return segments.Aggregate(root, Path.Combine);
        }

        private IReadOnlyList<DesktopSyncPairSnapshot> CreatePairs(DateTime syncedAt)
        {
            return scenario is DesktopVisualSmokeScenario.Connecting
                or DesktopVisualSmokeScenario.SignInError
                or DesktopVisualSmokeScenario.AddFolder
                or DesktopVisualSmokeScenario.AddFolderManyRemoteFolders
                or DesktopVisualSmokeScenario.EmptyDashboard
                ? []
                : CreateDashboardPairs(syncedAt);
        }

        private IReadOnlyList<DesktopSyncPairSnapshot> CreateDashboardPairs(DateTime syncedAt)
        {
            return
            [
                new DesktopSyncPairSnapshot(
                    VisualSmokeFixtureIds.DocumentsPairId,
                    "Documents",
                    CreateLocalPath("Documents"),
                    "/Documents",
                    CreateDocumentsStatus(),
                    Guid.Parse("29f81b10-b9a8-4f1d-88b0-9bdc6861b4e6"),
                    syncedAt,
                    1842,
                    scenario == DesktopVisualSmokeScenario.Error
                        ? DesktopActionRequiredMessageResolver.MissingDesktopSyncChangesApiMessage
                        : null),
                new DesktopSyncPairSnapshot(
                    VisualSmokeFixtureIds.PhotosPairId,
                    "Camera uploads",
                    CreateLocalPath("Pictures", "Camera Uploads"),
                    "/Photos/Camera Uploads",
                    CreateCameraUploadsStatus(),
                    Guid.Parse("c88c7b48-66a3-49dc-aee3-dd7b28614f96"),
                    syncedAt.AddMinutes(-3),
                    1859),
            ];
        }

        private string CreateDocumentsStatus()
        {
            return scenario switch
            {
                DesktopVisualSmokeScenario.Error => "Error",
                DesktopVisualSmokeScenario.Progress => "Syncing",
                DesktopVisualSmokeScenario.ManySmallDownload => "Syncing",
                DesktopVisualSmokeScenario.HydrationProgress => "Syncing",
                DesktopVisualSmokeScenario.DehydrationProgress => "Syncing",
                DesktopVisualSmokeScenario.HighPressureStarting => "Syncing",
                DesktopVisualSmokeScenario.VirtualFilesSeeding => "Syncing",
                _ => "Idle",
            };
        }

        private string CreateCameraUploadsStatus()
        {
            return scenario == DesktopVisualSmokeScenario.Progress ? "Syncing" : "Idle";
        }
    }
}
