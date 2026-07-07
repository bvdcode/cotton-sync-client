// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;

namespace Cotton.Sync.Cli
{
    internal static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            return await RunWithTopLevelExceptionMappingAsync(
                    () => SyncCliCommandRunner.RunAsync(args, Console.Out, Console.Error),
                    Console.Error)
                .ConfigureAwait(false);
        }

        internal static async Task<int> RunWithTopLevelExceptionMappingAsync(
            Func<Task<int>> run,
            TextWriter error)
        {
            ArgumentNullException.ThrowIfNull(run);
            ArgumentNullException.ThrowIfNull(error);

            try
            {
                return await run().ConfigureAwait(false);
            }
            catch (OperationCanceledException exception)
            {
                Trace.TraceWarning(exception.ToString());
                await error.WriteLineAsync("Operation canceled.").ConfigureAwait(false);
                return 130;
            }
            catch (Exception exception)
            {
                Trace.TraceError(exception.ToString());
                await error
                    .WriteLineAsync("Unexpected error: " + exception.GetType().Name + ": " + exception.Message)
                    .ConfigureAwait(false);
                return 1;
            }
        }
    }
}
