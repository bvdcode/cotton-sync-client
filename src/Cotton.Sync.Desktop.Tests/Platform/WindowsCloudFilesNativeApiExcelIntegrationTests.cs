// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Platform;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Cotton.Sync.Desktop.Tests.Platform
{
    public partial class WindowsCloudFilesNativeApiIntegrationTests
    {
        [Test]
        [Explicit("Starts the installed Microsoft Excel application.")]
        public async Task FinalizeUploadedFile_AllowsExcelToKeepTheWorkbookOpen()
        {
            string? excelPath = FindExcelPath();
            if (excelPath is null)
            {
                Assert.Ignore("Microsoft Excel is not installed.");
                return;
            }

            string root = Path.Combine(
                Path.GetTempPath(),
                "cotton-cloud-files-open-excel-" + Guid.NewGuid().ToString("N"));
            string fileName = "Debts.xlsx";
            string filePath = Path.Combine(root, fileName);
            byte[] oldIdentity = Encoding.UTF8.GetBytes("old-version");
            byte[] updatedIdentity = Encoding.UTF8.GetBytes("updated-version");
            WindowsCloudFilesNativeApi nativeApi = new();
            Guid syncPairId = Guid.NewGuid();
            WindowsStorageProviderSyncRootRegistrar registrar = new(GetShellHelperPath());
            Directory.CreateDirectory(root);
            CreateWorkbook(filePath);
            registrar.Register(new WindowsStorageProviderSyncRootRegistration(
                syncPairId,
                root,
                "excel-integration-test",
                Path.Combine(AppContext.BaseDirectory, "Cotton.Sync.Desktop.exe")));
            nativeApi.RegisterSyncRoot(new WindowsCloudFilesNativeSyncRootRegistration(
                root,
                WindowsCloudFilesAdapter.ProviderName,
                "excel-integration-test",
                WindowsCloudFilesProviderMetadata.ProviderGuid,
                WindowsCloudFilesPlaceholderFactory.CreateSyncRootIdentity(syncPairId, Guid.NewGuid())));
            nativeApi.ConvertToPlaceholder(filePath, oldIdentity, isDirectory: false, markInSync: false);
            ProcessStartInfo startInfo = new(excelPath)
            {
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("/x");
            startInfo.ArgumentList.Add(filePath);
            using Process excel = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Microsoft Excel did not start.");

            try
            {
                await WaitForMainWindowAsync(excel);
                await Task.Delay(TimeSpan.FromSeconds(1));
                Assert.Throws<IOException>(() =>
                {
                    using FileStream exclusive = new(
                        filePath,
                        FileMode.Open,
                        FileAccess.ReadWrite,
                        FileShare.None);
                });
                await using FileStream readable = new(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                byte[] hash = await SHA256.HashDataAsync(readable);
                long sizeBytes = readable.Length;
                DateTime lastWriteUtc = File.GetLastWriteTimeUtc(filePath);
                WindowsCloudFilesUploadedFileFinalizationResult result = await nativeApi.FinalizeUploadedFileAsync(
                    new WindowsCloudFilesUploadedFileFinalizationRequest(
                        new WindowsCloudFilesNativePlaceholder(
                            root,
                            fileName,
                            updatedIdentity,
                            sizeBytes,
                            File.GetCreationTimeUtc(filePath),
                            lastWriteUtc),
                        Convert.ToHexStringLower(hash),
                        sizeBytes,
                        lastWriteUtc,
                        WindowsCloudFilesUploadedFileFinalizationMode.UpdateExistingPlaceholder));

                byte[] actualIdentity = nativeApi.GetPlaceholderIdentity(filePath);
                Assert.Multiple(() =>
                {
                    Assert.That(result.IsFinalized, Is.True);
                    Assert.That(actualIdentity, Is.EqualTo(updatedIdentity));
                    Assert.That(excel.HasExited, Is.False);
                });
            }
            finally
            {
                await StopProcessAsync(excel);
                nativeApi.UnregisterSyncRoot(root);
                registrar.Unregister(syncPairId, root);
                Directory.Delete(root, recursive: true);
            }
        }

        private static string GetShellHelperPath()
        {
            return Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "Cotton.Sync.WindowsShell",
                "bin",
                "Release",
                "net10.0-windows10.0.19041.0",
                "win-x64",
                "Cotton.Sync.WindowsShell.exe"));
        }

        private static string? FindExcelPath()
        {
            string[] candidates =
            [
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Microsoft Office",
                    "root",
                    "Office16",
                    "EXCEL.EXE"),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "Microsoft Office",
                    "root",
                    "Office16",
                    "EXCEL.EXE"),
            ];
            return candidates.FirstOrDefault(File.Exists);
        }

        private static async Task WaitForMainWindowAsync(Process process)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(20);
            while (!process.HasExited
                && process.MainWindowHandle == IntPtr.Zero
                && DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
                process.Refresh();
            }

            Assert.Multiple(() =>
            {
                Assert.That(process.HasExited, Is.False, "Microsoft Excel exited before opening the workbook.");
                Assert.That(process.MainWindowHandle, Is.Not.EqualTo(IntPtr.Zero), "Microsoft Excel did not open a window.");
            });
        }

        private static async Task StopProcessAsync(Process process)
        {
            if (process.HasExited)
            {
                return;
            }

            process.CloseMainWindow();
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (!process.HasExited && DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }

        private static void CreateWorkbook(string filePath)
        {
            using ZipArchive archive = ZipFile.Open(filePath, ZipArchiveMode.Create);
            WriteEntry(archive, "[Content_Types].xml", ContentTypesXml);
            WriteEntry(archive, "_rels/.rels", PackageRelationshipsXml);
            WriteEntry(archive, "xl/workbook.xml", WorkbookXml);
            WriteEntry(archive, "xl/_rels/workbook.xml.rels", WorkbookRelationshipsXml);
            WriteEntry(archive, "xl/worksheets/sheet1.xml", WorksheetXml);
        }

        private static void WriteEntry(ZipArchive archive, string path, string content)
        {
            ZipArchiveEntry entry = archive.CreateEntry(path);
            using Stream stream = entry.Open();
            using StreamWriter writer = new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(content);
        }

        private const string ContentTypesXml = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
              <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
            </Types>
            """;

        private const string PackageRelationshipsXml = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """;

        private const string WorkbookXml = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets>
            </workbook>
            """;

        private const string WorkbookRelationshipsXml = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
            </Relationships>
            """;

        private const string WorksheetXml = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>Debts</t></is></c></row></sheetData>
            </worksheet>
            """;
    }
}
