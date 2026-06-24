// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using System.Net.Sockets;

namespace Cotton.Sync.Desktop.Diagnostics
{
    internal static class DesktopUnhandledExceptionReporter
    {
        private static int s_isInstalled;

        public static void Install()
        {
            if (Interlocked.Exchange(ref s_isInstalled, 1) == 1)
            {
                return;
            }

            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        internal static string FormatUnhandledException(object? exceptionObject, bool isTerminating)
        {
            string details = exceptionObject switch
            {
                Exception exception => exception.ToString(),
                null => "No exception object was provided.",
                _ => exceptionObject.ToString() ?? exceptionObject.GetType().FullName ?? "Unknown exception object.",
            };
            return DesktopSecretRedactor.Redact(
                "Unhandled desktop exception captured. Terminating: "
                + isTerminating
                + Environment.NewLine
                + details);
        }

        internal static string FormatUnobservedTaskException(AggregateException exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            return DesktopSecretRedactor.Redact(
                "Unobserved desktop task exception captured."
                + Environment.NewLine
                + exception);
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
        {
            Trace.TraceError(FormatUnhandledException(args.ExceptionObject, args.IsTerminating));
        }

        private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            ReportUnobservedTaskException(args);
        }

        internal static void ReportUnobservedTaskException(UnobservedTaskExceptionEventArgs args)
        {
            ArgumentNullException.ThrowIfNull(args);
            if (IsExpectedDesktopSocketCleanupException(args.Exception))
            {
                args.SetObserved();
                return;
            }

            Trace.TraceError(FormatUnobservedTaskException(args.Exception));
            args.SetObserved();
        }

        internal static bool IsExpectedDesktopSocketCleanupException(AggregateException exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            IReadOnlyCollection<Exception> innerExceptions = exception.Flatten().InnerExceptions;
            return innerExceptions.Count > 0
                && innerExceptions.All(static inner => inner is SocketException
                {
                    SocketErrorCode: SocketError.OperationAborted,
                });
        }
    }
}
