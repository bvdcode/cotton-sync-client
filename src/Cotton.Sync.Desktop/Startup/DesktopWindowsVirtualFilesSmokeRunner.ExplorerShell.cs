// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Files;
using Cotton.Auth;
using Cotton.Nodes;
using Cotton.Sdk.Auth;
using Cotton.Sdk.Nodes;
using Cotton.Sdk.Sync;
using Cotton.Sync.App.Activities;
using Cotton.Sync.App.Auth;
using Cotton.Sync.App.Continuous;
using Cotton.Sync.App.LocalChanges;
using Cotton.Sync.App.Platform;
using Cotton.Sync.App.Preferences;
using Cotton.Sync.App.Progress;
using Cotton.Sync.App.RemoteChanges;
using Cotton.Sync.App.Runners;
using Cotton.Sync.App.ShellIntegration;
using Cotton.Sync.App.Status;
using Cotton.Sync.App.Supervision;
using Cotton.Sync.App.SyncApplication;
using Cotton.Sync.App.SyncPairs;
using Cotton.Sync.Desktop.Auth;
using Cotton.Sync.Desktop.Composition;
using Cotton.Sync.Desktop.Diagnostics;
using Cotton.Sync.Desktop.Platform;
using Cotton.Sync.Desktop.Shell;
using Cotton.Sync.Local;
using Cotton.Sync.Remote;
using Cotton.Sync.State;
using Cotton.Sync.VirtualFiles;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Cotton.Sync.Desktop.Startup
{
    internal static partial class DesktopWindowsVirtualFilesSmokeRunner
    {
        private static async Task<int> VerifyExplorerShellSettledStatusAsync(
            TextWriter output,
            string itemPath,
            string label,
            CancellationToken cancellationToken)
        {
            try
            {
                ShellItemStatusSnapshot status = await ReadExplorerShellStatusAsync(itemPath, cancellationToken)
                    .ConfigureAwait(false);
                bool hasAvailability = status.Columns.Any(IsExplorerAvailabilityStatus);
                bool hasActiveStatus = status.Columns.Any(IsActiveExplorerShellStatusColumn);
                if (hasAvailability && !hasActiveStatus)
                {
                    await output.WriteLineAsync(
                        FormatCheck(true, "Explorer shell status settled for " + label + ".")
                        + " "
                        + status.Format())
                        .ConfigureAwait(false);
                    return 0;
                }

                await output.WriteLineAsync(
                    FormatCheck(false, "Explorer shell status is active or unreadable for " + label + ".")
                    + " "
                    + status.Format())
                    .ConfigureAwait(false);
                return 1;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await output.WriteLineAsync(
                    FormatCheck(false, "Explorer shell status could not be read for " + label + ".")
                    + " "
                    + CleanSingleLine(exception.Message))
                    .ConfigureAwait(false);
                return 1;
            }
        }

        private static async Task<ShellItemStatusSnapshot> ReadExplorerShellStatusAsync(
            string itemPath,
            CancellationToken cancellationToken)
        {
            if (!OperatingSystem.IsWindows())
            {
                return new ShellItemStatusSnapshot([]);
            }

            string output = await DesktopPowerShellFileReader.ReadAsync(
                "$ErrorActionPreference='Stop'; "
                + "$target=$env:COTTON_SYNC_EXTERNAL_READ_PATH; "
                + "$parent=[System.IO.Path]::GetDirectoryName($target); "
                + "$name=[System.IO.Path]::GetFileName($target); "
                + "$shell=New-Object -ComObject Shell.Application; "
                + "$folder=$shell.Namespace($parent); "
                + "if ($null -eq $folder) { throw 'Shell namespace was not available.' }; "
                + "$item=$folder.ParseName($name); "
                + "if ($null -eq $item) { throw 'Shell item was not available.' }; "
                + "for($index=0; $index -le 330; $index++) { "
                + "$header=[string]$folder.GetDetailsOf($null,$index); "
                + "$value=[string]$folder.GetDetailsOf($item,$index); "
                + "if ([string]::IsNullOrWhiteSpace($header) -and [string]::IsNullOrWhiteSpace($value)) { continue }; "
                + "$headerBytes=[System.Text.Encoding]::UTF8.GetBytes($header); "
                + "$valueBytes=[System.Text.Encoding]::UTF8.GetBytes($value); "
                + "$headerEncoded=[Convert]::ToBase64String($headerBytes); "
                + "$valueEncoded=[Convert]::ToBase64String($valueBytes); "
                + "'{0}|{1}|{2}' -f $index,$headerEncoded,$valueEncoded "
                + "}",
                itemPath,
                ExternalFileReadTimeout,
                cancellationToken)
                .ConfigureAwait(false);

            List<ShellStatusColumn> columns = [];
            string[] lines = output.Split(
                ["\r\n", "\n"],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (string line in lines)
            {
                string[] parts = line.Split('|', 3);
                if (parts.Length != 3
                    || !int.TryParse(
                        parts[0],
                        System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out int index))
                {
                    throw new InvalidOperationException("Explorer shell status helper returned an invalid row.");
                }

                columns.Add(new ShellStatusColumn(
                    index,
                    DecodeBase64Utf8(parts[1]),
                    DecodeBase64Utf8(parts[2])));
            }

            return new ShellItemStatusSnapshot(columns);
        }

        private static string DecodeBase64Utf8(string value)
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }

        private static bool IsActiveExplorerShellStatus(string value)
        {
            return ContainsShellStatusTerm(value, "sync")
                || ContainsShellStatusTerm(value, "pending")
                || ContainsShellStatusTerm(value, "error")
                || ContainsShellStatusTerm(value, "processing")
                || ContainsShellStatusTerm(value, "updating")
                || ContainsShellStatusTerm(value, "\u0441\u0438\u043d\u0445")
                || ContainsShellStatusTerm(value, "\u043e\u0436\u0438\u0434")
                || ContainsShellStatusTerm(value, "\u043e\u0448\u0438\u0431");
        }

        private static bool IsActiveExplorerShellStatusColumn(ShellStatusColumn column)
        {
            return IsShellStatusColumnName(column.Name)
                && IsActiveExplorerShellStatus(column.Value);
        }

        private static bool IsExplorerAvailabilityStatus(ShellStatusColumn column)
        {
            if (string.IsNullOrWhiteSpace(column.Value))
            {
                return false;
            }

            return IsAvailabilityColumnName(column.Name)
                || IsKnownAvailabilityValue(column.Value);
        }

        private static bool IsAvailabilityColumnName(string value)
        {
            return ContainsShellStatusTerm(value, "availability")
                || ContainsShellStatusTerm(value, "\u0434\u043e\u0441\u0442\u0443\u043f");
        }

        private static bool IsShellStatusColumnName(string value)
        {
            return IsAvailabilityColumnName(value)
                || ContainsShellStatusTerm(value, "status")
                || ContainsShellStatusTerm(value, "\u0441\u0442\u0430\u0442\u0443\u0441");
        }

        private static bool IsKnownAvailabilityValue(string value)
        {
            string normalized = value.Trim();
            return string.Equals(normalized, "Available", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "Available when online", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "Available on this device", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "Always available on this device", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "Online-only", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "Locally available", StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    normalized,
                    "\u0414\u043e\u0441\u0442\u0443\u043f\u043d\u043e",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    normalized,
                    "\u0414\u043e\u0441\u0442\u0443\u043f\u043d\u043e \u043f\u0440\u0438 \u043f\u043e\u0434\u043a\u043b\u044e\u0447\u0435\u043d\u0438\u0438 \u043a \u0418\u043d\u0442\u0435\u0440\u043d\u0435\u0442\u0443",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    normalized,
                    "\u0414\u043e\u0441\u0442\u0443\u043f\u043d\u043e \u043d\u0430 \u044d\u0442\u043e\u043c \u0443\u0441\u0442\u0440\u043e\u0439\u0441\u0442\u0432\u0435",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    normalized,
                    "\u0412\u0441\u0435\u0433\u0434\u0430 \u0434\u043e\u0441\u0442\u0443\u043f\u043d\u043e \u043d\u0430 \u044d\u0442\u043e\u043c \u0443\u0441\u0442\u0440\u043e\u0439\u0441\u0442\u0432\u0435",
                    StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsShellStatusTerm(string value, string term)
        {
            return value.Contains(term, StringComparison.OrdinalIgnoreCase);
        }

        private static Task<WindowsShellVerbInvocationResult> InvokeExplorerFreeUpSpaceAsync(
            string filePath,
            CancellationToken cancellationToken)
        {
            return InvokeExplorerVerbAsync(filePath, IsFreeUpSpaceVerb, cancellationToken);
        }

        private static Task<WindowsShellVerbInvocationResult> InvokeExplorerAlwaysKeepAsync(
            string filePath,
            CancellationToken cancellationToken)
        {
            return InvokeExplorerVerbAsync(filePath, IsAlwaysKeepVerb, cancellationToken);
        }

        private static Task<WindowsShellVerbInvocationResult> InvokeExplorerVerbAsync(
            string filePath,
            Func<string, bool> matchesVerb,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            ArgumentNullException.ThrowIfNull(matchesVerb);
            cancellationToken.ThrowIfCancellationRequested();
            TaskCompletionSource<WindowsShellVerbInvocationResult> completion = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Thread thread = new(() =>
            {
                try
                {
                    completion.TrySetResult(WindowsShellAutomation.InvokeVerb(filePath, matchesVerb));
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });
            if (OperatingSystem.IsWindows())
            {
                thread.SetApartmentState(ApartmentState.STA);
            }

            thread.IsBackground = true;
            thread.Start();
            cancellationToken.Register(
                static state => ((TaskCompletionSource<WindowsShellVerbInvocationResult>)state!).TrySetCanceled(),
                completion);
            return completion.Task;
        }

        private static bool IsFreeUpSpaceVerb(string value)
        {
            return value.Contains("Free up space", StringComparison.OrdinalIgnoreCase)
                || value.Contains("\u041e\u0441\u0432\u043e\u0431\u043e\u0434\u0438\u0442\u044c \u043c\u0435\u0441\u0442\u043e", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAlwaysKeepVerb(string value)
        {
            return value.Contains("Always keep on this device", StringComparison.OrdinalIgnoreCase)
                || value.Contains("\u0412\u0441\u0435\u0433\u0434\u0430 \u0445\u0440\u0430\u043d\u0438\u0442\u044c \u043d\u0430 \u044d\u0442\u043e\u043c \u0443\u0441\u0442\u0440\u043e\u0439\u0441\u0442\u0432\u0435", StringComparison.OrdinalIgnoreCase)
                || value.Contains("\u0425\u0440\u0430\u043d\u0438\u0442\u044c \u044d\u0442\u0438 \u0444\u0430\u0439\u043b\u044b \u043d\u0430 \u0443\u0441\u0442\u0440\u043e\u0439\u0441\u0442\u0432\u0435", StringComparison.OrdinalIgnoreCase);
        }


        private record ShellStatusColumn(
            int Index,
            string Name,
            string Value);

        private record ShellItemStatusSnapshot(IReadOnlyList<ShellStatusColumn> Columns)
        {
            public string Format()
            {
                return string.Join(
                    ";",
                    Columns.Where(static column => IsShellStatusDiagnosticColumn(column))
                        .Select(static column =>
                        column.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + "["
                        + (string.IsNullOrWhiteSpace(column.Name) ? "<empty>" : CleanSingleLine(column.Name))
                        + "]"
                        + "="
                        + (string.IsNullOrWhiteSpace(column.Value) ? "<empty>" : CleanSingleLine(column.Value))));
            }

            private static bool IsShellStatusDiagnosticColumn(ShellStatusColumn column)
            {
                return column.Index is 7 or 8 or 148 or 149 or 298 or 299 or 300 or 305 or 307 or 308
                    || IsAvailabilityColumnName(column.Name)
                    || IsShellStatusColumnName(column.Name)
                    || IsKnownAvailabilityValue(column.Value)
                    || IsActiveExplorerShellStatus(column.Value);
            }
        }
    }
}
