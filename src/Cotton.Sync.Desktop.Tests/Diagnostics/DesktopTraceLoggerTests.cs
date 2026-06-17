// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 Vadim Belov <https://belov.us>

using System.Diagnostics;
using Cotton.Sync.Desktop.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Cotton.Sync.Desktop.Tests.Diagnostics
{
    public class DesktopTraceLoggerTests
    {
        [Test]
        public void Format_IncludesLevelCategoryEventAndException()
        {
            var exception = new InvalidOperationException("sync failed");

            string formatted = DesktopTraceLogFormatter.Format(
                "Cotton.Sync.App.Runners.SyncPairRunner",
                LogLevel.Warning,
                new EventId(42, "Retry"),
                "Retrying sync",
                exception);

            Assert.Multiple(() =>
            {
                Assert.That(formatted, Does.Contain("Warning"));
                Assert.That(formatted, Does.Contain("[Cotton.Sync.App.Runners.SyncPairRunner]"));
                Assert.That(formatted, Does.Contain("event=42:Retry"));
                Assert.That(formatted, Does.Contain("Retrying sync"));
                Assert.That(formatted, Does.Contain("sync failed"));
            });
        }

        [Test]
        public void Log_WritesRedactedMessageToTrace()
        {
            var listener = new CollectingTraceListener();
            Trace.Listeners.Add(listener);
            try
            {
                ILogger logger = new DesktopTraceLogger("Cotton.Sync.Desktop.Tests");

                logger.LogError("Authorization: Bearer access-token");

                Assert.Multiple(() =>
                {
                    Assert.That(listener.Output, Does.Contain("Error"));
                    Assert.That(listener.Output, Does.Contain("Cotton.Sync.Desktop.Tests"));
                    Assert.That(listener.Output, Does.Contain("Bearer [redacted]"));
                    Assert.That(listener.Output, Does.Not.Contain("access-token"));
                });
            }
            finally
            {
                Trace.Listeners.Remove(listener);
            }
        }

        [Test]
        public void Log_DefaultMinimumLevelSkipsDebugMessages()
        {
            var listener = new CollectingTraceListener();
            Trace.Listeners.Add(listener);
            try
            {
                ILogger logger = new DesktopTraceLogger("Cotton.Sync.Desktop.Tests");

                logger.LogDebug("debug request flood");
                logger.LogInformation("sync started");

                Assert.Multiple(() =>
                {
                    Assert.That(listener.Output, Does.Not.Contain("debug request flood"));
                    Assert.That(listener.Output, Does.Contain("sync started"));
                });
            }
            finally
            {
                Trace.Listeners.Remove(listener);
            }
        }

        [Test]
        public void Log_DebugMinimumLevelKeepsDebugMessages()
        {
            var listener = new CollectingTraceListener();
            Trace.Listeners.Add(listener);
            try
            {
                ILogger logger = new DesktopTraceLogger("Cotton.Sync.Desktop.Tests", LogLevel.Debug);

                logger.LogDebug("diagnostic request trace");

                Assert.That(listener.Output, Does.Contain("diagnostic request trace"));
            }
            finally
            {
                Trace.Listeners.Remove(listener);
            }
        }

        [Test]
        public void Factory_UsesEnvironmentMinimumLevel()
        {
            string? previous = Environment.GetEnvironmentVariable(DesktopTraceLogLevel.EnvironmentVariableName);
            Environment.SetEnvironmentVariable(DesktopTraceLogLevel.EnvironmentVariableName, "Debug");
            var listener = new CollectingTraceListener();
            Trace.Listeners.Add(listener);
            try
            {
                using var factory = new DesktopTraceLoggerFactory();
                ILogger logger = factory.CreateLogger("Cotton.Sync.Desktop.Tests");

                logger.LogDebug("debug enabled from environment");

                Assert.That(listener.Output, Does.Contain("debug enabled from environment"));
            }
            finally
            {
                Trace.Listeners.Remove(listener);
                Environment.SetEnvironmentVariable(DesktopTraceLogLevel.EnvironmentVariableName, previous);
            }
        }

        private class CollectingTraceListener : TraceListener
        {
            public string Output { get; private set; } = string.Empty;

            public override void Write(string? message)
            {
                Output += message;
            }

            public override void WriteLine(string? message)
            {
                Output += message + Environment.NewLine;
            }
        }
    }
}
