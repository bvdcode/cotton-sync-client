// SPDX-License-Identifier: MIT
// Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

using Cotton.Sync.Desktop.ViewModels;

namespace Cotton.Sync.Desktop
{
    internal static class DesktopWindowSizingPolicy
    {
        private const double DashboardHeight = 540;
        private const double DashboardMinHeight = 520;
        private const double DashboardMinWidth = 388;
        private const double DashboardWidth = 400;
        private const double SetupServerHeight = 288;
        private const double SetupServerMinHeight = 280;
        private const double SetupSignInHeight = 452;
        private const double SetupSignInMinHeight = 440;
        private const double SetupMinWidth = 316;
        private const double SetupWidth = 336;
        private const double WindowFrameHeightAllowance = 48;

        public static DesktopWindowProfile ResolveProfile(ShellViewModel viewModel)
        {
            if (viewModel.IsDashboardVisible)
            {
                return DesktopWindowProfile.Dashboard;
            }

            return viewModel.IsSignInStepVisible
                ? DesktopWindowProfile.SetupSignIn
                : DesktopWindowProfile.SetupServer;
        }

        public static DesktopWindowProfileSettings GetSettings(DesktopWindowProfile profile)
        {
            return profile switch
            {
                DesktopWindowProfile.Dashboard => new DesktopWindowProfileSettings(
                    DashboardWidth,
                    DashboardHeight,
                    DashboardMinWidth,
                    DashboardMinHeight),
                DesktopWindowProfile.SetupSignIn => new DesktopWindowProfileSettings(
                    SetupWidth,
                    SetupSignInHeight,
                    SetupMinWidth,
                    SetupSignInMinHeight),
                DesktopWindowProfile.SetupServer => new DesktopWindowProfileSettings(
                    SetupWidth,
                    SetupServerHeight,
                    SetupMinWidth,
                    SetupServerMinHeight),
                _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
            };
        }

        public static (double Height, double MinHeight) CalculateFittedHeight(
            double desiredHeight,
            double minimumHeight,
            int workingAreaPixelHeight,
            double renderScaling)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(desiredHeight);
            if (minimumHeight <= 0 || minimumHeight > desiredHeight)
            {
                throw new ArgumentOutOfRangeException(nameof(minimumHeight));
            }

            if (workingAreaPixelHeight <= 0 || renderScaling <= 0)
            {
                return (desiredHeight, minimumHeight);
            }

            double availableHeight = Math.Max(
                1,
                (workingAreaPixelHeight / renderScaling) - WindowFrameHeightAllowance);
            return (
                Math.Min(desiredHeight, availableHeight),
                Math.Min(minimumHeight, availableHeight));
        }
    }
}
