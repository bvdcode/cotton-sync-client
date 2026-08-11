// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.App.Auth;

namespace Cotton.Sync.Cli
{
    internal static class SyncCliErrorWriter
    {
        public static async Task WriteBrowserSignInAsync(
            TextWriter error,
            AppCodeBrowserSignInException exception)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(exception.Error))
            {
                await error.WriteLineAsync("Error: " + exception.Error).ConfigureAwait(false);
            }
        }

        public static async Task WriteCommandFailureAsync(
            TextWriter error,
            string message,
            Exception exception)
        {
            await error.WriteLineAsync(message).ConfigureAwait(false);
            await error.WriteLineAsync(
                    "Error: " + exception.GetType().Name + ": " + CleanSingleLine(exception.Message))
                .ConfigureAwait(false);
        }

        private static string CleanSingleLine(string? message)
        {
            return (message ?? string.Empty)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
        }
    }
}
