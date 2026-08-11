// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Avalonia;
using Cotton.Sync.Desktop.Startup;

namespace Cotton.Sync.Desktop
{
    internal static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            return DesktopProgramRunner.Run(args, BuildAvaloniaApp);
        }

        public static AppBuilder BuildAvaloniaApp()
        {
            return AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
        }
    }
}
