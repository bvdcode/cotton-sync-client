// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.Diagnostics;
using System.Diagnostics;
using System.Net.Sockets;

namespace Cotton.Sync.Desktop.Tests.Diagnostics
{
    public class DesktopUnhandledExceptionReporterTests
    {
        [Test]
        public void FormatUnhandledException_RedactsSecretsAndMarksTerminatingState()
        {
            var exception = new InvalidOperationException(
                "Failed with Authorization: Bearer secret-token and password=secret-password");

            string message = DesktopUnhandledExceptionReporter.FormatUnhandledException(exception, isTerminating: true);

            Assert.Multiple(() =>
            {
                Assert.That(message, Does.Contain("Unhandled desktop exception captured."));
                Assert.That(message, Does.Contain("Terminating: True"));
                Assert.That(message, Does.Contain(nameof(InvalidOperationException)));
                Assert.That(message, Does.Contain("Bearer [redacted]"));
                Assert.That(message, Does.Contain("password=[redacted]"));
                Assert.That(message, Does.Not.Contain("secret-token"));
                Assert.That(message, Does.Not.Contain("secret-password"));
            });
        }

        [Test]
        public void FormatUnobservedTaskException_RedactsSecretsAndKeepsExceptionContext()
        {
            var exception = new AggregateException(
                new InvalidOperationException("""{"accessToken":"secret-token","message":"failed"}"""));

            string message = DesktopUnhandledExceptionReporter.FormatUnobservedTaskException(exception);

            Assert.Multiple(() =>
            {
                Assert.That(message, Does.Contain("Unobserved desktop task exception captured."));
                Assert.That(message, Does.Contain(nameof(AggregateException)));
                Assert.That(message, Does.Contain("\"accessToken\":\"[redacted]\""));
                Assert.That(message, Does.Not.Contain("secret-token"));
            });
        }

        [Test]
        public void ReportUnobservedTaskException_LogsAndMarksExceptionObserved()
        {
            var exception = new AggregateException(
                new InvalidOperationException("""{"accessToken":"secret-token","message":"failed"}"""));
            var args = new UnobservedTaskExceptionEventArgs(exception);
            var listener = new CollectingTraceListener();
            Trace.Listeners.Add(listener);

            try
            {
                DesktopUnhandledExceptionReporter.ReportUnobservedTaskException(args);

                Assert.Multiple(() =>
                {
                    Assert.That(args.Observed, Is.True);
                    Assert.That(listener.Output, Does.Contain("Unobserved desktop task exception captured."));
                    Assert.That(listener.Output, Does.Contain("\"accessToken\":\"[redacted]\""));
                    Assert.That(listener.Output, Does.Not.Contain("secret-token"));
                });
            }
            finally
            {
                Trace.Listeners.Remove(listener);
            }
        }

        [Test]
        public void ReportUnobservedTaskException_SuppressesExpectedSocketCleanupAbort()
        {
            var exception = new AggregateException(new SocketException((int)SocketError.OperationAborted));
            var args = new UnobservedTaskExceptionEventArgs(exception);
            var listener = new CollectingTraceListener();
            Trace.Listeners.Add(listener);

            try
            {
                DesktopUnhandledExceptionReporter.ReportUnobservedTaskException(args);

                Assert.Multiple(() =>
                {
                    Assert.That(args.Observed, Is.True);
                    Assert.That(listener.Output, Is.Empty);
                    Assert.That(DesktopUnhandledExceptionReporter.IsExpectedDesktopSocketCleanupException(exception), Is.True);
                });
            }
            finally
            {
                Trace.Listeners.Remove(listener);
            }
        }

        [Test]
        public void ReportUnobservedTaskException_LogsMixedSocketCleanupAggregate()
        {
            var exception = new AggregateException(
                new SocketException((int)SocketError.OperationAborted),
                new InvalidOperationException("real failure"));
            var args = new UnobservedTaskExceptionEventArgs(exception);
            var listener = new CollectingTraceListener();
            Trace.Listeners.Add(listener);

            try
            {
                DesktopUnhandledExceptionReporter.ReportUnobservedTaskException(args);

                Assert.Multiple(() =>
                {
                    Assert.That(args.Observed, Is.True);
                    Assert.That(listener.Output, Does.Contain("Unobserved desktop task exception captured."));
                    Assert.That(listener.Output, Does.Contain("real failure"));
                    Assert.That(DesktopUnhandledExceptionReporter.IsExpectedDesktopSocketCleanupException(exception), Is.False);
                });
            }
            finally
            {
                Trace.Listeners.Remove(listener);
            }
        }

        private sealed class CollectingTraceListener : TraceListener
        {
            private readonly StringWriter _writer = new();

            public string Output => _writer.ToString();

            public override void Write(string? message)
            {
                _writer.Write(message);
            }

            public override void WriteLine(string? message)
            {
                _writer.WriteLine(message);
            }
        }
    }
}
