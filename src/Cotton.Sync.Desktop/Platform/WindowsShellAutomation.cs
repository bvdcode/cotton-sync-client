// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Cotton.Sync.Desktop.Platform
{
    [SupportedOSPlatform("windows")]
    internal static class WindowsShellAutomation
    {
        private const uint InProcessServerContext = 1;
        private static readonly Guid ShellApplicationClassId = new("13709620-C279-11CE-A49E-444553540000");
        private static readonly Guid ShellDispatchInterfaceId = new("286E6F1B-7113-4355-9562-96B7E9D64C54");

        public static string? ReadStringProperty(string filePath, string propertyName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
            return WithShellItem(
                filePath,
                null,
                item => item.ExtendedProperty(propertyName) as string);
        }

        public static WindowsShellVerbInvocationResult InvokeVerb(
            string filePath,
            Func<string, bool> matchesVerb)
        {
            ArgumentNullException.ThrowIfNull(matchesVerb);
            WindowsShellVerbInvocationResult missingResult = new(false, null, []);
            return WithShellItem(
                filePath,
                missingResult,
                item => InvokeVerb(item, matchesVerb));
        }

        private static WindowsShellVerbInvocationResult InvokeVerb(
            IWindowsShellFolderItem item,
            Func<string, bool> matchesVerb)
        {
            IWindowsShellFolderItemVerbs? verbs = null;
            List<string> names = [];
            try
            {
                verbs = item.Verbs();
                for (int index = 0; index < verbs.Count; index++)
                {
                    IWindowsShellFolderItemVerb? verb = null;
                    try
                    {
                        verb = verbs.Item(index);
                        if (verb is null)
                        {
                            continue;
                        }

                        string name = CleanVerbName(verb.Name);
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            names.Add(name);
                        }

                        if (matchesVerb(name))
                        {
                            verb.DoIt();
                            return new WindowsShellVerbInvocationResult(true, name, names);
                        }
                    }
                    finally
                    {
                        ReleaseComObject(verb);
                    }
                }

                return new WindowsShellVerbInvocationResult(false, null, names);
            }
            finally
            {
                ReleaseComObject(verbs);
            }
        }

        private static TResult WithShellItem<TResult>(
            string filePath,
            TResult missingResult,
            Func<IWindowsShellFolderItem, TResult> action)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            ArgumentNullException.ThrowIfNull(action);
            string fullPath = Path.GetFullPath(filePath);
            string? directoryPath = Path.GetDirectoryName(fullPath);
            string fileName = Path.GetFileName(fullPath);
            if (string.IsNullOrWhiteSpace(directoryPath) || string.IsNullOrWhiteSpace(fileName))
            {
                return missingResult;
            }

            IWindowsShellDispatch? shell = null;
            IWindowsShellFolder? folder = null;
            IWindowsShellFolderItem? item = null;
            try
            {
                shell = CreateShellDispatch();
                folder = shell.NameSpace(directoryPath);
                if (folder is null)
                {
                    return missingResult;
                }

                item = folder.ParseName(fileName);
                return item is null ? missingResult : action(item);
            }
            finally
            {
                ReleaseComObject(item);
                ReleaseComObject(folder);
                ReleaseComObject(shell);
            }
        }

        private static IWindowsShellDispatch CreateShellDispatch()
        {
            Guid classId = ShellApplicationClassId;
            Guid interfaceId = ShellDispatchInterfaceId;
            int result = CoCreateInstance(
                ref classId,
                IntPtr.Zero,
                InProcessServerContext,
                ref interfaceId,
                out IWindowsShellDispatch? shell);
            Marshal.ThrowExceptionForHR(result);
            return shell ?? throw new InvalidOperationException("Windows Shell automation could not be created.");
        }

        private static string CleanVerbName(string? value)
        {
            return (value ?? string.Empty)
                .Replace("&", string.Empty, StringComparison.Ordinal)
                .Trim();
        }

        private static void ReleaseComObject(object? value)
        {
            if (value is not null && Marshal.IsComObject(value))
            {
                Marshal.FinalReleaseComObject(value);
            }
        }

        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int CoCreateInstance(
            [In] ref Guid classId,
            IntPtr outerUnknown,
            uint context,
            [In] ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out IWindowsShellDispatch? shell);
    }
}
