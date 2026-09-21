// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Startup;
using System.Text;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsCloudFilesNativeApiIntegrationTests
    {
        [Test]
        [Explicit("Registers a temporary Windows Cloud Files sync root.")]
        public async Task ReconnectSyncRoot_PreservesDirectoryAndFileCloudIdentity()
        {
            string root = Path.Combine(Path.GetTempPath(), "cotton-cloud-files-reconnect-" + Guid.NewGuid().ToString("N"));
            Guid pairId = Guid.NewGuid();
            WindowsCloudFilesNativeApi nativeApi = new();
            WindowsStorageProviderSyncRootRegistrar registrar = new(GetShellHelperPath());
            WindowsStorageProviderSyncRootRegistration shellRegistration = new(
                pairId, root, "connection-integration-test",
                Path.Combine(AppContext.BaseDirectory, "Cotton.Sync.Desktop.exe"));
            WindowsCloudFilesNativeSyncRootRegistration nativeRegistration = new(
                root, WindowsCloudFilesAdapter.ProviderName, "connection-integration-test",
                WindowsCloudFilesProviderMetadata.ProviderGuid,
                WindowsCloudFilesPlaceholderFactory.CreateSyncRootIdentity(pairId, Guid.NewGuid()));
            Directory.CreateDirectory(root);
            registrar.Register(shellRegistration);
            nativeApi.RegisterSyncRoot(nativeRegistration);
            string directory = Path.Combine(root, "Documents");
            string file = Path.Combine(directory, "example.txt");
            byte[] directoryIdentity = Encoding.UTF8.GetBytes("directory-id");
            byte[] fileIdentity = Encoding.UTF8.GetBytes("file-id");
            DateTime timestamp = DateTime.UtcNow.AddMinutes(-1);
            try
            {
                using (WindowsCloudFilesConnection connection = nativeApi.ConnectSyncRoot(
                    new WindowsCloudFilesConnectionRequest(root, new NoopWindowsCloudFilesCallbackHandler())))
                {
                    nativeApi.CreatePlaceholder(new WindowsCloudFilesNativePlaceholder(
                        root, "Documents", directoryIdentity, 0, timestamp, timestamp, IsDirectory: true));
                    nativeApi.CreatePlaceholder(new WindowsCloudFilesNativePlaceholder(
                        directory, "example.txt", fileIdentity, 5, timestamp, timestamp));
                    nativeApi.SetInSyncState(directory);
                    await ReportStateAsync("connected");
                }

                await ReportStateAsync("disconnected");
                Assert.Multiple(() =>
                {
                    Assert.That(nativeApi.GetPlaceholderIdentity(directory), Is.EqualTo(directoryIdentity));
                    Assert.That(nativeApi.GetPlaceholderIdentity(file), Is.EqualTo(fileIdentity));
                });
                registrar.Register(shellRegistration);
                nativeApi.RegisterSyncRoot(nativeRegistration);
                using WindowsCloudFilesConnection reconnected = nativeApi.ConnectSyncRoot(
                    new WindowsCloudFilesConnectionRequest(root, new NoopWindowsCloudFilesCallbackHandler()));
                await ReportStateAsync("reconnected");
                AssertCloudIdentity();
            }
            finally
            {
                nativeApi.UnregisterSyncRoot(root);
                registrar.Unregister(pairId, root);
                Directory.Delete(root, recursive: true);
            }

            void AssertCloudIdentity()
            {
                Assert.Multiple(() =>
                {
                    Assert.That(nativeApi.GetPlaceholderIdentity(directory), Is.EqualTo(directoryIdentity));
                    Assert.That(nativeApi.GetPlaceholderIdentity(file), Is.EqualTo(fileIdentity));
                    Assert.That(File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint), Is.True);
                    Assert.That(File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint), Is.True);
                });
            }

            async Task ReportStateAsync(string phase)
            {
                foreach (string path in new[] { directory, file })
                {
                    string state = await DesktopPowerShellFileReader.ReadAsync(
                        "$target=$env:COTTON_SYNC_EXTERNAL_READ_PATH; "
                        + "$shell=New-Object -ComObject Shell.Application; "
                        + "$folder=$shell.Namespace([IO.Path]::GetDirectoryName($target)); "
                        + "$item=$folder.ParseName([IO.Path]::GetFileName($target)); "
                        + "$item.ExtendedProperty('System.StorageProviderState')",
                        path, TimeSpan.FromSeconds(10), CancellationToken.None);
                    TestContext.Out.WriteLine($"{phase} {Path.GetFileName(path)} attributes={File.GetAttributes(path)} state={nativeApi.GetPlaceholderState(path)} shell={state.Trim()}");
                    if (phase == "reconnected")
                    {
                        Assert.That(state.Trim(), Is.EqualTo("1"));
                    }
                }
            }
        }
    }
}
