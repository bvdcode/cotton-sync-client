// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.IO.Compression;
using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Diagnostics;

namespace Cotton.Sync.Desktop.Tests.Diagnostics
{
    public partial class DesktopDiagnosticsExporterTests
    {
        [Test]
        public async Task ExportAsync_IncludesCurrentAndRotatedCloudFilesLogs()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            await File.WriteAllTextAsync(paths.CloudFilesLogFilePath, "current cloud event");
            for (int index = 1; index <= DesktopLogRetention.RetainedFileCount; index++)
            {
                await File.WriteAllTextAsync(paths.CloudFilesLogFilePath + "." + index, "previous cloud event " + index);
            }

            DesktopDiagnosticsExporter exporter = new();
            string archivePath = await exporter.ExportAsync(paths, CreateBundle(paths));

            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            Assert.That(ReadEntry(archive, "logs/cloud-files.log"), Is.EqualTo("current cloud event"));
            for (int index = 1; index <= DesktopLogRetention.RetainedFileCount; index++)
            {
                Assert.That(ReadEntry(archive, "logs/cloud-files.log." + index), Is.EqualTo("previous cloud event " + index));
            }
        }

        [Test]
        public async Task ExportAsync_RemovesPrivatePathsAndSecretsFromCloudFilesLogs()
        {
            DesktopAppPaths paths = DesktopAppPaths.CreateForDataDirectory(_tempDirectory);
            await File.WriteAllTextAsync(paths.CloudFilesLogFilePath, paths.DataDirectory + " Authorization: Bearer private-token");
            await File.WriteAllTextAsync(paths.CloudFilesLogFilePath + ".1", paths.DataDirectory + " Authorization: Bearer previous-token");

            DesktopDiagnosticsExporter exporter = new();
            string archivePath = await exporter.ExportAsync(paths, CreateBundle(paths));

            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            foreach (string entry in new[] { "logs/cloud-files.log", "logs/cloud-files.log.1" })
            {
                string content = ReadEntry(archive, entry);
                Assert.Multiple(() =>
                {
                    Assert.That(content, Does.Not.Contain(paths.DataDirectory));
                    Assert.That(content, Does.Not.Contain("private-token"));
                    Assert.That(content, Does.Not.Contain("previous-token"));
                    Assert.That(content, Does.Contain("Bearer [redacted]"));
                });
            }
        }
    }
}
