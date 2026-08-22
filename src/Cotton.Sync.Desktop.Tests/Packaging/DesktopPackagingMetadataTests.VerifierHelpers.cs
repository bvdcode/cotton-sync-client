// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Cotton.Sync.Desktop.Platform;

namespace Cotton.Sync.Desktop.Tests.Packaging
{
    public partial class DesktopPackagingMetadataTests
    {
        private static (int ExitCode, string Output) RunVfsReleaseEvidenceVerifier(
            string evidenceDirectory,
            int? minimumVfsPlaceholderCount = null)
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = "pwsh",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(GetDesktopFilePath("Packaging/windows/verify-vfs-release-evidence.ps1"));
            startInfo.ArgumentList.Add("-EvidenceDirectory");
            startInfo.ArgumentList.Add(evidenceDirectory);
            if (minimumVfsPlaceholderCount is not null)
            {
                startInfo.ArgumentList.Add("-MinimumVfsPlaceholderCount");
                startInfo.ArgumentList.Add(minimumVfsPlaceholderCount.Value.ToString(CultureInfo.InvariantCulture));
            }

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("PowerShell verifier process did not start.");
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(milliseconds: 30000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("VFS release evidence verifier did not exit within 30 seconds.");
            }

            return (process.ExitCode, stdout + stderr);
        }

        private static (int ExitCode, string Output) RunVfsLogonEvidenceVerifier(string evidenceDirectory)
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = "pwsh",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(GetDesktopFilePath("Packaging/windows/verify-vfs-logon-evidence.ps1"));
            startInfo.ArgumentList.Add("-EvidenceDirectory");
            startInfo.ArgumentList.Add(evidenceDirectory);

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("PowerShell verifier process did not start.");
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(milliseconds: 30000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("VFS logon evidence verifier did not exit within 30 seconds.");
            }

            return (process.ExitCode, stdout + stderr);
        }

        private static void DeleteTestDirectory(string directory)
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        private static string NormalizeProfilePath(string? value)
        {
            return (value ?? string.Empty).Replace('\\', '/');
        }

        private static string NormalizePowerShellErrorOutput(string value)
        {
            string withoutAnsi = Regex.Replace(value, "\u001b\\[[0-9;]*m", string.Empty);
            string withoutLineMarkers = Regex.Replace(withoutAnsi, "\\s+\\|\\s+", " ");
            return Regex.Replace(withoutLineMarkers, "\\s+", " ");
        }

        private static void AssertCloudFilesImport(Type nativeApiType, string entryPoint)
        {
            MethodInfo? method = nativeApiType.GetMethod(entryPoint, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null, entryPoint + " must stay in the desktop Cloud Files native bridge.");
            DllImportAttribute? dllImport = method!.GetCustomAttribute<DllImportAttribute>();
            Assert.That(dllImport, Is.Not.Null, entryPoint + " must use the Windows Cloud Files API.");
            Assert.That(dllImport!.Value, Is.EqualTo("CldApi.dll"));
            Assert.That(dllImport.ExactSpelling, Is.True);
        }
    }
}
